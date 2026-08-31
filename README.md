# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit `Squeaked` facts; autonomous Centers react without calling each other or sharing a database.

Each step adds one distributed-systems idea and stays runnable end-to-end. Current: **Step 11 — swap the transport (`IEventBus` → RabbitMQ)**.

## Rules

1. No Center-to-Center calls. Integration is events only.
2. No shared database. Each Center owns its schema.
3. Events are past facts (`Squeaked`, not `SqueakTheDuck`).
4. Transport is hostile: at-least-once, unordered across keys.
5. Every step stays runnable.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22](https://nodejs.org/) — DashboardCenter Vue UI (`npm ci` runs on `dotnet build`)
- [Docker](https://docs.docker.com/get-docker/) — Aspire RabbitMQ container and `DuckNet.EventBus.Tests` broker suite

## Build & test

```bash
dotnet build
dotnet test
```

## Demos

### Step 11 — RabbitMQ behind `IEventBus` (Aspire)

The production path uses RabbitMQ. Center tests and the kernel still use `InMemoryEventBus` when `ConnectionStrings__rabbitmq` is unset. Handlers do not change.

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: `rabbitmq`, `telemetry`, `alarm`, `dashboard`, and `billing` healthy. Then the Step 10 saga demo still applies. Kill `rabbitmq` in Aspire: feeders and consumers reconnect.

`dotnet test --filter FullyQualifiedName~EventBus` is the port proof (in-memory + Testcontainers).

Agent shortcut: `/run-aspire`.

### Step 10 — saga (Aspire)

AlarmCenter raises; BillingCenter reserves a fee. Resolve before the timeout and the fee is released. Miss the window and a timeout worker compensates. The two Centers never call each other.

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: `telemetry`, `alarm`, `dashboard`, and `billing` healthy. Then:

```bash
# Watch the saga
curl http://<billing>/sagas

# Fast path — operator resolve on AlarmCenter (emits AlarmResolved)
curl -X POST http://<alarm>/alarms/duck-1/resolve
curl http://<billing>/sagas   # state Released, FeeReleased reason=AlarmResolved

# Slow path — leave another alarm Reserved; Aspire timeout is 15s
# GET /sagas → Expired, FeeReleased reason=Timeout
```

`SAGA_TIMEOUT_SECONDS` defaults to 300; AppHost sets 15 so the timeout path is demoable. `BILLING_FEE_CENTS` defaults to 100.

Agent shortcut: `/run-aspire`.

### Step 9 — tracing (Aspire)

One squeak is one trace. `TraceId` (W3C traceparent) rides on the envelope; Centers never call each other. Open Aspire **Traces**. Names look like `alarm: handle.Squeaked` (resource + span). Filter `handle.Squeaked` — not `DuckNet.*` (that is the ActivitySource, not the dashboard name).

```bash
dotnet run --project src/DuckNet.AppHost
```

A duplicate delivery keeps the same `TraceId` and adds a second `handle.Squeaked` span tagged `ducknet.duplicate`. Inbox still skips the side effect.

Agent shortcut: `/run-aspire`.

### Step 8 — hot partitions (kernel)

One LoudDuck squeaks 100× more than the others. A single worker (`--shard-count 1`) lets quiet ducks wait behind it. Three shards isolate the hot key. Lag prints per shard and per duck.

```bash
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 1 --hot-demo --shard-count 1 --no-shuffle --duplicate-rate 0
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 1 --hot-demo --shard-count 3 --no-shuffle --duplicate-rate 0
```

`--hot-demo` sets `LOUD_DUCK_ID=duck-1`, 8ms handle delay, and fast emits. Compare `maxLagMs` for ducks that did **not** hash onto shard 0.

### Step 8 — Aspire

LoudDuck is on. Alarm and Dashboard run three shard workers and a 12ms handle delay. Dashboard shows per-shard queue/lag. `GET /metrics` on alarm or dashboard is the JSON.

```bash
dotnet run --project src/DuckNet.AppHost
```

Set `SHARD_COUNT=1` on alarm or dashboard to re-starve quiet keys.

Agent shortcut: `/run-aspire`.

### Step 7 — poison + DLQ (Aspire)

One malformed `Squeaked` never blocks the partition. Each Center retries, writes its own `dead_letter_queue`, advances `last_offset`, and keeps consuming. Inspect `GET /dlq`. Replay with a fixed payload or skip.

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: `telemetry`, `alarm`, and `dashboard` healthy. From Telemetry:

```bash
curl -X POST http://<telemetry>/bus/poison
curl http://<alarm>/dlq
curl -X POST http://<alarm>/dlq/1/replay?fix=true
```

`GET /stats` includes `dlqCount`. Env `INJECT_POISON_EVENT=true` on Telemetry appends one poison row at startup.

Agent shortcut: `/run-aspire`.

### Step 7 — kernel poison demo

```bash
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5 --inject-poison
dotnet run --project src/DuckNet.Kernel -- --list-dlq
dotnet run --project src/DuckNet.Kernel -- --replay-dlq 1 --fix
```

`Log rows == Counted + 1`, `DLQ rows == 1` before replay.

### Step 6 — mixed v1/v2 log, upcast at each Center (Aspire)

TelemetryCenter emits `Squeaked` v2 (`volumeDb`). AlarmCenter and DashboardCenter upcast leftover v1 rows before the handler. Handlers parse v2 only. Dashboard stores `volume_db` on the hour bucket. Delete the dashboard, rebuild — mixed log still projects.

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: `telemetry`, `alarm`, and `dashboard` healthy. Click the **dashboard** URL for the Vue UI (**Developer** maps by default; **Read model** for hour buckets, volume, rebuild). JSON remains at `/dashboard/summary`. Live traffic is v2. Mixed v1/v2 is the test fixture (`dotnet test --filter MixedVersion`).

`GET /dashboard/summary` includes `totalVolumeDb`. `POST /dashboard/rebuild` still truncates and replays from offset 0.

Stop `alarm`, wait for a burst, start it again — `GET /alarms` still fills in from the log (Step 4).

| Knob | Default | Meaning |
|------|---------|---------|
| `ALARM_RATE_THRESHOLD` | 10 | raise when unique squeaks in the window **>** this |
| `ALARM_WINDOW_SECONDS` | 60 | event-time sliding window |
| `EVENT_LOG_URL` | (Aspire injects) | Telemetry base URL for `/bus/events` |
| `DUCKNET_DB` | per-Center file under AppHost `data/` | SQLite path |
| `LOUD_DUCK_ID` | `duck-1` (Aspire) | 100× squeak weight |
| `SHARD_COUNT` | 3 | consumer workers; `1` starves quiet keys |
| `HANDLE_DELAY_MS` | 12 (Aspire) | fake handler work so lag is visible |

Agent shortcut: `/run-aspire`.

### Step 3 — kernel (single process)

Defenses on — producer writes state+outbox in one transaction; the log is the source of truth; duplicates and shuffle hit the tail feeder. Counts stay exact and survive restart:

```bash
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5
```

Naive consumer — inbox and sequencer off:

```bash
dotnet run --project src/DuckNet.Kernel -- --mis-demo --reset-db --seconds 5
```

Agent shortcuts: `/run-demo`, `/mis-demo`.

**What to look for (Aspire):** Open the **dashboard** resource URL — **Developer** is the default (Vue Flow graph of Centers, live offsets, click a circle for that Center's process, **Objects** for types, **All detail** for the whole graph). Hover a node, edge, or live number; press **T** to pin the card; **T** again on a highlighted word to drill down; **Esc** closes. **Read model** is the hour-bucket table. The Vue app polls each Center as a browser; DashboardCenter does not call the others. **Traces** — filter `handle.Squeaked`, click a row; `simulate.squeak` / `append.log` / both Centers share one `TraceId`. **Saga** — Billing drill-in or `GET /sagas`; fast resolve from the Alarm panel (or `POST` alarm `/alarms/duck-1/resolve`) → `Released`; wait 15s → `Expired`. LoudDuck's shard has queue depth and lag. `/stats` `database` is still each Center's file, never a shared DB.

**What to look for (kernel):** `--hot-demo --shard-count 1` → every duck's `maxLagMs` is huge. `--shard-count 3` → only keys that hash onto the LoudDuck shard lag; others stay around the handle delay. With defenses on and no poison, `Published == Counted` and `Out of order == 0`.

## Layout

```
src/DuckNet.AppHost/          # Aspire orchestration
src/DuckNet.ServiceDefaults/  # OTel + DuckNet.* ActivitySources
src/DuckNet.Contracts/        # event records + versions only
src/DuckNet.EventBus/         # IEventBus + HTTP log adapter + upcasters + tracing
src/DuckNet.Kernel/           # durable primitives + console (shards, retry, DLQ CLI)
src/DuckNet.TelemetryCenter/  # owns event_log; LoudDuck; POST /bus/poison
src/DuckNet.AlarmCenter/      # own DB, rate rule, AlarmRaised / AlarmResolved, DLQ, shards
src/DuckNet.DashboardCenter/  # disposable read model + Vue Developer maps + Read model + DLQ + shard lag
src/DuckNet.BillingCenter/    # own DB, saga, timeout compensation, FeeReserved / FeeReleased
tests/
infra/docker/                 # one image per Center
```

## Roadmap

| Phase | Steps | Outcome |
|-------|-------|---------|
| A — Kernel | 0–3 | At-least-once, idempotency, per-key ordering, durable log + outbox |
| B — Distributed | 4–6 | Multi-Center Aspire host, CQRS projections, schema evolution |
| C — Production pain | 7–11 | DLQ, hot partitions, tracing, sagas, broker swap |
| D — Cloud & ops | 12+ | Azure hosting, per-Center CI/CD |

| Step | Status | Demo punchline |
|------|--------|----------------|
| 0 | complete | One producer, one consumer, counts match |
| 1 | complete | Forced duplicates + inbox → counts still match |
| 2 | complete | Shuffle + per-key sequencer → order and counts still match |
| 3 | complete | Durable log + outbox → kill/restart, no double-count |
| 4 | complete | Two Centers, two DBs; Alarm catches up from the log |
| 5 | complete | Delete the dashboard, rebuild from the log |
| 6 | complete | Mixed v1/v2 log; upcast at the consumer, not in the bus |
| 7 | complete | One poison message; retry → DLQ; stream continues |
| 8 | complete | LoudDuck + sharding; quiet keys stay near real-time |
| 9 | complete | Envelope TraceId + OTel; one squeak, one Aspire trace |
| 10 | in progress | Billing saga: reserve on alarm, release or timeout compensate |
| 11+ | planned | See [ImplementationPlan.md](./ImplementationPlan.md) |

## Docs

- [docs/architecture/](./docs/architecture/) — as-built architecture + execution diagrams per completed step
- [docs/azure-deployment.md](./docs/azure-deployment.md) — learning notes: Azure options, 2018–2026 industry path, lab pricing (not implemented)
- [docs/ci-policy.md](./docs/ci-policy.md) — required vs advisory checks; ReviewFlow later/parked backlog
- [ImplementationPlan.md](./ImplementationPlan.md) — step-by-step build plan and acceptance criteria
- [CLAUDE.md](./CLAUDE.md) — architecture rules for humans and agents
- [CentersBuildPlan.md](./CentersBuildPlan.md) — why the Centers exist
- [DuckNetArchitectureSteps.html](./DuckNetArchitectureSteps.html) — target architecture per step (roadmap)
- [docs/development-diary.md](./docs/development-diary.md) — what landed, with diagrams
