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

Single-process kernel until Step 4. If the user names a step (1–3), follow that section in [PATTERNS.md](PATTERNS.md) first.

## Invariants

- Producer (`DuckSimulator`) never references consumer types. Integration is `IEventBus` only.
- Transport unit is `EventEnvelope`. Payload is JSON. Domain events (`Squeaked`) stay in `Domain/Events/`.
- `PartitionKey` = duck id. Sequence is per key, never global.
- `EventId` is the idempotency key. Duplicates keep the same id.
- `SubscribeAsync(consumerGroup, …)` — group is a logical subscriber. Do not ignore it when adding inbox/offsets.
- Do not implement later steps early. Hostile bus, inbox, sequencer, outbox land on their step branches.

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

## Changing the kernel

1. Keep producer and consumer coupled only through `IEventBus`.
2. New middleware wraps the bus — do not fork `IEventBus` per feature.
3. New consumer state (inbox, sequencer, offsets) is owned by the consumer, not the bus.
4. Tests: producer isolation, count correctness, then hostility (see PATTERNS.md).

## Verify (stop on evidence)

```bash
dotnet test
dotnet run --project src/DuckNet.Kernel -- --run-demo --seconds 5
```

Done when tests pass, demo totals match produced events with `Out of order == 0`, and `docs/architecture/step-N.md` has architecture + execution diagrams (see CLAUDE.md). Do not stop on an arbitrary turn cap.
