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

Done when tests pass and demo totals match produced events. Do not stop on an arbitrary turn cap.
