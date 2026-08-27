---
name: ducknet-kernel
description: Kernel patterns for DuckNet — EventEnvelope, IEventBus, producer/consumer isolation, and how inbox, per-key sequencer, and hostile-bus middleware must be added. Use when editing src/DuckNet.Kernel, kernel tests, or implementing Steps 0–3 (inbox, sequencer, duplicator, shuffler, outbox, event log).
argument-hint: "[step-number]"
allowed-tools: Read, Edit, Write, Grep, Glob, Bash(dotnet *)
paths:
  - "src/DuckNet.Kernel/**"
  - "tests/DuckNet.Kernel.Tests/**"
---

# ducknet-kernel

Single-process kernel console plus the durable primitives library used by Centers from Step 4. If the user names a step (1–3), follow that section in [PATTERNS.md](PATTERNS.md) first. For a new Center, use skill `ducknet-center`.

## Invariants

- Producer (`DuckSimulator`) never references consumer types. It writes through `TransactionalPublisher` (state + outbox), not `IEventBus`.
- Transport unit is `EventEnvelope`. Payload is JSON. Domain events (`Squeaked`) stay in `Domain/Events/`.
- `PartitionKey` = duck id. Sequence is per key, never global. Seq is assigned in the same transaction as the outbox row.
- `EventId` is the idempotency key. Duplicates keep the same id.
- `SubscribeAsync(consumerGroup, …)` — group is a logical subscriber. Inbox and offsets are keyed by group.
- Hostile middleware applies **after** log read, never before append.
- Do not implement later steps early. Dashboard / schema evolution land on Steps 5–6. RabbitMQ is Step 11.

## Step 0 map

```
DuckSimulator → IEventBus (InMemoryEventBus / Channel) → SqueakCounter
```

| Piece | Role |
|-------|------|
| `EventEnvelope` | Metadata + `PayloadJson` |
| `SqueakedEnvelope` | Serialize/parse `Squeaked` v1 |
| `InMemoryEventBus` | Unbounded channel; no hostility yet |
| `DuckSimulator` | N ducks, per-duck seq, optional seed |
| `SqueakCounter` | Totals; skip unknown `Type` |
| `KernelRunner` | Headless demo for tests |

## Step 1 map

```
DuckSimulator → DuplicatorMiddleware → Inbox → SqueakCounter
```

| Piece | Role |
|-------|------|
| `DuplicatorMiddleware` | Wraps `IEventBus`; re-publishes with the **same** `EventId` at rate `P` |
| `Inbox` | Consumer-owned `HashSet<Guid>`; skip if seen; mark after handle |
| `SqueakCounter` | Counts unique squeaks; logs `Skipping duplicate {EventId}` |
| Mis-demo | `INBOX_ENABLED=false` / `--disable-inbox` — counts drift on purpose |

## Step 2 map

```
DuckSimulator → DuplicatorMiddleware → ShufflerMiddleware → PerKeySequencer → Inbox → SqueakCounter
```

| Piece | Role |
|-------|------|
| `ShufflerMiddleware` | Wraps `IEventBus`; windowed shuffle (default 50); `FlushAsync` releases the remainder |
| `PerKeySequencer` | Per `PartitionKey`: emit if `seq == nextExpected`, buffer if ahead, drop if late |
| Gap timeout | Log after 5s; do **not** invent missing events |
| `SqueakCounter` | Counts unique squeaks; `OutOfOrderCount` if handle seq ≠ last+1 |
| Mis-demo | `--mis-demo` disables inbox **and** sequencer — counts drift, order breaks |

Ordering is per `PartitionKey`, never global. Compose: duplicator wraps shuffler wraps `InMemoryEventBus`.

## Step 3 map

```
Simulator → Tx(state + outbox) → OutboxDispatcher → event_log
  → LogTailFeeder → Duplicator → Shuffler → PerKeySequencer → Inbox → Handler
  Handler checkpoints inbox + squeak_counts + contiguous last_offset in one tx
```

| Piece | Role |
|-------|------|
| `TransactionalPublisher` | One SQLite tx: increment `duck_state`, insert `outbox` |
| `OutboxDispatcher` | Unpublished outbox → `event_log` → mark `published_at` |
| `LogTailFeeder` | Read `event_log` after offset; `PublishAsync` onto hostile bus |
| `ConsumerCheckpoint` | Inbox insert + counts + contiguous `last_offset` in one tx |
| `PerKeySequencer` | Seeded from persisted last seq on restart |
| Mis-demo | `--mis-demo` still disables inbox **and** sequencer; log/outbox stay on |

`last_offset` is a contiguous prefix (shuffle can deliver offsets out of order). Demo file: `ducknet-kernel.db`; `--reset-db` for a clean start.

## Changing the kernel

1. Keep producer and consumer coupled only through the log/bus. Producer does not reference consumer types.
2. New middleware wraps the bus — do not fork `IEventBus` per feature.
3. New consumer state (inbox, sequencer, offsets) is owned by the consumer, not the bus.
4. Tests: producer isolation, count correctness, hostility, then crash/restart/replay (see PATTERNS.md).

## Verify (stop on evidence)

```bash
dotnet test
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5
```

Done when tests pass, demo `Log rows == Counted` with `Out of order == 0` on a fresh DB, and `docs/architecture/step-N.md` has architecture + execution diagrams (see CLAUDE.md). Do not stop on an arbitrary turn cap.
