# Step 4 — as-built architecture

**Branch:** `step-4`  
**Punchline:** two processes, two SQLite files. TelemetryCenter owns the log write path. AlarmCenter subscribes only through `IEventBus` (HTTP log tail). Stop AlarmCenter, keep publishing, restart — it catches up and raises `AlarmRaised` for ducks that crossed the rate threshold.

Aspire AppHost runs both services. There is still no Center-to-Center business call and no shared database.

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws a shared `Event log` box both Centers write to. In code the log table lives in Telemetry's SQLite. AlarmCenter **never opens that file**. It tails and appends through `GET/POST /bus/events` — the `IEventBus` adapter until RabbitMQ (Step 11). Hostility (dup + shuffle) is applied on AlarmCenter **after** that read.

**Schema delta vs the plan:** Alarm sliding window uses **event time** (`OccurredAt`), not wall clock, so catch-up after downtime still sees a burst as a burst. `AlarmRaised` fires once per duck per threshold crossing (sticky `duck_alarm_state.active`) so a storm does not emit one alarm per extra squeak. `AlarmResolved` is not implemented yet (Step 10).

## Delta vs Step 3

| | |
|--|--|
| **Added** | `DuckNet.Contracts`, `DuckNet.EventBus`, `DuckNet.TelemetryCenter`, `DuckNet.AlarmCenter`, `DuckNet.AppHost`, HTTP log adapter (`HttpLogClient`, `HttpLogTailFeeder`), Alarm schema + rate window, `/health` + `/alarms`, Dockerfiles, `deploy-center.yml` |
| **Changed** | Kernel is the durable primitives library (still a Step 3 console). `EventEnvelope` / `IEventBus` / hostile middleware live in Contracts + EventBus. `KernelDb` takes a per-Center schema. |
| **Unchanged** | Outbox + log + inbox + contiguous offset, per-key sequencer, hostile transport after log read, no Center-to-Center business HTTP, no shared DB |

## Wire types

Dedup key is still **`EventId`**. Order key is **`PartitionKey` + `SequenceNumber`**. Resume key is **`LogOffset`** on Telemetry's log, checkpointed in Alarm's `consumer_offsets`.

```text
EventEnvelope                          Step 4 use
  EventId          Guid                inbox key; duplicates keep this id
  Type             string              "Squeaked" | "AlarmRaised"
  Version          int                 1
  PartitionKey     string              duckId
  SequenceNumber   long                per-type per-duck; Alarm sequencer only offers Squeaked
  OccurredAt       DateTimeOffset      event-time window for the rate rule
  PayloadJson      string              Squeaked v1 or AlarmRaised v1
  CausationId      string?             AlarmRaised ← Squeaked EventId
  LogOffset        long                telemetry event_log.offset
```

```text
Squeaked v1        duckId, sequenceNumber, occurredAt
AlarmRaised v1     duckId, rate (squeaks/minute in the window), windowStart
```

Config that changes behavior:

| Knob | Default | Effect |
|------|---------|--------|
| `ALARM_RATE_THRESHOLD` | 10 | raise when unique squeaks in the window **>** this |
| `ALARM_WINDOW_SECONDS` | 60 | event-time sliding window |
| `EVENT_LOG_URL` | (required for Alarm) | Telemetry base URL for `/bus/events` |
| `DUCKNET_DB` | `telemetry.db` / `alarm.db` | per-Center SQLite path |
| `DUPLICATE_RATE` / shuffle | same as Step 3 | applied on AlarmCenter after log read |
| `--reset-db` / `RESET_DB` | off | kernel demo; Centers use `RESET_DB=true` |

## Architecture

`IEventBus` is the only integration seam. Inbox, sequencer, offsets, and alarm rows live on AlarmCenter. Telemetry does not call Alarm. Alarm does not open `telemetry.db`.

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
      CONS["AlarmConsumer"]
      PK["PerKeySequencer"]
      INB[("inbox + offsets")]
      WIN[("squeak_window + alarms")]
      ROB["RemoteOutboxDispatcher"]
      MEM --> CONS
      CONS --> PK
      CONS --> INB
      CONS --> WIN
      WIN --> ROB
      ROB -->|"POST /bus/events"| BUSAPI
    end
  end

  SIM -.->|no type reference| CONS
  CONS -.->|never opens| TDB
  TC -.->|no HTTP to Alarm| AC
  DUP -.->|must not contain| PK
  SHF -.->|must not contain| INB
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
    AppendAsync(envelope)
  }
  class AlarmStore {
    TryRaise(Squeaked) bool
  }
  class AlarmConsumer {
    RunAsync()
  }
  DuckSimulator --> TransactionalPublisher
  TransactionalPublisher --> OutboxDispatcher
  OutboxDispatcher --> EventLogStore
  HttpLogTailFeeder --> HttpLogClient
  HttpLogTailFeeder --> IEventBus
  AlarmConsumer --> IEventBus
  AlarmConsumer --> AlarmStore
  RemoteOutboxDispatcher --> HttpLogClient
