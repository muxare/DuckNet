# Step 1 — as-built architecture

**Branch:** `step-1` → `main`  
**Punchline:** transport lies (at-least-once); the consumer does not. Counts stay exact under duplicates. `--mis-demo` turns the inbox off so totals drift on purpose.

Single process. Hostility is **duplication only** (same `EventId`). No shuffle, no sequencer, no durable log.

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws `Hostile bus → Duplicator` as two boxes. In code, `DuplicatorMiddleware` *is* the `IEventBus` the producer talks to; it wraps `InMemoryEventBus`. Inbox lives on the consumer, not on the bus.

## Delta vs Step 0

| | |
|--|--|
| **Added** | `DuplicatorMiddleware` (wraps `IEventBus`), `Inbox` (`HashSet<Guid>`, per consumer group, in-memory), mis-demo flag, skip log `Skipping duplicate {EventId}` |
| **Changed** | `SqueakCounter` consults inbox before counting; `KernelRunner` / `Program` wrap the bus, flush delayed clones, wait on `AttemptCount`, cancel consumer after drain |
| **Unchanged** | `DuckSimulator` (still `IEventBus` only), `EventEnvelope` shape, `InMemoryEventBus` channel, no Center-to-Center, no shared DB |

## Wire types

Dedup key is **`EventId`**, never payload or duck id. Clones are the same envelope instance (same id).

```text
EventEnvelope                          Step 1 use
  EventId          Guid                inbox key; duplicates keep this id
  Type             string              "Squeaked" — others ignored
  Version          int                 1
  PartitionKey     string              duckId — not used for ordering yet
  SequenceNumber   long                per-duck — not enforced yet
  OccurredAt       DateTimeOffset
  PayloadJson      string              Squeaked v1
```

Config that changes behavior:

| Knob | Default | Effect |
|------|---------|--------|
| `DUPLICATE_RATE` / `--duplicate-rate` | 0.15 | Probability of a second `PublishAsync` with the same `EventId` |
| delayed re-enqueue | 40ms max in live demo; 0 in tests | Clone after `Task.Delay`; `FlushAsync` waits for pending clones |
| inbox enabled | on | `--mis-demo` / `--disable-inbox` / `INBOX_ENABLED=false` always handles |

## Architecture

Inbox is consumer-owned. Putting dedup inside the bus would break the later bus swap (Step 11).

```mermaid
flowchart TB
  subgraph Process["DuckNet.Kernel — one process"]
    KR["KernelRunner / Program"]

    subgraph Producer["Producer — unchanged types"]
      SIM["DuckSimulator"]
      SEQ["sequenceByDuck"]
      SIM --- SEQ
    end

    subgraph Transport["Transport — IEventBus"]
      DUP["DuplicatorMiddleware<br/>rate P, optional delay"]
      BUS["InMemoryEventBus"]
      CH["Channel of EventEnvelope"]
      DUP -->|"inner Publish / Subscribe"| BUS
      BUS --> CH
      DUP -->|"P: clone same EventId"| CH
    end

    subgraph Consumer["Consumer — owns idempotency"]
      CNT["SqueakCounter<br/>group: squeak-counter"]
      INB["Inbox<br/>HashSet of EventId<br/>in-memory only"]
      TOT["TotalCount = unique handles"]
      ATT["AttemptCount = Squeaked seen"]
      BY["CountsByDuck"]
      CNT --> INB
      CNT --- TOT
      CNT --- ATT
      CNT --- BY
    end
  end

  SIM -->|"PublishAsync"| DUP
  CH --> CNT
  SIM -.->|still no type reference| CNT
  DUP -.->|must not contain| INB
```

```mermaid
classDiagram
  class IEventBus {
    <<interface>>
    PublishAsync(EventEnvelope)
    SubscribeAsync(consumerGroup)
  }
  class InMemoryEventBus
  class DuplicatorMiddleware {
    duplicateRate
    DuplicateCount
    FlushAsync()
  }
  class Inbox {
    ConsumerGroup
    enabled
    DuplicateSkipCount
    ShouldHandle(EventId) bool
    MarkProcessed(EventId)
  }
  class SqueakCounter {
    AttemptCount
    TotalCount
    CountsByDuck
  }
  class DuckSimulator {
    PublishedCount
  }
  DuplicatorMiddleware ..|> IEventBus
  InMemoryEventBus ..|> IEventBus
  DuplicatorMiddleware --> IEventBus : inner
  DuckSimulator --> IEventBus : DuplicatorMiddleware at runtime
  SqueakCounter --> IEventBus : same wrapper
  SqueakCounter --> Inbox : consumer-owned
```

