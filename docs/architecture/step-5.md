# Step 5 — as-built architecture

**Branch:** `step-5`  
**Punchline:** the dashboard is cattle, the log is the pet. DashboardCenter projects `squeaks_by_duck_hour` from `Squeaked` only. Truncate the read model, `POST /dashboard/rebuild`, replay from offset 0 — identical rows.

Aspire now hosts three services. DashboardCenter never publishes, never opens Telemetry or Alarm SQLite, and never calls another Center. Integration is still `IEventBus` (HTTP log tail).

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws a shared `Event log` that Alarm also writes to. In code the log table lives in Telemetry's SQLite. DashboardCenter **never opens that file** and **does not POST** `/bus/events` — it is a projector only. Hostility (dup + shuffle) is applied on DashboardCenter **after** the HTTP read.

**Schema delta vs the plan:** no `outbox` (nothing to publish). No `PerKeySequencer` — hour counts are commutative, so inbox + contiguous `last_offset` is enough under shuffle. `hour_utc` is the UTC hour of `OccurredAt` (`yyyy-MM-ddTHH:00:00Z`).

## Delta vs Step 4

| | |
|--|--|
| **Added** | `DuckNet.DashboardCenter`, `squeaks_by_duck_hour` read model, `POST /dashboard/rebuild`, `GET /dashboard/summary` + `/dashboard/duck/{id}`, consumer group `dashboard-projector`, Dockerfile + deploy path |
| **Changed** | AppHost runs three projects. Kernel inbox/offset and `HttpLogTailFeeder` gained reset hooks so rebuild can replay from 0. |
| **Unchanged** | Telemetry owns `event_log` writes. AlarmCenter rate window + `AlarmRaised`. No Center-to-Center business HTTP. No shared DB. Hostile transport after log read. |

## Wire types

Dedup key is still **`EventId`**. Resume key is **`LogOffset`** on Telemetry's log, checkpointed in Dashboard's `consumer_offsets`. Hour bucket key is **`duck_id` + `hour_utc`**.

```text
EventEnvelope                          Step 5 use
  EventId          Guid                inbox key; duplicates keep this id
  Type             string              project "Squeaked"; skip others (offset only)
  Version          int                 1
  PartitionKey     string              duckId (not ordered here)
  SequenceNumber   long                unused by the projector
  OccurredAt       DateTimeOffset      truncated to UTC hour
  PayloadJson      string              Squeaked v1
  LogOffset        long                telemetry event_log.offset
```

```text
Squeaked v1        duckId, sequenceNumber, occurredAt
```

```text
squeaks_by_duck_hour
  duck_id TEXT
  hour_utc TEXT        e.g. 2026-08-27T12:00:00Z
  count INTEGER
  PRIMARY KEY (duck_id, hour_utc)
```

Config that changes behavior:

| Knob | Default | Effect |
|------|---------|--------|
| `EVENT_LOG_URL` | (required) | Telemetry base URL for `/bus/events` |
| `DUCKNET_DB` | `dashboard.db` | Dashboard SQLite path |
| `DUPLICATE_RATE` / shuffle | same as Step 4 | applied on DashboardCenter after log read |
| `--reset-db` / `RESET_DB` | off | delete the Dashboard file on start |

## Architecture

`IEventBus` is the only integration seam. The read model, inbox, and offsets live on DashboardCenter. Telemetry does not call Dashboard. Dashboard does not open `telemetry.db` or `alarm.db`. Dashboard does not write the log.

```mermaid
flowchart TB
  subgraph Aspire["DuckNet.AppHost"]
    subgraph TC["TelemetryCenter — own SQLite"]
      SIM["DuckSimulator"]
      TX["TransactionalPublisher"]
      TDB[("telemetry.db<br/>duck_state + outbox + event_log")]
      DSP["OutboxDispatcher"]
      BUSAPI["GET/POST /bus/events"]
      SIM --> TX
      TX --> TDB
      TDB --> DSP
      DSP --> TDB
      TDB --> BUSAPI
    end

    subgraph Transport["IEventBus adapter — not a business API"]
      HTTP["HttpLogClient / HttpLogTailFeeder"]
      DUP["DuplicatorMiddleware"]
      SHF["ShufflerMiddleware"]
      MEM["InMemoryEventBus"]
      BUSAPI -->|"HTTP after log"| HTTP
      HTTP -->|"PublishAsync"| DUP
      DUP --> SHF
      SHF --> MEM
    end

    subgraph AC["AlarmCenter — own SQLite"]
      ALM["AlarmConsumer"]
      ADB[("alarm.db")]
      ALM --> ADB
    end

    subgraph DC["DashboardCenter — own SQLite"]
      CONS["DashboardConsumer"]
      INB[("inbox + offsets")]
      RM[("squeaks_by_duck_hour")]
      RB["POST /dashboard/rebuild"]
      MEM --> CONS
      CONS --> INB
      CONS --> RM
      RB -->|"truncate + offset 0"| RM
      RB --> INB
    end
  end

  BUSAPI -.->|GET only — Alarm also tails| AC
  SIM -.->|no type reference| CONS
  CONS -.->|never opens| TDB
  CONS -.->|never opens| ADB
  DC -.->|no POST /bus/events| BUSAPI
  TC -.->|no HTTP to Dashboard| DC
  AC -.->|no HTTP to Dashboard| DC
  DUP -.->|must not contain| INB
```