```

**Intentionally absent:** DashboardCenter, PostgreSQL, RabbitMQ, `AlarmResolved`, DLQ.

## Execution — squeak to AlarmRaised

Happy path. Cross-key order is not constrained after shuffle.

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Tx as TransactionalPublisher
  participant Tdb as telemetry.db
  participant Bus as GET /bus/events
  participant Alm as AlarmConsumer
  participant Adb as alarm.db
  participant Post as POST /bus/events

  Sim->>Tx: PublishSqueakAsync duck-1
  Tx->>Tdb: BEGIN duck_state + outbox COMMIT
  Note over Tdb: OutboxDispatcher appends event_log

  Alm->>Bus: after last_offset
  Bus-->>Alm: Squeaked LogOffset=N
  Alm->>Alm: sequencer Offer (Squeaked only)
  Alm->>Adb: BEGIN inbox + window + offset COMMIT
  alt unique squeaks in event-time window greater than threshold and not active
    Adb->>Adb: INSERT alarms + outbox AlarmRaised
    Adb->>Post: RemoteOutboxDispatcher
    Post->>Tdb: append AlarmRaised (INSERT OR IGNORE EventId)
  end
```

## Execution — AlarmCenter down then catch-up

Telemetry keeps appending. Alarm's offset stays put. On restart the HTTP tail resumes; the rate window uses `OccurredAt`, so a storm during the gap still qualifies.

```mermaid
sequenceDiagram
  autonumber
  participant Tel as TelemetryCenter
  participant Log as event_log
  participant Alm as AlarmCenter

  Tel->>Log: Squeaked 1..12 duck-1
  Note over Alm: process stopped
  Alm->>Alm: start, last_offset = 0
  Alm->>Log: GET /bus/events?after=0
  Log-->>Alm: offsets 1..12
  Alm->>Alm: window count 12 greater than 10
  Alm->>Alm: INSERT alarm + outbox AlarmRaised
```

## Execution — hostile transport (Alarm consumer)

Same as Step 3, on the subscriber: duplicator clones keep `EventId`; shuffle is not global order; inbox and sequencer are on AlarmCenter.

```mermaid
sequenceDiagram
  autonumber
  participant Feed as HttpLogTailFeeder
  participant Dup as Duplicator
  participant Alm as AlarmConsumer

  Feed->>Dup: Squeaked EventId=X offset=N
  Dup->>Alm: X
  Dup-->>Alm: clone X
  Alm->>Alm: inbox skip second X
  Note over Alm: window counted once
```

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type == Squeaked?}
  type -->|no| off[Mark contiguous last_offset only]
  type -->|yes| seq[PerKeySequencer.Offer]
  seq --> late{seq vs nextExpected}
  late -->|less| drop[Late drop — no checkpoint]
  late -->|greater| buf[Buffer]
  late -->|equal| ready[Handle this envelope]
  ready --> tx["BEGIN: inbox, duck_progress, window trim+count, offset"]
  tx --> new{new EventId?}
  new -->|no| skip[Skip]
  new -->|yes| rate{count greater than threshold and not active?}
  rate -->|yes| raise["INSERT alarms + outbox AlarmRaised, active=1"]
  rate -->|no| maybeOff{count at or below threshold and active?}
  maybeOff -->|yes| clear["active=0 — no AlarmResolved yet"]
  maybeOff -->|no| done[Next envelope]
  raise --> done
  clear --> done
  skip --> done
  off --> done
  drop --> done
  buf --> done
```

## Demo lifecycle

```mermaid
flowchart TD
  start([dotnet run AppHost]) --> dash[Aspire dashboard]
  dash --> tel[telemetry /health]
  dash --> alm[alarm /health]
  tel --> sim[DuckSimulator → outbox → event_log]
  sim --> tail[Alarm HttpLogTailFeeder]
  tail --> rule[rate window]
  rule --> alarms[GET /alarms]
  stop[Stop alarm in dashboard] --> storm[Telemetry keeps publishing]
  storm --> restart[Start alarm]
  restart --> catch[Resume from last_offset]
  catch --> alarms
```
