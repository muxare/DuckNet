# Step 2 — as-built architecture

**Branch:** `step-2`  
**Punchline:** transport also lies about order. The consumer restores per-`PartitionKey` sequence. Counts stay exact; per-duck seq stays monotonic. `--mis-demo` turns inbox **and** sequencer off so totals drift and handles go out of order.

Single process. Hostility is **duplication + windowed shuffle**. Ordering is **per partition key, never global**. No durable log, no outbox.

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws `Hostile bus → Duplicator` and `Shuffler` as sibling boxes. In code, `DuplicatorMiddleware` wraps `ShufflerMiddleware`, which wraps `InMemoryEventBus`. Sequencer and inbox live on the consumer, not on the bus.

## Delta vs Step 1

| | |
|--|--|
| **Added** | `ShufflerMiddleware` (windowed shuffle + `FlushAsync`), `PerKeySequencer` (buffer / emit / late-drop / gap log), `OutOfOrderCount`, `--no-shuffle` / `--shuffle-window` / `--disable-sequencer` |
| **Changed** | `SqueakCounter` offers each delivery to the sequencer *then* the inbox; `KernelRunner` / `Program` compose duplicator→shuffler, flush clones then the shuffle remainder; `--mis-demo` now disables inbox **and** sequencer |
| **Unchanged** | `DuckSimulator` (`IEventBus` only; `PartitionKey` = duck id, seq per duck), `EventEnvelope` shape, `InMemoryEventBus` channel, inbox still in-memory `HashSet<Guid>`, no Center-to-Center, no shared DB |

## Wire types

Dedup key is still **`EventId`**. Order key is **`PartitionKey` + `SequenceNumber`** (per key, never a global clock).

```text
EventEnvelope                          Step 2 use
  EventId          Guid                inbox key; duplicates keep this id
  Type             string              "Squeaked" — others ignored
  Version          int                 1
  PartitionKey     string              duckId — sequencer key
  SequenceNumber   long                per-duck monotonic, starts at 1
  OccurredAt       DateTimeOffset
  PayloadJson      string              Squeaked v1
```

Config that changes behavior:

| Knob | Default | Effect |
|------|---------|--------|
| `DUPLICATE_RATE` / `--duplicate-rate` | 0.15 | Probability of a second `PublishAsync` with the same `EventId` |
| `SHUFFLE_ENABLED` / `--no-shuffle` | on | Windowed shuffle of the publish stream |
| `SHUFFLE_WINDOW` / `--shuffle-window` | 50 | Shuffle batch size; remainder shuffled on `FlushAsync` |
| sequencer | on | `--disable-sequencer` / `SEQUENCER_ENABLED=false` pass-through |
| inbox | on | `--disable-inbox` / `INBOX_ENABLED=false` always handles |
| `--mis-demo` | defenses on | Disables **inbox and sequencer** (naive consumer) |
| gap timeout | 5s | Log only; do not invent or skip the missing seq |

## Architecture

Inbox and sequencer are consumer-owned. Putting either inside the bus would break the later bus swap (Step 11). Shuffle randomizes **across** keys; the sequencer only promises order **within** a key.

```mermaid
flowchart TB
  subgraph Process["DuckNet.Kernel — one process"]
    KR["KernelRunner / Program"]

    subgraph Producer["Producer — IEventBus only"]
      SIM["DuckSimulator"]
      SEQ["sequenceByDuck<br/>PartitionKey = duckId"]
      SIM --- SEQ
    end

    subgraph Transport["Transport — IEventBus wrappers"]
      DUP["DuplicatorMiddleware<br/>rate P, optional delay"]
      SHF["ShufflerMiddleware<br/>window N, Fisher-Yates"]
      BUS["InMemoryEventBus"]
      CH["Channel of EventEnvelope"]
      DUP -->|"inner Publish / Subscribe"| SHF
      SHF -->|"flush shuffled window"| BUS
      BUS --> CH
    end

    subgraph Consumer["Consumer — owns order + idempotency"]
      CNT["SqueakCounter<br/>group: squeak-counter"]
      PK["PerKeySequencer<br/>nextExpected + buffer per key"]
      INB["Inbox<br/>HashSet of EventId"]
      TOT["TotalCount = unique handles"]
      OO["OutOfOrderCount"]
      CNT --> PK
      CNT --> INB
      CNT --- TOT
      CNT --- OO
    end
  end

  SIM -->|"PublishAsync"| DUP
  CH --> CNT
  SIM -.->|still no type reference| CNT
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
  class InMemoryEventBus
  class ShufflerMiddleware {
    windowSize
    FlushAsync()
  }
  class DuplicatorMiddleware {
    duplicateRate
    FlushAsync()
  }
  class PerKeySequencer {
    Offer(envelope)
    ReportGaps(timeout)
    LateDropCount
  }
  class Inbox {
    ShouldHandle(EventId) bool
    MarkProcessed(EventId)
  }
  class SqueakCounter {
    AttemptCount
    TotalCount
    OutOfOrderCount
  }
  DuplicatorMiddleware ..|> IEventBus
  ShufflerMiddleware ..|> IEventBus
  InMemoryEventBus ..|> IEventBus
  DuplicatorMiddleware --> IEventBus : ShufflerMiddleware
  ShufflerMiddleware --> IEventBus : InMemoryEventBus
  DuckSimulator --> IEventBus : DuplicatorMiddleware at runtime
  SqueakCounter --> IEventBus : same wrapper
  SqueakCounter --> PerKeySequencer : consumer-owned
  SqueakCounter --> Inbox : consumer-owned
```

