# DuckNet — Stepwise Build Plan for a Telia-Style "Centers" System

**Goal:** Rebuild the *complexity* of the Telia platform (distributed interconnected Centers, event-driven, eventually consistent) in the smallest possible codebase, one concept per step. Toy content, real architecture.

**Toy domain:** A fleet of smart rubber ducks. Ducks emit `Squeaked` telemetry events. Centers react: count squeaks, raise alarms, bill the owner. Deliberately silly — all attention goes to the seams between services, which is where the Telia complexity lived.

**Rules of the game (these ARE the complexity):**
1. A Center never calls another Center. Events only.
2. A Center never reads another Center's database. No shared DB, ever.
3. Events are facts about the past, not commands.
4. Delivery is at-least-once and unordered — always. Even in-memory, simulate it.
5. Every step must stay runnable end-to-end. Never break the demo.

**Tech:** .NET 10, one solution, .NET Aspire AppHost from step 4 onward. Start with in-memory transport behind an `IEventBus` abstraction; swap the transport late without touching Center code — that swap is itself proof the architecture is right. Steps 0–11 are on `main`; Azure is Phase D (**12a** contract → **12b** adapters/IaC → **12c** live).

---

## Phase A — The kernel (the smallest central piece of the complexity)

### Step 0 — One event, one producer, one consumer
Single console app. A `DuckSimulator` produces `Squeaked(duckId, seq, at)` onto an in-memory `Channel<T>`. A `SqueakCounter` consumes and prints totals.
- **Complexity unlocked:** event-as-fact mindset; producer knows nothing about consumers.
- **Done when:** counter output matches events produced.
- ~1 evening. Keep it under ~150 lines.

### Step 1 — At-least-once + idempotent consumer *(the true kernel)*
Make the in-memory bus hostile on purpose: randomly redeliver ~10% of events. Watch the counter go wrong. Fix it with an **inbox** (processed-event-id set) so handlers are idempotent.
- **Complexity unlocked:** "exactly-once is a lie; you design at-least-once + idempotency."
- **Done when:** with 20% forced duplicates, the count is still exact.

### Step 2 — Out-of-order delivery + per-key ordering
Bus now also shuffles delivery order. Add `duckId` as partition key and a per-duck sequence number. Consumer detects gaps and reorders/buffers per key (or explicitly decides it doesn't need order — write down why).
- **Complexity unlocked:** ordering is per-partition, never global; partition key choice is an architectural decision.
- **Done when:** shuffled input still yields correct per-duck state.

### Step 3 — Durability: append-only event log + outbox
Persist events to an append-only log (start with a SQLite/Postgres table: `offset, key, type, version, payload`). Producer writes state change + outgoing event in one transaction (**outbox**), a dispatcher publishes from the outbox. Consumers track their own offset.
- **Complexity unlocked:** the log is the source of truth; replay becomes possible; no dual-write problem.
- **Done when:** you can kill the consumer, restart it, and it resumes from its offset with no loss and no double-count.

**Phase A is the heart. Everything after this is composition.**

---

## Phase B — Becoming distributed

### Step 4 — Second Center, own database
Split into two real services under an Aspire AppHost: **TelemetryCenter** (owns duck state, publishes `Squeaked`) and **AlarmCenter** (own DB, subscribes, raises `AlarmRaised` when a duck squeaks >N times/minute). Rule 2 enforced: separate schemas/databases.
- **Complexity unlocked:** autonomy, failure isolation, eventual consistency becomes *visible* (alarm lags telemetry — that's correct, not a bug).
- **Done when:** stopping AlarmCenter for a minute loses nothing; it catches up from the log on restart.

### Step 5 — CQRS: projections as disposable read models
Add a **DashboardCenter** that builds a read model (squeaks per duck per hour) purely by consuming events. Add a `rebuild` command that drops the read model and replays the whole log.
- **Complexity unlocked:** read/write separation; projections are cattle, the log is the pet. This mirrors Telia's separated hot-ingest vs. query paths.
- **Done when:** delete the dashboard DB, replay, get byte-identical results.

### Step 6 — Schema evolution across a boundary
Ship `Squeaked v2` (adds `volumeDb`). TelemetryCenter emits v2; AlarmCenter still understands v1. Implement tolerant reader + upcasting (v1→v2 with default) so old events in the log still replay.
- **Complexity unlocked:** contract versioning with N independent consumers — the hardest day-2 problem in interconnected systems.
- **Done when:** a log containing mixed v1/v2 events replays cleanly in every Center.

---

## Phase C — Production-shaped pain

### Step 7 — Poison messages + DLQ
Inject a malformed event. Without handling, the consumer loop dies and blocks the partition. Add retry-with-backoff → dead-letter store → keep consuming.
- **Done when:** one bad event never stops the stream, and the DLQ is inspectable/replayable.

### Step 8 — Backpressure + hot partitions
Make one duck squeak 100x more than the rest. Measure per-partition lag. Show the failure mode (one hot key starves the consumer), then mitigate (key-hash sharding, bounded channels, lag metrics).
- **Complexity unlocked:** the Event Hubs/Cosmos partition-key lesson from Telia, reproduced on your laptop.

### Step 9 — Distributed tracing
OpenTelemetry: correlation/causation IDs on every event, spans across Centers, viewed in the Aspire dashboard. Trace one squeak from simulator → telemetry → alarm → dashboard.
- **Complexity unlocked:** "distributed tracing is what makes an interconnected system debuggable."

### Step 10 — Cross-Center workflow without transactions (saga)
Add **BillingCenter**: `AlarmRaised` → reserve a service fee → if `AlarmResolved` within 5 min, compensate (release fee). No distributed transaction; state machine + compensating events.
- **Complexity unlocked:** coordination via choreography/compensation instead of locks — the "no distributed transactions" story.

### Step 11 (stretch) — Swap the transport
Implement `IEventBus` over a real broker (RabbitMQ container via Aspire, or Azure Service Bus emulator). Center code unchanged.
- **Done when:** `git diff` on Center projects is empty. That diff is the punchline.

---

## Cadence for interview week
- **Before the Epiroc interview:** Steps 0–4 (roughly one evening each; 0–2 can be one sitting). This already gives you live, recent talking material for every hard question about at-least-once, idempotency, ordering, and eventual consistency.
- **After:** Steps 5–11 at leisure; each is an independent evening.

## Interview soundbite per phase
- **A:** "I can show you exactly why exactly-once is a lie — I've forced duplicates and out-of-order delivery on purpose and made the system correct anyway."
- **B:** "Two services, two databases, zero synchronous calls — and I can delete a read model and rebuild it from the log."
- **C:** "I've reproduced hot-partition starvation on my laptop and traced one event across four services."