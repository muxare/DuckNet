# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit `Squeaked` facts; autonomous Centers react without calling each other or sharing a database.

Each step adds one distributed-systems idea and stays runnable end-to-end. Current: **Step 4 — second Center (Aspire)**.

## Rules

1. No Center-to-Center calls. Integration is events only.
2. No shared database. Each Center owns its schema.
3. Events are past facts (`Squeaked`, not `SqueakTheDuck`).
4. Transport is hostile: at-least-once, unordered across keys.
5. Every step stays runnable.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

## Demos

### Step 4 — two Centers (Aspire)

TelemetryCenter publishes `Squeaked` into its log. AlarmCenter tails that log over HTTP (`IEventBus`), never opens Telemetry's SQLite, and raises `AlarmRaised` when a duck exceeds `ALARM_RATE_THRESHOLD` squeaks in `ALARM_WINDOW_SECONDS` (event time).

```bash
dotnet run --project src/DuckNet.AppHost
```

Aspire dashboard: both `telemetry` and `alarm` healthy. Stop `alarm`, wait for a burst, start it again — `GET /alarms` on AlarmCenter fills in from the log.

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

**What to look for (Aspire):** AlarmCenter `/stats` `database` is `alarm.db`, never `telemetry.db`. After a squeak storm, `/alarms` lists ducks that crossed the threshold.

## Layout

```
src/DuckNet.AppHost/          # Aspire orchestration
src/DuckNet.Contracts/        # event records only
src/DuckNet.EventBus/         # IEventBus + HTTP log adapter
src/DuckNet.Kernel/           # durable primitives + Step 3 console
src/DuckNet.TelemetryCenter/  # owns event_log
src/DuckNet.AlarmCenter/      # own DB, rate rule, AlarmRaised
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
| 4 | in progress | Two Centers, two DBs; Alarm catches up from the log |
| 5+ | planned | See [ImplementationPlan.md](./ImplementationPlan.md) |

## Docs

- [docs/architecture/](./docs/architecture/) — as-built architecture + execution diagrams per completed step
- [ImplementationPlan.md](./ImplementationPlan.md) — step-by-step build plan and acceptance criteria
- [CLAUDE.md](./CLAUDE.md) — architecture rules for humans and agents
- [CentersBuildPlan.md](./CentersBuildPlan.md) — why the Centers exist
- [DuckNetArchitectureSteps.html](./DuckNetArchitectureSteps.html) — target architecture per step (roadmap)
- [docs/development-diary.md](./docs/development-diary.md) — what landed, with diagrams