```mermaid
classDiagram
  class IEventBus {
    <<interface>>
    PublishAsync(EventEnvelope)
    SubscribeAsync(consumerGroup)
  }
  class HttpLogClient {
    ReadAfterAsync(offset)
  }
  class DashboardReadModel {
    ApplySqueak(duckId, occurredAt)
    Truncate()
    List()
  }
  class DashboardConsumer {
    RunAsync()
    RebuildAsync()
  }
  HttpLogTailFeeder --> HttpLogClient
  HttpLogTailFeeder --> IEventBus
  DashboardConsumer --> IEventBus
  DashboardConsumer --> DashboardReadModel
  DashboardConsumer --> Inbox
  DashboardConsumer --> ConsumerOffsetStore
```

**Intentionally absent:** `PerKeySequencer` on Dashboard, Dashboard outbox, `volume_db` (Step 6), BillingCenter, RabbitMQ, DLQ.

## Execution — squeak to hour bucket

Happy path. Cross-key order is not constrained after shuffle. Counts are commutative.

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Tdb as telemetry.db
  participant Bus as GET /bus/events
  participant Dash as DashboardConsumer
  participant Ddb as dashboard.db

  Sim->>Tdb: state + outbox COMMIT
  Note over Tdb: OutboxDispatcher appends event_log

  Dash->>Bus: after last_offset
  Bus-->>Dash: Squeaked LogOffset=N
  Dash->>Ddb: BEGIN inbox + upsert hour count + offset COMMIT
  Note over Ddb: PK (duck_id, hour_utc) count = count + 1
```

## Execution — rebuild from offset 0

The read model is disposable. Truncate + clear inbox + reset contiguous offset + feeder `ResetTo(0)`. Replay produces the same rows.

```mermaid
sequenceDiagram
  autonumber
  participant Op as Operator
  participant Dash as DashboardConsumer
  participant Ddb as dashboard.db
  participant Feed as HttpLogTailFeeder
  participant Log as GET /bus/events

  Op->>Dash: POST /dashboard/rebuild
  Dash->>Dash: acquire handle lock
  Dash->>Ddb: DELETE squeaks_by_duck_hour + inbox + offsets
  Dash->>Feed: ResetTo(0)
  Dash-->>Op: 202 replaying
  Feed->>Log: after=0
  Log-->>Feed: full log
  Feed->>Dash: Squeaked (and skipped AlarmRaised)
  Dash->>Ddb: project again — identical rows
```

## Execution — hostile transport (Dashboard consumer)

Same as Step 4, on the subscriber: duplicator clones keep `EventId`; shuffle is not global order; inbox is on DashboardCenter. The upsert itself is **not** idempotent — inbox is.

```mermaid
sequenceDiagram
  autonumber
  participant Feed as HttpLogTailFeeder
  participant Dup as Duplicator
  participant Dash as DashboardConsumer

  Feed->>Dup: Squeaked EventId=X offset=N
  Dup->>Dash: X
  Dup-->>Dash: clone X
  Dash->>Dash: inbox skip second X
  Note over Dash: hour count incremented once
```

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type == Squeaked?}
  type -->|no| off[Mark contiguous last_offset only]
  type -->|yes| tx["BEGIN: offset, inbox, upsert hour"]
  tx --> new{new EventId?}
  new -->|no| skip[Skip — duplicate]
  new -->|yes| up["INSERT hour row or count = count + 1"]
  skip --> done[Next envelope]
  up --> done
  off --> done
```

## Demo lifecycle

```mermaid
flowchart TD
  start([dotnet run AppHost]) --> dash[Aspire dashboard]
  dash --> tel[telemetry /health]
  dash --> alm[alarm /health]
  dash --> dc[dashboard /health]
  tel --> sim[DuckSimulator → event_log]
  sim --> tail[Dashboard HttpLogTailFeeder]
  tail --> sum[GET /dashboard/summary]
  del[POST /dashboard/rebuild] --> replay[feeder from offset 0]
  replay --> sum
```
