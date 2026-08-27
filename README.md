# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit `Squeaked` facts; autonomous Centers react without calling each other or sharing a database.

Each step adds one distributed-systems idea and stays runnable end-to-end. Current kernel: **Step 3 — durability (append-only log + outbox)**.

## Rules

1. No Center-to-Center calls. Integration is events only.
2. No shared database. Each Center owns its schema.
3. Events are past facts (`Squeaked`, not `SqueakTheDuck`).
4. Transport is hostile: at-least-once, unordered across keys.
5. Every step stays runnable.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

## Demos (Step 3)

Defenses on — producer writes state+outbox in one transaction; the log is the source of truth; duplicates and shuffle hit the tail feeder. Counts stay exact and survive restart:

```bash
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5
```

Kill the process and run again **without** `--reset-db` — lifetime `Counted` continues from `ducknet-kernel.db` (`Log offset` is the consumer’s contiguous prefix).

Naive consumer — inbox and sequencer off, so totals drift and handles go out of order:

```bash
dotnet run --project src/DuckNet.Kernel -- --mis-demo --reset-db --seconds 5
```

| Flag / env | Default | Meaning |
|------------|---------|---------|
| `--seconds N` | 30 | How long the simulator runs |
| `--db` / `DUCKNET_DB` | `ducknet-kernel.db` | SQLite file for this Center |
| `--reset-db` | off | Delete the DB before start |
| `--duplicate-rate` / `DUPLICATE_RATE` | 0.15 | Probability a log-tail publish is redelivered with the **same** `EventId` |
| `--no-shuffle` / `SHUFFLE_ENABLED=false` | shuffle on | Disable windowed reorder |
| `--shuffle-window` / `SHUFFLE_WINDOW` | 50 | Shuffle batch size |
| `--disable-sequencer` / `SEQUENCER_ENABLED=false` | sequencer on | Pass-through; shuffled order reaches the handler |
| `--disable-inbox` / `INBOX_ENABLED=false` | inbox on | Disable idempotency |
| `--mis-demo` | defenses on | Disable **inbox and sequencer** (teaching contrast) |

Agent shortcuts: `/run-demo` `[seconds]`, `/mis-demo` `[seconds]`.

**What to look for:** with defenses on, `Published (session) == Counted (lifetime)` on a fresh DB, `Log rows == Counted`, and `Out of order == 0`. After a restart without `--reset-db`, lifetime `Counted` equals `Log rows`. With `--mis-demo`, `Counted > Log rows` and `Out of order > 0`. Ordering is per duck, never global.

## Layout

```
src/DuckNet.Kernel/     # single-process kernel until Step 4
  Transport/            # IEventBus, hostile wrappers, LogTailFeeder
  Consumer/             # Inbox + PerKeySequencer + checkpoint
  Producer/             # DuckSimulator, TransactionalPublisher, OutboxDispatcher
  Persistence/          # SQLite: log, outbox, inbox, offsets, counts
tests/                  # unit + integration tests
.github/workflows/      # ci.yml, claude-review.yml, claude.yml
```

## Roadmap

| Phase | Steps | Outcome |
|-------|-------|---------|
| A — Kernel | 0–3 | At-least-once, idempotency, per-key ordering, durable log + outbox |
| B — Distributed | 4–6 | Multi-Center Aspire host, CQRS projections, schema evolution |
| C — Production pain | 7–11 | DLQ, hot partitions, tracing, sagas, broker swap |
| D — Cloud & ops | 12+ | Azure hosting, per-Center CI/CD |

| Step | Status | Demo punchline |
|------|--------|----------------|
| 0 | complete | One producer, one consumer, counts match |
| 1 | complete | Forced duplicates + inbox → counts still match |
| 2 | complete | Shuffle + per-key sequencer → order and counts still match |
| 3 | in progress | Durable log + outbox → kill/restart, no double-count |
| 4+ | planned | See [ImplementationPlan.md](./ImplementationPlan.md) |

## Docs

- [docs/architecture/](./docs/architecture/) — as-built architecture + execution diagrams per completed step
- [ImplementationPlan.md](./ImplementationPlan.md) — step-by-step build plan and acceptance criteria
- [CLAUDE.md](./CLAUDE.md) — architecture rules for humans and agents
- [CentersBuildPlan.md](./CentersBuildPlan.md) — why the Centers exist
- [DuckNetArchitectureSteps.html](./DuckNetArchitectureSteps.html) — target architecture per step (roadmap)
- [docs/development-diary.md](./docs/development-diary.md) — what landed, with diagrams
