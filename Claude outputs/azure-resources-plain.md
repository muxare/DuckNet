## Planned Azure resources: chosen versus alternatives

This section explains what DuckNet will run on in Azure, and why each piece was picked over the obvious alternatives. The Bicep files that describe these resources live in [`infra/bicep/`](../infra/bicep/). In Step 12b we only check that they compile; in Step 12c we actually create the resources.

**The short version.** Each Center becomes its own small container app. Facts flow through two pipes: a durable log (Event Hubs) that keeps every event for replay, and a live bus (Service Bus) that delivers events to each Center right now. Each Center gets its own Postgres database on one shared server. Apps identify themselves with managed identities instead of passwords, pull images from a private registry, and send traces to Application Insights. Everything is described in Bicep and rolled out from GitHub Actions.

**Glossary of the terms used below**

| Term | Plain meaning |
|------|---------------|
| **Center** | One DuckNet service (Telemetry, Alarm, Dashboard, Billing). Each owns its own data and only talks to others through events. |
| **Container / image** | A packaged copy of a Center with everything it needs to run. The image is the file; the container is that file running. |
| **Replica** | One running copy of a Center. Two replicas = two copies sharing the load. |
| **Scale to zero** | Shut down all replicas when idle, start one on demand. Saves money, but the first request waits for a "cold start". |
| **KEDA** | An autoscaler that watches a queue and starts more replicas when the queue gets long. |
| **Managed service** | Azure runs and patches it for you (disks, upgrades, backups). You just use it. |
| **Event log / append-only log** | A list of events that only grows. You can re-read it from the start to rebuild a Center's data. |
| **Partition / partition key** | The log is split into lanes. The key (here `duckId`) decides which lane an event lands in. Events in the same lane stay in order. |
| **Consumer group** | A named reader of the log with its own bookmark, so two Centers can read the same events independently. |
| **Topic / subscription** | A bus where one message is copied to every subscriber. Each Center has its own subscription, so all three receive the same fact. |
| **Dead-letter queue (DLQ)** | A holding pen for messages that failed delivery too many times, so they can be inspected instead of lost. |
| **Inbox / outbox** | Tables in a Center's own database. The inbox remembers which events were already handled (so duplicates are ignored). The outbox stores events to send in the same transaction as the data change, so they are never lost. |
| **Sequencer** | Code that puts events back in order per duck when the transport delivers them out of order. |
| **Saga** | A multi-step process that spans events over time (e.g. reserve a fee when an alarm starts, release it when the alarm ends). |
| **Managed identity (MI / UAMI)** | An Azure-issued identity for an app, so it can log in to other Azure services without a stored password. "User-assigned" means it is created separately and attached to the app. |
| **RBAC** | Role-based access control: "this identity may read secrets", "that one may pull images". |
| **OIDC (for GitHub)** | Lets a GitHub Actions run prove to Azure who it is with a short-lived token, instead of a long-lived secret in the repo. |
| **IaC / Bicep** | Infrastructure as code: the Azure resources are written in text files (Bicep is Azure's own language for this) so they can be reviewed and re-created. |
| **OpenTelemetry (OTel)** | A vendor-neutral standard for traces, metrics and logs. Code emits once; you choose where it is sent. |
| **Lift-and-shift** | Moving something to the cloud unchanged, without redesigning it. |

---

### Compute — Azure Container Apps (one app per Center)

**What it is.** A managed place to run containers. You hand it an image; it runs it, restarts it, and adds or removes replicas. No servers or Kubernetes to manage.

**Chosen.** Each Center is its own Container App in one shared environment. Every app keeps at least one replica running (`minReplicas: 1`), and the three consumers scale up with KEDA when their Service Bus subscription backs up.

Why the floor of one: the Centers run background loops all the time (reading the inbox, pushing the outbox, re-ordering per duck, timing out sagas). If a Center scaled to zero it would miss work until something woke it up; with a queue scaler it would still wait for a cold start before catching up. One always-on replica avoids both.

| Alternative | Why not (here) |
|-------------|----------------|
| **App Service** (one plan, four Web Apps) | Azure's classic .NET web host, and still the most common one. But all four apps share one plan, so they scale together; background consumers would use WebJobs, an old pattern; and deploying one Center alone is clumsy. Good for a normal website, poor at "one Center = one scale unit". |
| **Azure Functions** | Built for "one message in → one small function runs". DuckNet's Centers are long-running loops (feeder, outbox dispatcher, per-key buffer, shard workers). Functions would force a rewrite of the Centers, not just host them. |
| **AKS** (managed Kubernetes) | Right when a platform team already runs Kubernetes for many services. Four Centers do not justify cluster upgrades, node pools and ingress. The code would run there fine, but the effort teaches cluster operations, not Center isolation. |
| **One VM / ACI** (Option A in [azure-deployment.md](./azure-deployment.md)) | Cheapest way to say "it runs in Azure". Proves nothing about deploying Centers independently or using a managed bus. |
| **Keep Aspire in the cloud** | Aspire is the laptop orchestrator that starts everything for local dev. Using it as the production supervisor would hide the real deploy model this lab is meant to show. |

Aspire was designed to publish to Container Apps, so the local `AddProject("alarm")` naturally becomes the `ducknet-alarm` app in Azure. That is the standard greenfield path in 2024–2026.

### Log — Azure Event Hubs (`ducknet-events`, 4 partitions)

**What it is.** A managed append-only event log (the same idea as Kafka). Events are kept for a retention period; readers keep their own bookmark and can start from the beginning.

**Chosen** as the system of record for replay. The partition key is `EventEnvelope.PartitionKey`, i.e. `duckId`, so all events for one duck land in the same lane and stay in order. Telemetry writes to it; a Center that needs to rebuild its data (Dashboard) reads from the start.

Four partitions make the Step 8 hot-key lesson concrete: one very loud duck always hashes to the same lane, and buying more throughput does not help if the key is wrong.

| Alternative | Why not (here) |
|-------------|----------------|
| **Keep the HTTP `event_log`** | Lift-and-shift of the current approach. Replay would depend on Telemetry's HTTP endpoint, and it teaches nothing about partition keys or independent retention. |
| **Service Bus as the log** | Topics deliver messages; they are not an archive you replay from the start months later. Their duplicate-detection windows and message expiry are the wrong tools for "the log is the pet". |
| **Kafka / Confluent / Event Hubs' Kafka protocol** | Same shape as Event Hubs, but an extra product to run for a lab that already has an Event Hubs adapter. The right answer in an estate (Telia-like) that already runs Kafka. |
| **Storage / blob log** | Cheap archive, but no partitions, no consumer groups; you would write your own feeder anyway. |
| **Event Hubs only (no Service Bus)** | An honest, simpler setup: consumer groups = DuckNet groups. Loses Service Bus's DLQ and subscription features. Kept as a documented variant, but not the locked path, because the two-pipe lesson is the point of Phase D. |

### Fan-out — Azure Service Bus Standard (topic + subscriptions)

**What it is.** A managed message broker. A topic receives each event once and copies it to every subscription; each Center reads its own subscription at its own pace.

**Chosen** as the live `IEventBus`. One topic with subscriptions `alarm-center`, `dashboard-projector` and `billing-center`. This is what NServiceBus and MassTransit default to on Azure. A message that fails delivery 10 times (`maxDeliveryCount: 10`) is moved to the platform's dead-letter sub-queue.

Note the distinction: completing a message on the bus only says "delivered". Whether the side effect should happen is still decided by the Center's inbox, which knows what it has already processed. Step 11 proved this port with RabbitMQ; the 12b `ServiceBusEventBus` implements the same contract.

| Alternative | Why not (here) |
|-------------|----------------|
| **RabbitMQ in Azure** (Container App or VM) | Works, and matches local dev. But then you run the broker yourself: disks, clustering, upgrades. The point of 12c is a managed bus, not moving Rabbit. |
| **Event Grid** | Made for push notifications and webhooks. Weak at competing consumers, per-subscriber isolation and dead-lettering for this kind of loop. Wrong product for "three Centers each consume the same facts". |
| **Storage Queues** | Cheap and simple, but no topics: fan-out means N copies or a dispatcher you write. No subscriptions to act as consumer groups. |
| **Event Hubs consumer groups as the only bus** | See the log section. Fine if you collapse the two pipes into one. |
| **Service Bus Premium / sessions** | Sessions can keep per-duck order inside the broker. DuckNet already repairs order in `PerKeySequencer`, so laptop and Azure behave the same. Premium is a cost jump the lab does not need. |

Bicep turns **off** topic duplicate detection on purpose. Turning it on would mask bugs the inbox is supposed to survive, and it is time-windowed anyway, so it is not the permanent `EventId` identity the inbox provides.

### Data — Azure Database for PostgreSQL Flexible Server (four databases)

**What it is.** A managed Postgres server: Azure handles patching, backups and storage.

**Chosen:** one server holding four databases, `telemetry`, `alarm`, `dashboard`, `billing`. Rule 2 ("no shared database") is about schema ownership, not about separate machines. A shared server with separate databases is normal and cheap. The `PostgresKernelDb` code from 12b already exists; SQLite stays for the local demo.

| Alternative | Why not (here) |
|-------------|----------------|
| **SQLite on Azure Files** | Option B. Fine for a toy; struggles with concurrent writers and multiple replicas. |
| **Azure SQL** | A perfectly good choice for .NET shops. But it would be a second database provider next to the Npgsql work already done. Postgres is the greenfield default and keeps "not everything is Microsoft SQL" visible. |
| **Cosmos DB** | Right when global distribution, request units and partition keys in the data store are the product. DuckNet's data is relational (inbox, outbox, saga rows, hourly buckets). Cosmos would mean rewriting persistence to teach a lesson Step 8 already teaches at the log layer. |
| **One database, four schemas** | Easier backups, easier leaks. A cross-Center `JOIN` becomes tempting. Rejected as a Rule 2 violation in spirit even if the server is shared. |
| **Four separate servers** | Isolation theatre: more cost and ops with no extra lesson. |

Burstable `B1ms`, no high availability, 7-day backups: a lab-sized configuration. Production-grade HA is a later budget decision, not an architecture one.

### Identity — user-assigned managed identity per Center + Key Vault (RBAC)

**What it is.** Instead of passwords in config, each app gets an Azure identity and is granted exactly the roles it needs. Key Vault stores the few secrets that remain.

**Chosen.** Two kinds of identity with different jobs. The pipeline identity (`ducknet-gha`, via GitHub OIDC) creates and updates resources. The runtime identities (one per Center) read secrets and talk to the bus and database. They are different principals on purpose: a leaked Actions log cannot send to Service Bus, and a compromised Billing app cannot redeploy Alarm.

Each Center has its own identity so permissions can be tightened later. Today the Bicep is still fairly open on the bus (all Centers can send and receive), because Alarm and Billing publish as well as consume. Key Vault uses Azure RBAC rather than the older access-policy model.

| Alternative | Why not (here) |
|-------------|----------------|
| **Connection strings in GitHub Secrets** | Works until a log, a fork or an `echo` leaks them. 12a forbids `AZURE_CLIENT_SECRET` for the same reason. |
| **One identity for all four apps** | Simpler Bicep. One compromise then reaches all four databases and both buses. |
| **System-assigned identity only** | Created and deleted with the app; fine at small scale. User-assigned is easier to grant permissions to before the app exists, and to reuse across a swap. |
| **Secrets as plaintext Container App settings** | Convenient, but visible in the portal and ARM, and hard to rotate. Key Vault references + managed identity is the 2026 default. |

### Registry — Azure Container Registry Basic (no admin user)

**What it is.** A private store for container images inside Azure.

**Chosen** as the place Container Apps pull from, using the `AcrPull` role on each Center's identity. **GHCR** (GitHub's registry) stays the no-Azure artifact: every PR can push images there without a subscription. Once 12c OIDC exists, the pipeline pushes to ACR; the running app never holds a GHCR token.

