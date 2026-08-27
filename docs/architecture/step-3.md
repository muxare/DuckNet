# Step 3 — as-built architecture

**Branch:** `step-3`  
**Punchline:** the log is the source of truth. The producer writes **state + outbox** in one transaction; a dispatcher appends the log; a tail feeder publishes through the hostile bus. The consumer checkpoints **inbox + counts + offset** in one transaction. Kill + restart resumes; no loss, no double-count.

Single process. One SQLite file is this Center’s database (until Step 4). Hostility (dup + shuffle) is applied **after** log read, never before append.

**HTML roadmap note:** [DuckNetArchitectureSteps.html](../../DuckNetArchitectureSteps.html) draws dispatcher → log → hostile bus. In code, `OutboxDispatcher` only appends `event_log`; `LogTailFeeder` publishes each new row onto `DuplicatorMiddleware` → `ShufflerMiddleware` → `InMemoryEventBus`. Inbox, sequencer, and offset live on the consumer, not on the bus.

**Schema delta vs the plan:** `event_log` also stores `sequence_number` (needed to rebuild `EventEnvelope` without payload-type switching). Handler totals live in `squeak_counts` so a restart can continue counts. `last_offset` is a **contiguous prefix** of processed log offsets, because shuffle after log-read can deliver offsets out of order.

## Delta vs Step 2

| | |
|--|--|
| **Added** | SQLite (`KernelDb`), `duck_state` + `outbox` + `event_log` + `inbox` + `consumer_offsets` + `squeak_counts`, `TransactionalPublisher`, `OutboxDispatcher`, `LogTailFeeder`, `ConsumerCheckpoint`, `--db` / `--reset-db` |
| **Changed** | `DuckSimulator` writes through the outbox, not `IEventBus`. `SqueakCounter` checkpoints inbox+counts+offset in one transaction when a DB is present. `PerKeySequencer` can be seeded from persisted last seq. Demo default file is `ducknet-kernel.db` (survives `kill -9`) |
| **Unchanged** | `EventEnvelope` shape (plus `LogOffset` assigned at log append), hostile wrappers, per-key ordering, no Center-to-Center, no shared DB across Centers |

## Wire types

Dedup key is still **`EventId`**. Order key is **`PartitionKey` + `SequenceNumber`**. Resume key is **`LogOffset`** (contiguous prefix).

```text
EventEnvelope                          Step 3 use
  EventId          Guid                inbox key; duplicates keep this id
  Type             string              "Squeaked" — others ignored
  Version          int                 1
  PartitionKey     string              duckId — sequencer key
  SequenceNumber   long                per-duck monotonic, from duck_state in the same tx as outbox
  OccurredAt       DateTimeOffset
  PayloadJson      string              Squeaked v1
  LogOffset        long                0 until appended; then event_log.offset
```

Config that changes behavior:

| Knob | Default | Effect |
|------|---------|--------|
| `--db` / `DUCKNET_DB` | `ducknet-kernel.db` | SQLite path (tests use a unique temp file) |
| `--reset-db` | off | Delete the DB file before start |
| `DUPLICATE_RATE` / `--duplicate-rate` | 0.15 | Probability of a second `PublishAsync` with the same `EventId` **after** log read |
| `SHUFFLE_ENABLED` / `--no-shuffle` | on | Windowed shuffle of the tail-feeder stream |
| `SHUFFLE_WINDOW` / `--shuffle-window` | 50 | Shuffle batch size |
| sequencer | on | `--disable-sequencer` / `SEQUENCER_ENABLED=false` pass-through |
| inbox | on | `--disable-inbox` / `INBOX_ENABLED=false` always handles |
| `--mis-demo` | defenses on | Disables **inbox and sequencer** (naive consumer) |

## Architecture

The producer never references consumer types. The bus never owns inbox, sequencer, or offsets. Hostile middleware wraps **publish from the log tail**, not the outbox write.