**Intentionally absent:** SQLite inbox/outbox/log, second Center, DLQ, global ordering.

## Execution — shuffled deliveries restored per key

Happy path. Cross-key order is not constrained (`B1` may land before `A1`).

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Dup as DuplicatorMiddleware
  participant Shf as ShufflerMiddleware
  participant Cnt as SqueakCounter
  participant Seq as PerKeySequencer
  participant Inb as Inbox

  Sim->>Dup: A1 then A2 then B1
  Dup->>Shf: originals (+ clones same EventId)
  Note over Shf: window fills or FlushAsync
  Shf->>Cnt: e.g. B1, A2, A1

  Cnt->>Seq: Offer B1
  Seq-->>Cnt: emit B1
  Cnt->>Inb: ShouldHandle B1
  Inb-->>Cnt: true
  Cnt->>Cnt: handle B1  lastSeq[B]=1

  Cnt->>Seq: Offer A2
  Seq-->>Cnt: buffer A2  nextExpected[A]=1
  Cnt->>Seq: Offer A1
  Seq-->>Cnt: emit A1 then drain A2
  Cnt->>Inb: ShouldHandle A1 then A2
  Cnt->>Cnt: handle A1 then A2
  Note over Cnt: OutOfOrderCount stays 0
```

## Execution — late duplicate (sequencer before inbox)

Clones keep the same `EventId` **and** the same `SequenceNumber`. After that seq has been emitted, the sequencer drops the clone; the inbox often never sees it (`Inbox skips` may stay 0). Buffered clones overwrite the same seq slot instead.

```mermaid
sequenceDiagram
  autonumber
  participant Dup as DuplicatorMiddleware
  participant Seq as PerKeySequencer
  participant Inb as Inbox
  participant Cnt as SqueakCounter

  Dup->>Seq: envelope X seq=5  first
  Seq-->>Cnt: emit 5
  Cnt->>Inb: MarkProcessed X

  Dup->>Seq: clone X seq=5
  Seq-->>Seq: seq less than nextExpected
  Seq-->>Cnt: drop  LateDropCount++
  Note over Inb: not consulted
  Note over Cnt: TotalCount unchanged
```

## Execution — mis-demo (inbox and sequencer off)

Same hostile bus. Handles follow shuffled delivery order. Totals include clones.

```mermaid
sequenceDiagram
  autonumber
  participant Shf as ShufflerMiddleware
  participant Cnt as SqueakCounter sequencer false
  participant Inb as Inbox enabled false

  Shf->>Cnt: A2 then A1 then clone A1
  Cnt->>Inb: ShouldHandle always true
  Cnt->>Cnt: handle A2  OutOfOrderCount++
  Cnt->>Cnt: handle A1  OutOfOrderCount++
  Cnt->>Cnt: handle clone  TotalCount++ again
  Note over Cnt: Counted greater than Published
```

## Execution — demo lifecycle

```mermaid
flowchart TD
  start([KernelRunner.RunDemoAsync]) --> inner[new InMemoryEventBus]
  inner --> shf[wrap ShufflerMiddleware]
  shf --> dup[wrap DuplicatorMiddleware]
  dup --> seq{sequencer enabled?}
  seq -->|yes| pk[new PerKeySequencer]
  seq -->|no| none[passthrough]
  pk --> cnt[SqueakCounter]
  none --> cnt
  cnt --> sim[DuckSimulator on wrapper]
  sim --> run[start consumer<br/>await simulator duration]
  run --> flushDup[Duplicator.FlushAsync]
  flushDup --> flushShf[Shuffler.FlushAsync]
  flushShf --> wait{AttemptCount less than<br/>Published + DuplicateCount<br/>and under 2s?}
  wait -->|yes| d[Delay 10ms]
  d --> wait
  wait -->|no| cancel[cancel consumer]
  cancel --> result[RunResult: TotalCount, OutOfOrderCount,<br/>SequencerLateDrops, DuplicateSkips]
```

Invariant with defenses on: `TotalCount == PublishedCount` and `OutOfOrderCount == 0`.  
Mis-demo: `TotalCount > PublishedCount` when duplicates were injected; `OutOfOrderCount` is usually &gt; 0 with shuffle on.

## Handler decision

```mermaid
flowchart TD
  recv[Receive EventEnvelope] --> type{Type == Squeaked?}
  type -->|no| ignore[Ignore]
  type -->|yes| att[AttemptCount++]
  att --> seqon{sequencer on?}
  seqon -->|no| ready[Handle this envelope]
  seqon -->|yes| offer[PerKeySequencer.Offer]
  offer --> cmp{incoming seq vs nextExpected}
  cmp -->|less| late["LateDropCount++<br/>do not handle"]
  cmp -->|greater| buf["Buffer by seq<br/>WaitingSince if first gap"]
  cmp -->|equal| emit[Emit then drain consecutive buffer]
  emit --> ready
  ready --> inbox{Inbox.ShouldHandle EventId?}
  inbox -->|no| skip["Skipping duplicate EventId"]
  inbox -->|yes| order{seq == last for duck + 1?}
  order -->|no| oo[OutOfOrderCount++ log]
  order -->|yes| inc[TotalCount++ CountsByDuck++]
  oo --> inc
  inc --> mark[Inbox.MarkProcessed]
  buf --> gaps[ReportGaps 5s log only]
  late --> gaps
  mark --> gaps
  skip --> gaps
  gaps --> done([Next envelope])
  ignore --> done
```

Gap after 5s: log `Gap on {key}: waiting for seq N, buffered [...]`. Do not invent the missing event and do not skip the gap. Real systems would DLQ or alert (Step 7).
