# Kernel patterns (Steps 1–3)

Do not add these on `main` / Step 0. Implement on `step-N`, keep the demo runnable.

Compose later as:

```
Simulator → Tx(state + outbox) → log → HostileBus(Duplicator, Shuffler)
  → PerKeySequencer → Inbox → handler
```

## Step 1 — Hostile duplicates + inbox

**Duplicator** wraps publish: with probability `P` (default ~0.15, env-configurable), re-enqueue a clone with the **same `EventId`**.

**Inbox** is consumer-owned: skip if `EventId` already processed; mark after successful handle.

- Step 1: in-memory `HashSet<Guid>` (or equivalent).
- Step 3: SQLite `inbox(consumer_group, event_id)` in the same transaction as the offset write.
- Mis-demo flag: disable inbox so counts drift, then turn it back on.

Tests: publish twice with the same id → handler runs once. 1000 events at 20% dup → exact count.

## Step 2 — Shuffle + per-key sequencer

**Shuffler** buffers a window and releases shuffled. Unordered **across** keys; never claim global order.

**PerKeySequencer** state per `PartitionKey`:

| Incoming seq | Action |
|--------------|--------|
| `== nextExpected` | emit, then drain buffer |
| `> nextExpected` | buffer |
| `< nextExpected` | duplicate — drop (inbox may also catch) |

Gap timeout (e.g. 5s): log only. Do not invent missing events.

Test: feed `(B1, A2, A1)` → per-key order `A1` then `A2`; `B1` independent.

## Step 3 — Log + outbox

Producer: one transaction for state + outbox row. Dispatcher: unpublished outbox → `event_log` → mark published. `LogTailFeeder` publishes log rows onto the hostile bus (dup + shuffle **after** log read).

Consumer: handle + inbox + counts + contiguous `last_offset` in one transaction. Kill/restart resumes from offset with no double-count. Sequencer is seeded from persisted last seq.

Tests: uncommitted tx writes neither side; restart from offset does not double-count; replay from 0 reproduces counts.

## Step 7 — Retry + DLQ

**RetryPipeline** wraps the handler (not the bus): catch, exponential backoff, max attempts (default 5). Exhausted → insert `dead_letter_queue` (this Center's SQLite) and still advance contiguous `last_offset`. Inbox is not marked.

Poison is a well-formed envelope with unparseable `PayloadJson`. Inject via `INJECT_POISON_EVENT`, `POST /bus/poison`, or kernel `--inject-poison`. Replay (`--replay-dlq` / `POST /dlq/{id}/replay?fix=true`) or skip (`--skip-dlq` / `POST /dlq/{id}/skip`) is a consumer tool.

Test: same-key seq 1 (good), seq 2 (poison), seq 3 (good) → count 2, one DLQ row, seq 3 applied. Replay with `--fix` → count 3.

## Step 8 — Hot partitions + sharding

```
Log → hostile bus → hash(PartitionKey) % N → shard channel → worker
  sequencer → RetryPipeline → handler
  queued ≥ capacity → backpressure++ (do not block the dispatcher)
```

| Piece | Role |
|-------|------|
| `LoudDuck` | One duckId with weight 100 vs 1 for each other duck |
| `PartitionShard` | Stable FNV-1a; same key always the same shard |
| `ShardWorkerPool` | One worker per shard; per-key order preserved |
| `ShardMetrics` | Per-shard lag (`maxOffset - lastOffset`), queue, backpressure; per-key `lagMs` |
| Mis-demo | `--shard-count 1` — quiet ducks wait behind LoudDuck |

Sharding is consumer-owned, not inside `IEventBus`. Blocking a bounded `WriteAsync` on the hot shard HOL-blocks quiet keys on the subscribe loop — capacity is a signal, not a wait.

Test: burst hot then quiet on different shards; `SHARD_COUNT=1` quiet lag is large; `SHARD_COUNT=3` quiet lag ≈ handle delay.

## Step 9 — Envelope TraceId + OpenTelemetry

Simulator creates a producer span and stamps `EventEnvelope.TraceId` with the W3C `traceparent`. `event_log` persists `trace_id` and `causation_id`. Consumers start `handle.Squeaked` from that id. Duplicator clones `TraceId`; inbox skip tags `ducknet.duplicate=true`. `AlarmRaised` copies `TraceId` and sets `CausationId` to the parent `EventId`.

`DuckNet.ServiceDefaults` is OTel only (no HTTP resilience, no remapped `/health`). Aspire injects `OTEL_EXPORTER_OTLP_ENDPOINT`.

Test: `dotnet test --filter Tracing`. Demo: Aspire **Traces**, filter `handle.Squeaked` (not `DuckNet.*`).

## Step 10 — Billing saga (no distributed transaction)

`AlarmRaised` → Billing inserts `billing_sagas` `Reserved` (PK = `EventId`) and publishes `FeeReserved`. `AlarmResolved` before `expires_at` → `Released` + `FeeReleased` reason `AlarmResolved`. Timeout worker: still `Reserved` after the timeout → `Expired` + `FeeReleased` reason `Timeout`. Duplicate `AlarmRaised` is inbox + PK, not a lock. AlarmCenter and BillingCenter never call each other.

Test: `dotnet test --filter FullyQualifiedName~Billing`. Demo: Aspire `GET /sagas`; fast `POST /alarms/{duckId}/resolve`; slow wait `SAGA_TIMEOUT_SECONDS` (AppHost 15s).

## Anti-patterns

- Parsing “done” from log text instead of counts / test assertions.
- Global ordering. Ordering is per `PartitionKey` only.
- Deduping on payload or duck id instead of `EventId`.
- Putting inbox or sequencer inside the bus implementation (breaks Step 11 bus swap).
- Center-to-Center calls or a shared kernel DB used as a cross-Center query path.