```mermaid
flowchart TB
  subgraph Process["DuckNet.Kernel — one process, one SQLite file"]
    KR["KernelRunner / Program"]

    subgraph Producer["Producer — no IEventBus"]
      SIM["DuckSimulator"]
      TX["TransactionalPublisher"]
      ST[("duck_state")]
      OB[("outbox")]
      DSP["OutboxDispatcher"]
      LOG[("event_log")]
      SIM --> TX
      TX --> ST
      TX --> OB
      OB --> DSP
      DSP --> LOG
    end

    subgraph Transport["Transport — IEventBus wrappers after log read"]
      FEED["LogTailFeeder"]
      DUP["DuplicatorMiddleware"]
      SHF["ShufflerMiddleware"]
      BUS["InMemoryEventBus"]
      LOG --> FEED
      FEED -->|"PublishAsync"| DUP
      DUP --> SHF
      SHF --> BUS
    end

    subgraph Consumer["Consumer — owns order, idempotency, offset"]
      CNT["SqueakCounter"]
      PK["PerKeySequencer"]
      CK["ConsumerCheckpoint"]
      INB[("inbox")]
      OFF[("consumer_offsets")]
      SC[("squeak_counts")]
      BUS --> CNT
      CNT --> PK
      CNT --> CK
      CK --> INB
      CK --> OFF
      CK --> SC
    end
  end

  SIM -.->|still no type reference| CNT
  DUP -.->|must not contain| PK
  SHF -.->|must not contain| INB
```

```mermaid
classDiagram
  class TransactionalPublisher {
    PublishSqueakAsync(duckId)
  }
  class OutboxDispatcher {
    DrainAsync()
  }
  class LogTailFeeder {
    FeedBatchAsync(limit)
    CatchUpAsync()
  }
  class IEventBus {
    <<interface>>
    PublishAsync(EventEnvelope)
    SubscribeAsync(consumerGroup)
  }
  class ConsumerCheckpoint {
    TryCommit(handle) bool
  }
  class ConsumerOffsetStore {
    LastOffset
    MarkProcessed(offset)
  }
  DuckSimulator --> TransactionalPublisher
  TransactionalPublisher --> StateStore
  TransactionalPublisher --> OutboxStore
  OutboxDispatcher --> OutboxStore
  OutboxDispatcher --> EventLogStore
  LogTailFeeder --> EventLogStore
  LogTailFeeder --> IEventBus
  DuplicatorMiddleware ..|> IEventBus
  SqueakCounter --> IEventBus
  SqueakCounter --> PerKeySequencer
  SqueakCounter --> ConsumerCheckpoint
  ConsumerCheckpoint --> Inbox
  ConsumerCheckpoint --> ConsumerOffsetStore
  ConsumerCheckpoint --> SqueakCountStore
```

**Intentionally absent:** second Center, Aspire, PostgreSQL, DLQ, global ordering.

## Execution — transactional publish then log then hostile bus

Happy path. Cross-key order is not constrained after shuffle.

```mermaid
sequenceDiagram
  autonumber
  participant Sim as DuckSimulator
  participant Tx as TransactionalPublisher
  participant Db as SQLite
  participant Dsp as OutboxDispatcher
  participant Feed as LogTailFeeder
  participant Dup as DuplicatorMiddleware
  participant Cnt as SqueakCounter
  participant Ck as ConsumerCheckpoint

  Sim->>Tx: PublishSqueakAsync duck-1
  Tx->>Db: BEGIN
  Tx->>Db: duck_state last_seq += 1
  Tx->>Db: INSERT outbox
  Tx->>Db: COMMIT

  Dsp->>Db: unpublished outbox rows
  Dsp->>Db: BEGIN append event_log + mark published COMMIT

  Feed->>Db: ReadAfter last_offset
  Feed->>Dup: PublishAsync envelope LogOffset=N
  Dup->>Cnt: original (+ clone same EventId)

  Cnt->>Cnt: sequencer Offer
  Cnt->>Ck: TryCommit EventId + LogOffset
  Ck->>Db: BEGIN inbox + squeak_counts + contiguous last_offset COMMIT
```

## Execution — producer crash mid-transaction

Neither seq nor outbox row survives. Retry assigns the same next seq.

```mermaid
sequenceDiagram
  autonumber
  participant Tx as TransactionalPublisher
  participant Db as SQLite

  Tx->>Db: BEGIN
  Tx->>Db: duck_state last_seq = 4
  Tx->>Db: INSERT outbox
  Note over Tx,Db: process killed before COMMIT
  Db->>Db: ROLLBACK
  Note over Db: last_seq still 3, outbox empty
```

