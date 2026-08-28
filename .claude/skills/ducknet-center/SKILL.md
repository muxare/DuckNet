---
name: ducknet-center
description: Scaffold or extend a DuckNet Center with its own SQLite schema, consumer group, inbox/offsets, and IEventBus subscription. Use when adding a Center, wiring Aspire, or enforcing no Center-to-Center references / no shared DB.
argument-hint: "[center-name]"
allowed-tools: Read, Edit, Write, Grep, Glob, Bash(dotnet *)
paths:
  - "src/DuckNet.*Center/**"
  - "src/DuckNet.AppHost/**"
  - "tests/DuckNet.*Center.Tests/**"
---

# ducknet-center

One process, one SQLite file, one consumer group. Integration is `IEventBus` only.

## Invariants

- No project reference from one Center to another. AppHost may reference Centers (orchestration).
- No Center opens another Center's database file. Telemetry owns `event_log` writes; others consume via `HttpLogClient` / `IEventBus` (`GET/POST /bus/events`).
- Events are past facts (`Squeaked`, `AlarmRaised`). Dedup key is `EventId`. Order is per `PartitionKey`.
- Envelope `Version` is a contract. Upcast in the consumer (`EventUpcasterPipeline`) before `Parse`. See skill `ducknet-event-contract`.
- Hostile middleware (duplicator, shuffler) applies **after** log read, on the consumer, never before append.
- Inbox + contiguous `last_offset` (+ Center side effects) commit in one transaction.
- Shard workers are consumer-owned (`Hash(PartitionKey) % SHARD_COUNT`). Same key → same shard. Do not put sharding inside `IEventBus`.

## Layout

```
src/DuckNet.{Name}Center/     # ASP.NET: /health + Center APIs
  {Name}App.cs                # composition; own KernelDb schema
src/DuckNet.AppHost/          # AddProject + EVENT_LOG_URL + health
src/DuckNet.Contracts/        # event records only
src/DuckNet.EventBus/         # IEventBus, hostile wrappers, HttpLogClient
infra/docker/DuckNet.{Name}Center/Dockerfile
```

## New Center checklist

1. Own schema in `CenterSchema` (or Center-local SQL). Include `inbox`, `consumer_offsets`, `outbox`, `dead_letter_queue`. Never copy Telemetry's `event_log` as a query path.
2. `SubscribeAsync(consumerGroup)` with a unique group name.
3. Handler: upcast → shard dispatch → sequencer (if keyed) → `RetryPipeline` → inbox → side effect → offset, one tx. Exhausted retries → DLQ + offset; do not mark inbox.
4. Publish via local outbox; dispatcher appends through the bus (`HttpLogClient.AppendAsync`), not by opening Telemetry SQLite.
5. Aspire: `AddProject`, `WithHttpHealthCheck("/health")`, `EVENT_LOG_URL` from telemetry HTTP endpoint. No `WithReference` used as a business client.
6. Tests: csproj isolation; catch-up from log while this Center was down; never opens Telemetry DB.
7. Dockerfile + path in `deploy-center.yml`.
8. `docs/architecture/step-N.md` when the step's acceptance criteria pass.

## Verify

```bash
dotnet test
dotnet run --project src/DuckNet.AppHost
```
