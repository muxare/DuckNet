# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit `Squeaked` facts; autonomous Centers react without calling each other or sharing a database.

Each step adds one distributed-systems idea and stays runnable end-to-end. Current: **Step 6 — schema evolution across a boundary**.

## Rules

1. No Center-to-Center calls. Integration is events only.
2. No shared database. Each Center owns its schema.
3. Events are past facts (`Squeaked`, not `SqueakTheDuck`).
4. Transport is hostile: at-least-once, unordered across keys.
5. Every step stays runnable.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22](https://nodejs.org/) — DashboardCenter Vue UI (`npm ci` runs on `dotnet build`)

## Build & test

```bash
dotnet build
dotnet test
```

## Demos

### Step 6 — mixed v1/v2 log, upcast at each Center (Aspire)

TelemetryCenter emits `Squeaked` v2 (`volumeDb`). AlarmCenter and DashboardCenter upcast leftover v1 rows before the handler. Handlers parse v2 only. Dashboard stores `volume_db` on the hour bucket. Delete the dashboard, rebuild — mixed log still projects.

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: `telemetry`, `alarm`, and `dashboard` healthy. Click the **dashboard** URL for the Vue UI (hour buckets, volume, rebuild). JSON remains at `/dashboard/summary`. Live traffic is v2. Mixed v1/v2 is the test fixture (`dotnet test --filter MixedVersion`).

`GET /dashboard/summary` includes `totalVolumeDb`. `POST /dashboard/rebuild` still truncates and replays from offset 0.

Stop `alarm`, wait for a burst, start it again — `GET /alarms` still fills in from the log (Step 4).

| Knob | Default | Meaning |
|------|---------|---------|
| `ALARM_RATE_THRESHOLD` | 10 | raise when unique squeaks in the window **>** this |
| `ALARM_WINDOW_SECONDS` | 60 | event-time sliding window |
| `EVENT_LOG_URL` | (Aspire injects) | Telemetry base URL for `/bus/events` |
| `DUCKNET_DB` | per-Center file under AppHost `data/` | SQLite path |

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

**What to look for (kernel):** with defenses on, `Published (session) == Counted (lifetime)` on a fresh DB, `Log rows == Counted`, and `Out of order == 0`.

**What to look for (Aspire):** Click DashboardCenter — Vue table of `squeaks_by_duck_hour` with live totals. `/stats` `database` is `dashboard.db`, never `telemetry.db`. Rebuild from the UI (or `POST /dashboard/rebuild`) refills the same rows. New squeaks show `Version: 2` and a `volumeDb`. AlarmCenter `/alarms` still lists threshold crossings.

## Layout

```
src/DuckNet.AppHost/          # Aspire orchestration
src/DuckNet.Contracts/        # event records + versions only
src/DuckNet.EventBus/         # IEventBus + HTTP log adapter + upcasters
src/DuckNet.Kernel/           # durable primitives + Step 3 console
src/DuckNet.TelemetryCenter/  # owns event_log
src/DuckNet.AlarmCenter/      # own DB, rate rule, AlarmRaised
src/DuckNet.DashboardCenter/  # disposable read model + Vue UI + rebuild
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
| 6 | in progress | Mixed v1/v2 log; upcast at the consumer, not in the bus |
| 7+ | planned | See [ImplementationPlan.md](./ImplementationPlan.md) |

## Docs

- [docs/architecture/](./docs/architecture/) — as-built architecture + execution diagrams per completed step
- [docs/azure-deployment.md](./docs/azure-deployment.md) — learning notes: Azure options, 2018–2026 industry path, lab pricing (not implemented)
- [ImplementationPlan.md](./ImplementationPlan.md) — step-by-step build plan and acceptance criteria
- [CLAUDE.md](./CLAUDE.md) — architecture rules for humans and agents
- [CentersBuildPlan.md](./CentersBuildPlan.md) — why the Centers exist
- [DuckNetArchitectureSteps.html](./DuckNetArchitectureSteps.html) — target architecture per step (roadmap)
- [docs/development-diary.md](./docs/development-diary.md) — what landed, with diagrams