## Execution — consumer kill then restart

Offset is a contiguous prefix. Sequencer is reseeded from `squeak_counts.last_seq`. Inbox prevents double-count of already-committed EventIds.

```mermaid
sequenceDiagram
  autonumber
  participant Feed as LogTailFeeder
  participant Cnt as SqueakCounter
  participant Ck as ConsumerCheckpoint
  participant Db as SQLite

  Feed->>Cnt: offsets 1..4
  Cnt->>Ck: TryCommit each
  Ck->>Db: last_offset = 4, counts = 4
  Note over Cnt: kill consumer

  Feed->>Feed: new process startOffset = 4
  Feed->>Cnt: offsets 5..N
  Cnt->>Cnt: sequencer nextExpected = last_seq + 1
  Cnt->>Ck: TryCommit 5..N
  Note over Cnt: TotalCount continues from 4
```

## Execution — mis-demo (inbox and sequencer off)

Same durable log. Handles follow shuffled delivery order. Totals include clones. Offset still advances.

```mermaid
sequenceDiagram
  autonumber
  participant Feed as LogTailFeeder
  participant Cnt as SqueakCounter sequencer false
  participant Ck as ConsumerCheckpoint inbox false

  Feed->>Cnt: A2 then A1 then clone A1
  Cnt->>Ck: TryCommit always increments counts
  Cnt->>Cnt: OutOfOrderCount++
  Note over Cnt: Counted greater than log rows
```

## Execution — demo lifecycle

```mermaid
flowchart TD
  start([KernelRunner.RunDemoAsync]) --> db[KernelDb.Open]
  db --> restore[Load squeak_counts + last_offset]
  restore --> pub[TransactionalPublisher + DuckSimulator]
  pub --> hostile[Duplicator wraps Shuffler wraps InMemoryEventBus]
  hostile --> disp[OutboxDispatcher.RunAsync]
  disp --> feed[LogTailFeeder from last_offset]
  feed --> cnt[SqueakCounter + checkpoint]
  cnt --> run[await simulator duration]
  run --> drain[Dispatcher.DrainAsync]
  drain --> catch[Feeder.CatchUpAsync]
  catch --> flushDup[Duplicator.FlushAsync]
  flushDup --> flushShf[Shuffler.FlushAsync]
  flushShf --> wait{AttemptCount less than<br/>session Published + DuplicateCount<br/>and under 2s?}
  wait -->|yes| d[Delay 10ms]
  d --> wait
  wait -->|no| cancel[cancel loops]
  cancel --> result[RunResult: TotalCount lifetime,<br/>PublishedCount session, LogCount, LastOffset]
```

Invariant with defenses on: `TotalCount == LogCount` and `OutOfOrderCount == 0`. On a fresh DB, `PublishedCount == TotalCount`. A second session on the same file continues lifetime counts.

Mis-demo: `TotalCount > LogCount` when duplicates were injected; `OutOfOrderCount` is usually &gt; 0 with shuffle on.

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
  cmp -->|less| late["LateDropCount++<br/>do not checkpoint"]
  cmp -->|greater| buf["Buffer by seq"]
  cmp -->|equal| emit[Emit then drain consecutive buffer]
  emit --> ready
  ready --> durable{checkpoint present?}
  durable -->|no| memInbox{Inbox.ShouldHandle EventId?}
  memInbox -->|no| skip["Skipping duplicate EventId"]
  memInbox -->|yes| apply[In-memory count++]
  apply --> mark[Inbox.MarkProcessed]
  durable -->|yes| tx["BEGIN: inbox insert, counts++, contiguous offset"]
  tx --> applied{new EventId?}
  applied -->|no| skip
  applied -->|yes| mem[In-memory count++ after COMMIT]
  buf --> gaps[ReportGaps 5s log only]
  late --> gaps
  mark --> gaps
  mem --> gaps
  skip --> gaps
  gaps --> done([Next envelope])
  ignore --> done
```

Gap after 5s: log only. Do not invent the missing event. Real systems would DLQ or alert (Step 7).
