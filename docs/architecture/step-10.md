# Step 10 — as-built architecture

**Branch:** `step-10`  
**Punchline:** a fee is reserved and released without a distributed transaction. BillingCenter owns a saga row. AlarmCenter never calls it. Time is the compensator: `AlarmResolved` before expiry → `FeeReleased` (reason `AlarmResolved`); still `Reserved` after 5 minutes → `FeeReleased` (reason `Timeout`). Duplicate `AlarmRaised` cannot double-charge (inbox `EventId` + saga PK `alarm_id`).

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws BillingCenter + compensating events. In code `alarm_id` is the `AlarmRaised` `EventId` (the catalog has no separate alarm id). Aspire sets `SAGA_TIMEOUT_SECONDS=15` so the timeout path is visible; the default is 5 minutes. Fast resolve is `POST /alarms/{duckId}/resolve` on AlarmCenter (operator fact, not a Center-to-Center call).

## Delta vs Step 9

| | |
|--|--|
| **Added** | BillingCenter (own SQLite, `billing_sagas`, timeout worker), `AlarmResolved` / `FeeReserved` / `FeeReleased`, `POST /alarms/{duckId}/resolve`, `GET /sagas` |
| **Changed** | AlarmCenter publishes `AlarmResolved` when the event-time window drops below threshold (or on operator resolve). `AlarmRaised` still sticky per duck. |
| **Unchanged** | No Center-to-Center business HTTP. No shared DB. Hostile transport after log read. Upcast, inbox, sequencer, retry, DLQ, shards, envelope `TraceId`. |

## Wire types

Dedup key is still **`EventId`**. Saga identity is **`AlarmRaised.EventId`** stored as `billing_sagas.alarm_id`.

```text
EventEnvelope                          Step 10 use
  EventId          Guid                inbox key; AlarmRaised EventId = alarm_id
  Type             string              AlarmRaised | AlarmResolved | FeeReserved | FeeReleased
  PartitionKey     string              duckId (alarm events) / alarmId (fee events)
  SequenceNumber   long                per-duck alarm seq; fee events 1 then 2 per alarm
  TraceId          string?             copied AlarmRaised → FeeReserved
  CausationId      string?             AlarmResolved ← AlarmRaised EventId
                                       FeeReserved  ← AlarmRaised EventId
                                       FeeReleased  ← AlarmResolved EventId (or alarm_id on timeout)

AlarmRaised v1     duckId, rate, windowStart
AlarmResolved v1   duckId, resolvedAt
FeeReserved v1     alarmId, duckId, amountCents, expiresAt
FeeReleased v1     alarmId, reason   // AlarmResolved | Timeout
```

Saga states: `Reserved` | `Released` | `Expired`. Timeout and resolve race on `UPDATE … WHERE state='Reserved'` in this Center's SQLite — not a distributed lock.

## Architecture

`IEventBus` is still the only integration seam. Billing never opens Alarm SQLite. Alarm never opens Billing SQLite.

```mermaid
flowchart TB
  subgraph Aspire["DuckNet.AppHost"]
    subgraph TC["TelemetryCenter — own SQLite"]
      SIM["DuckSimulator"]
      TDB[("telemetry.db<br/>event_log")]
      BUSAPI["GET/POST /bus/events"]
      SIM --> TDB --> BUSAPI
    end

    subgraph Transport["IEventBus adapter — not a business API"]
      HTTP["HttpLogClient / HttpLogTailFeeder"]
      DUP["DuplicatorMiddleware"]
      SHF["ShufflerMiddleware"]
      MEM["InMemoryEventBus"]
      BUSAPI -->|"HTTP after log"| HTTP
      HTTP --> DUP --> SHF --> MEM
    end

    subgraph AC["AlarmCenter — own SQLite"]
      ALM["handle.Squeaked"]
      ADB[("alarm.db")]
      MEM --> ALM --> ADB
      ALM -->|"outbox AlarmRaised / AlarmResolved"| BUSAPI
    end

    subgraph DC["DashboardCenter — own SQLite"]
      CONS["handle.Squeaked"]
      DDB[("dashboard.db")]
      MEM --> CONS --> DDB
    end

    subgraph BC["BillingCenter — own SQLite"]
      SAGA["handle.AlarmRaised / AlarmResolved"]
      TMO["timeout worker"]
      BDB[("billing.db<br/>billing_sagas")]
      MEM --> SAGA --> BDB
      TMO --> BDB
      SAGA -->|"outbox FeeReserved / FeeReleased"| BUSAPI
      TMO -->|"outbox FeeReleased Timeout"| BUSAPI
    end
  end

  ALM -.->|never opens| TDB
  CONS -.->|never opens| TDB
  SAGA -.->|never opens| TDB
  SAGA -.->|never calls| ALM
  ALM -.->|never calls| SAGA
```

**Intentionally absent:** RabbitMQ, MCP ops server, Azure Monitor exporter.

## Execution — happy path vs timeout

```mermaid
sequenceDiagram
  autonumber
  participant Alarm as AlarmCenter
  participant Log as event_log
  participant Bill as BillingCenter
  participant Clock as timeout worker

  Alarm->>Log: AlarmRaised EventId=A
  Log->>Bill: Subscribe clone may duplicate
  Bill->>Bill: inbox + INSERT saga Reserved PK=A
  Bill->>Log: FeeReserved alarmId=A
  alt AlarmResolved before expires_at
    Alarm->>Log: AlarmResolved CausationId=A
    Log->>Bill: handle.AlarmResolved
    Bill->>Bill: UPDATE Reserved → Released
    Bill->>Log: FeeReleased reason=AlarmResolved
  else still Reserved after timeout
    Clock->>Bill: expires_at <= now
    Bill->>Bill: UPDATE Reserved → Expired
    Bill->>Log: FeeReleased reason=Timeout
  end
```

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type?}
  type -->|Squeaked / Fee*| skip[advance offset — not our stream]
  type -->|AlarmRaised / AlarmResolved| seq[Sequencer Offer per duckId]
  seq --> retry[RetryPipeline]
  retry --> inbox{inbox insert EventId?}
  inbox -->|duplicate| tag["tag ducknet.duplicate=true<br/>no second fee"]
  inbox -->|new + AlarmRaised| pk{INSERT saga PK?}
  pk -->|yes| res[state Reserved + outbox FeeReserved]
  pk -->|ignore| done[already reserved]
  inbox -->|new + AlarmResolved| st{state is Reserved?}
  st -->|yes| rel[state Released + outbox FeeReleased]
  st -->|no| ignore[Expired or missing — no-op]
  clock[timeout poll] --> due{Reserved and expires_at <= now?}
  due -->|yes| exp[state Expired + outbox FeeReleased Timeout]
  due -->|no| wait[sleep]
```

## Demo lifecycle

```mermaid
flowchart TD
  aspire[dotnet run AppHost] --> bill[GET billing /sagas]
  bill --> fast["POST alarm /alarms/duck-1/resolve → Released"]
  bill --> slow["wait SAGA_TIMEOUT_SECONDS=15 → Expired"]
```

Open the Aspire dashboard. `telemetry`, `alarm`, `dashboard`, and `billing` should be healthy. `GET /sagas` on billing lists rows. Fast path: `POST /alarms/{duckId}/resolve` on alarm after a raise. Slow path: leave the alarm active; after 15s the timeout worker publishes `FeeReleased` with reason `Timeout`.