| Alternative | Why not (here) |
|-------------|----------------|
| **Pull GHCR from Container Apps** | Needs a personal access token or federated pull. One more secret on the runtime side. ACR + managed identity is the Azure-native pull. |
| **Docker Hub** | Rate limits, no managed identity, and not where this CD already publishes. |
| **ACR admin user** | A username/password login. Bicep sets `adminUserEnabled: false`. |

### Observability — Log Analytics + Application Insights

**What it is.** Azure's log store (Log Analytics) and its application-tracing product (Application Insights).

**Chosen.** The code already emits OpenTelemetry via `ServiceDefaults`. Moving to Azure changes only where the data is sent, not the `DuckNet.*` activity sources or the `TraceId` / `CausationId` fields on the envelope. Container Apps' own logs go to the same workspace.

| Alternative | Why not (here) |
|-------------|----------------|
| **Aspire dashboard in Azure** | A laptop tool, not a production store. |
| **Grafana / Prometheus / Tempo self-hosted** | Standard on AKS. Another cluster's worth of operations for four apps. |
| **Vendor APM only (no OTel)** | Locks traces to one product. DuckNet already paid the OTel cost locally. |

### IaC — Bicep (compile now, apply in 12c)

**Chosen** because Aspire and `azd` already emit Bicep, Azure itself keeps the state (no separate state backend as with Pulumi or Terraform), and CI is simply `az bicep build` and `what-if`. Full argument: [iac-bicep-vs-pulumi.md](./decisions/iac-bicep-vs-pulumi.md).

**Terraform** is a third language, its Azure provider lags new features, and it needs the same state backend as Pulumi without the C# upside. **Clicking in the portal** cannot be reviewed and would drift from `infra.yml`.

### CD — GitHub Actions + OIDC, mutate Azure only on `workflow_dispatch`

**Chosen** because the code, PRs, `ci.yml` and `deploy-center.yml` already live on GitHub. Azure DevOps would split CI from CD for a repo it does not host. Full argument: [cd-github-actions-vs-azure-devops.md](./decisions/cd-github-actions-vs-azure-devops.md).

Applying only on a manual trigger (`workflow_dispatch`) is a lab cost control, not an ideal for enterprise CD. Production would still want a required Environment reviewer; automatic deploy on merge is an optional later flag.

### Region — Sweden Central, fallback West Europe

Default in `main.bicep`. A data-residency-shaped default for a Nordic lab. If a SKU is missing in Sweden Central, falling back to West Europe is a 12c operational detail, not an architecture change.
