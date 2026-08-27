# Step 0 — as-built architecture

**Branch:** `step-0` → `main`  
**Punchline:** one producer, one consumer, counts match. Producer has zero references to consumer types.

Single process. No hostility, no inbox, no log, no second Center.

## Delta vs previous step

There is no previous step.

| | |
|--|--|
| **Added** | `Squeaked`, `EventEnvelope`, `IEventBus` / `InMemoryEventBus`, `DuckSimulator`, `SqueakCounter`, `KernelRunner` |
| **Changed** | — |
| **Unchanged** | — |

## Wire types

`EventEnvelope` is the only thing on the bus. Payload is JSON; domain type stays in `Domain/Events/`.

```text
EventEnvelope
  EventId          Guid     unique per emission (unused for dedup until Step 1)
  Type             string   "Squeaked"
  Version          int      1
  PartitionKey     string   duckId
  SequenceNumber   long     per-duck, assigned by producer
  OccurredAt       DateTimeOffset
  PayloadJson      string   Squeaked { DuckId, SequenceNumber, OccurredAt }
  TraceId          string?  unused
  CausationId      string?  unused
```

## Architecture

Producer and consumer share a process and an `IEventBus`. They do not share types: `DuckSimulator`’s constructor takes only `IEventBus`. The channel is unbounded and honest (exactly-once *in this step* only because nothing duplicates).

```mermaid
flowchart TB
  subgraph Process["DuckNet.Kernel — one process"]
    KR["KernelRunner / Program<br/>wires both sides, runs until duration"]

    subgraph Producer["Producer"]
      SIM["DuckSimulator"]
      SEQ["sequenceByDuck<br/>duckId → last seq"]
      SIM --- SEQ
    end

    subgraph Transport["Transport"]
      IFACE["IEventBus"]
      BUS["InMemoryEventBus"]
      CH["unbounded Channel of EventEnvelope<br/>no dup, no shuffle"]
      IFACE -.->|implemented by| BUS
      BUS --> CH
    end

    subgraph Consumer["Consumer"]
      CNT["SqueakCounter<br/>group: squeak-counter"]
      TOT["TotalCount"]
      BY["CountsByDuck"]
      CNT --- TOT
      CNT --- BY
    end
  end

  SIM -->|"PublishAsync"| IFACE
  CH -->|"SubscribeAsync(consumerGroup)"| CNT

  SIM -.->|no type reference| CNT
```

```mermaid
classDiagram
  class IEventBus {
    <<interface>>
    PublishAsync(EventEnvelope)
    SubscribeAsync(consumerGroup) IAsyncEnumerable
  }
  class InMemoryEventBus {
    Channel~EventEnvelope~
  }
  class EventEnvelope {
    EventId
    Type
    Version
    PartitionKey
    SequenceNumber
    OccurredAt
    PayloadJson
  }
  class Squeaked {
    DuckId
    SequenceNumber
    OccurredAt
  }
  class SqueakedEnvelope {
    <<static>>
    Create(Squeaked) EventEnvelope
    Parse(EventEnvelope) Squeaked
  }
  class DuckSimulator {
    IEventBus
    PublishedCount
    RunAsync(duration)
    PublishOneAsync(duckId)
  }
  class SqueakCounter {
    IEventBus
    consumerGroup
    TotalCount
    CountsByDuck
    RunAsync()
  }
  InMemoryEventBus ..|> IEventBus
  DuckSimulator --> IEventBus : publish only
  SqueakCounter --> IEventBus : subscribe only
  DuckSimulator ..> SqueakedEnvelope : Create
  SqueakCounter ..> SqueakedEnvelope : Parse
  SqueakedEnvelope ..> EventEnvelope
  SqueakedEnvelope ..> Squeaked
  DuckSimulator --> Squeaked
```

**Intentionally absent:** duplicator, inbox, sequencer, outbox, event log, second Center, persistence.

## Execution — one squeak

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Seq as sequenceByDuck
  participant Env as SqueakedEnvelope
  participant Bus as InMemoryEventBus
  participant Ch as Channel
  participant Cnt as SqueakCounter

  Sim->>Seq: NextSequence(duckId)
  Seq-->>Sim: n
  Sim->>Env: Create(Squeaked(duckId, n, now))
  Note over Env: EventId = new Guid<br/>PartitionKey = duckId
  Sim->>Bus: PublishAsync(envelope)
  Bus->>Ch: WriteAsync
  Sim->>Sim: PublishedCount++

  Cnt->>Bus: SubscribeAsync("squeak-counter")
  Bus->>Ch: ReadAllAsync
  Ch-->>Cnt: envelope
  alt Type != Squeaked
    Cnt-->>Cnt: continue
  else Type == Squeaked
    Cnt->>Env: Parse(envelope)
    Cnt->>Cnt: TotalCount++
    Cnt->>Cnt: CountsByDuck[duckId]++
  end
```

## Execution — demo lifecycle

```mermaid
flowchart TD
  start([KernelRunner.RunDemoAsync]) --> wire[new InMemoryEventBus]
  wire --> cnt[new SqueakCounter on bus]
  cnt --> sim[new DuckSimulator on same bus]
  sim --> parallel[start counter.RunAsync<br/>await simulator.RunAsync duration]
  parallel --> wait{TotalCount less than PublishedCount<br/>and under 2s deadline?}
  wait -->|yes| delay[Delay 10ms]
  delay --> wait
  wait -->|no| result[return TotalCount + CountsByDuck]
```

Consumer has no end-of-stream: the runner waits until counts catch up (or 2s), then the process ends / tests cancel the subscription.

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type == Squeaked?}
  type -->|no| skip[Ignore]
  type -->|yes| parse[Parse payload]
  parse --> inc[Increment TotalCount and CountsByDuck]
```

No inbox: every delivered `Squeaked` increments the counter. That is correct only while the bus does not redeliver.
