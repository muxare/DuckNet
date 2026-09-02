# Azure IaC (Step 12b)

Checked-in Bicep is the source of truth ([decision](../../docs/decisions/iac-bicep-vs-pulumi.md)). **Compile here; apply in 12c.** No subscription is required for this step. Why these resources and not the alternatives: [design rationale](../../docs/design-rationale.md).

```bash
az bicep install
az bicep build --file infra/bicep/main.bicep
```

`infra.yml` runs that compile on PRs that touch `infra/bicep/`. `what-if` and `az deployment group create` run only when GitHub Environment OIDC vars exist, and only on `workflow_dispatch`.

| Module | Resource |
|--------|----------|
| `monitoring` | Log Analytics + Application Insights |
| `acr` | ACR Basic (no admin user) |
| `identities` | User-assigned MI per Center |
| `keyvault` | Key Vault with RBAC |
| `postgres` | Flexible Server B1ms + databases `telemetry`, `alarm`, `dashboard`, `billing` |
| `eventhubs` | Namespace + hub `ducknet-events` (4 partitions) |
| `servicebus` | Namespace + topic `ducknet-events` + subscriptions per consumer group |
| `containerapps` | ACE + one app per Center; KEDA on Service Bus depth; min replicas 1 |
| `roles` | Runtime MI data-plane roles; pipeline `AcrPush` / Key Vault Secrets Officer if `pipelinePrincipalId` is set |

Pipeline identity (Entra app `ducknet-gha`) is **not** a data-plane client. Container App MIs are.