**Intentionally absent:** `ShufflerMiddleware`, `PerKeySequencer`, SQLite inbox/outbox/log, second Center.

## Execution — unique squeak then duplicate delivery

Happy path (inbox on). The second delivery is the duplicator, not the producer.

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Dup as DuplicatorMiddleware
  participant Ch as InMemoryEventBus Channel
  participant Cnt as SqueakCounter
  participant Inb as Inbox

  Sim->>Dup: PublishAsync(envelope EventId=X)
  Dup->>Ch: write original X
  Sim->>Sim: PublishedCount++

  alt random less than P
    Dup->>Dup: DuplicateCount++
    opt maxDelay greater than 0
      Dup->>Dup: Delay 1..maxDelay ms
    end
    Dup->>Ch: write clone X  same EventId
  end

  Cnt->>Ch: SubscribeAsync("squeak-counter")
  Ch-->>Cnt: envelope X  first delivery
  Cnt->>Cnt: AttemptCount++
  Cnt->>Inb: ShouldHandle(X)
  Inb-->>Cnt: true  not in set
  Cnt->>Cnt: TotalCount++  CountsByDuck++
  Cnt->>Inb: MarkProcessed(X)

  Ch-->>Cnt: envelope X  second delivery
  Cnt->>Cnt: AttemptCount++
  Cnt->>Inb: ShouldHandle(X)
  Inb-->>Cnt: false  already processed
  Inb->>Inb: DuplicateSkipCount++
  Cnt->>Cnt: log Skipping duplicate X
  Note over Cnt: TotalCount unchanged
```

## Execution — mis-demo (inbox disabled)

Same duplicator. `ShouldHandle` always returns true; `MarkProcessed` is a no-op. Totals include clones.

```mermaid
sequenceDiagram
  autonumber
  participant Dup as DuplicatorMiddleware
  participant Cnt as SqueakCounter
  participant Inb as Inbox enabled false

  Dup->>Cnt: envelope X
  Cnt->>Inb: ShouldHandle(X)
  Inb-->>Cnt: true  always
  Cnt->>Cnt: TotalCount++
  Cnt->>Inb: MarkProcessed(X)
  Note over Inb: no-op — set stays empty

  Dup->>Cnt: clone X
  Cnt->>Inb: ShouldHandle(X)
  Inb-->>Cnt: true  always
  Cnt->>Cnt: TotalCount++ again
  Note over Cnt: Counted = Published + Duplicates
```

## Execution — demo lifecycle

```mermaid
flowchart TD
  start([KernelRunner.RunDemoAsync]) --> inner[new InMemoryEventBus]
  inner --> dup[wrap DuplicatorMiddleware]
  dup --> inbox[new Inbox group squeak-counter]
  inbox --> cnt[SqueakCounter on wrapper]
  cnt --> sim[DuckSimulator on wrapper]
  sim --> run[start consumer<br/>await simulator duration]
  run --> flush[Duplicator.FlushAsync<br/>wait delayed clones]
  flush --> wait{AttemptCount less than<br/>Published + DuplicateCount<br/>and under 2s?}
  wait -->|yes| d[Delay 10ms]
  d --> wait
  wait -->|no| cancel[cancel consumer subscription]
  cancel --> result[RunResult:<br/>TotalCount, PublishedCount,<br/>DuplicateDeliveries, DuplicateSkips]
```

Invariant with inbox on: `TotalCount == PublishedCount` and `DuplicateSkips == DuplicateDeliveries`.  
Mis-demo at rate 1.0: `TotalCount == PublishedCount * 2`, `DuplicateSkips == 0`.

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type == Squeaked?}
  type -->|no| ignore[Ignore — not an attempt]
  type -->|yes| att[AttemptCount++]
  att --> inbox{Inbox.ShouldHandle EventId?}
  inbox -->|no| skip["Log Skipping duplicate EventId<br/>TotalCount unchanged"]
  inbox -->|yes| parse[Parse Squeaked]
  parse --> inc[TotalCount++  CountsByDuck++]
  inc --> mark[Inbox.MarkProcessed EventId]
  mark --> logevery{TotalCount mod logEvery == 0?}
  logevery -->|yes| print[Write processed=N]
  logevery -->|no| done([Next envelope])
  skip --> done
  print --> done
```

`ShouldHandle` false only when the inbox is enabled **and** `EventId` is already in the set. Mark happens **after** a successful handle so a crash mid-handle would retry (in-memory crash still loses the set — persistence is Step 3).
