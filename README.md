# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit `Squeaked` facts; autonomous Centers react without calling each other or sharing a database.

Each step adds one distributed-systems idea and stays runnable end-to-end. Current kernel: **Step 1 — at-least-once delivery + idempotent consumer**.

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

## Demos (Step 1)

Inbox on — duplicates are injected, counts stay exact:

```bash
dotnet run --project src/DuckNet.Kernel -- --seconds 5
```

Inbox off — same hostility, counts drift (the teaching contrast):

```bash
dotnet run --project src/DuckNet.Kernel -- --mis-demo --seconds 5
```

| Flag / env | Default | Meaning |
|------------|---------|---------|
| `--seconds N` | 30 | How long the simulator runs |
| `--duplicate-rate` / `DUPLICATE_RATE` | 0.15 | Probability a publish is redelivered with the **same** `EventId` |
| `--mis-demo` / `--disable-inbox` / `INBOX_ENABLED=false` | inbox on | Disable idempotency so totals include duplicates |

Agent shortcuts: `/run-demo` `[seconds]`, `/mis-demo` `[seconds]`.

**What to look for:** with inbox on, `Published == Counted` and `Skipped == Duplicates`. With inbox off, `Counted == Published + Duplicates`.

## Layout

```
src/DuckNet.Kernel/     # single-process kernel until Step 4
  Transport/            # IEventBus, InMemoryEventBus, DuplicatorMiddleware
  Consumer/             # Inbox + SqueakCounter
  Producer/             # DuckSimulator
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
| 2+ | planned | See [ImplementationPlan.md](./ImplementationPlan.md) |

## Docs

- [docs/architecture/](./docs/architecture/) — as-built architecture + execution diagrams per completed step
- [ImplementationPlan.md](./ImplementationPlan.md) — step-by-step build plan and acceptance criteria
- [CLAUDE.md](./CLAUDE.md) — architecture rules for humans and agents
- [CentersBuildPlan.md](./CentersBuildPlan.md) — why the Centers exist
- [DuckNetArchitectureSteps.html](./DuckNetArchitectureSteps.html) — target architecture per step (roadmap)
- [docs/development-diary.md](./docs/development-diary.md) — what landed, with diagrams
