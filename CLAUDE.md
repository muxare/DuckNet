# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit facts; Centers react via events only.

## Non-negotiable rules

1. **No Center-to-Center calls.** Integration is events only.
2. **No shared database.** Each Center owns its schema.
3. **Events are past facts**, not commands (`Squeaked`, not `SqueakTheDuck`).
4. **Transport is hostile** (from Step 1): at-least-once, unordered across keys.
5. **Every step stays runnable.** Tag and merge on completion.
6. **"No behavior change" is a test result, not a diff impression.** Never claim it in a commit, PR, or issue without a green `dotnet test` on the change; if tests can't be run, write "untested" and say why.

## Git workflow

- One branch per step: `step-0`, `step-1`, … (Phase D: `step-12a`, `step-12b`, `step-12c`)
- Implement on the branch; merge to `main` only when acceptance criteria pass.
- Tag on merge: `git tag step-N`
- Commit format: `feat(step-N): description`

## Step diagrams (required)

When a step’s acceptance criteria pass, create or rewrite [`docs/architecture/step-N.md`](docs/architecture/) from the **code on this branch**, not from the future target. Link it from [`docs/architecture/README.md`](docs/architecture/README.md).

Required Mermaid diagrams in that file:

1. **Architecture** (`flowchart` with subgraphs) — producer, transport, consumer (Centers + DBs from Step 4). Show ownership, `IEventBus` as the only integration seam, and what does *not* connect (no Center-to-Center calls, no shared DB, inbox/sequencer not inside the bus).
2. **Execution** (`sequenceDiagram`, plus a handler `flowchart` if the step adds a decision) — one event from emit to side effect. Include hostile-transport and mis-demo/failure branches the step **actually implements**.

Also required in the same file:

- **Delta vs previous step** — added / changed / unchanged
- **Wire types** — `EventEnvelope` fields (and payloads) that matter this step

[`DuckNetArchitectureSteps.html`](./DuckNetArchitectureSteps.html) is the *target* roadmap (all steps). `docs/architecture/step-N.md` is the *as-built* record. If implementation diverges from the HTML, note it in the step file. Do not draw later-step components as if they exist.

Do not skip this when the step is “just wiring”.

## Layout (Step 12b)

```
src/DuckNet.AppHost/          # Aspire: telemetry + alarm + dashboard + billing + RabbitMQ
src/DuckNet.ServiceDefaults/  # OTel + DuckNet.* ActivitySources
src/DuckNet.Contracts/        # EventEnvelope (TraceId, CausationId), Squeaked v1/v2, AlarmRaised, AlarmResolved, FeeReserved, FeeReleased
src/DuckNet.EventBus/         # IEventBus, InMemoryEventBus, RabbitMqEventBus, ServiceBusEventBus, EventHubsLogWriter, EventBusFactory
src/DuckNet.Kernel/           # primitives + Step 3 console
  Transport/                  # LogTailFeeder (SQLite)
  Consumer/                   # Inbox + PerKeySequencer + checkpoint + RetryPipeline + ShardWorkerPool
  Producer/                   # DuckSimulator (LoudDuck), TransactionalPublisher, OutboxDispatcher
  Persistence/                # KernelDb (SQLite) + PostgresKernelDb + per-Center schema
src/DuckNet.TelemetryCenter/  # owns event_log writes; GET/POST /bus/events; POST /bus/poison; LoudDuck
src/DuckNet.AlarmCenter/      # own DB; rate window; AlarmRaised / AlarmResolved via outbox; upcast Squeaked; DLQ; shards
src/DuckNet.DashboardCenter/  # own DB; Vue UI; squeaks_by_duck_hour + volume_db; DLQ; GET /metrics
src/DuckNet.BillingCenter/    # own DB; saga on AlarmRaised / AlarmResolved; timeout compensation; FeeReserved / FeeReleased
tests/                        # kernel + EventBus + AlarmCenter + DashboardCenter + BillingCenter
infra/bicep/                  # Azure resources (compile in 12b, apply in 12c)
infra/docker/                 # one Dockerfile per Center
.github/workflows/            # ci.yml, claude-review.yml, refactor-scan.yml, backlog-*.yml, deploy-center.yml, infra.yml
.github/chains/               # agent chain manifests (schema-typed multi-stage sessions)
```

## Build & test

```bash
dotnet build
dotnet test
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5
dotnet run --project src/DuckNet.AppHost
```

Slash commands: `/run-demo`, `/mis-demo` (kernel), `/run-aspire` (AppHost), `/refactor-scan`, `/backlog-build`, `/backlog-groom`, `/backlog-slice`. Format hook: `dotnet format` on `*.cs` after agent edits.

## PR review

`claude-review.yml` is a ReviewFlow-style loop: **triage → specialists → one
aggregated comment**. Specialists are isolated (no shared conversation); they
read `review-state.json` plus a file-subset diff. Aggregation is `jq`, not a
model.

| Stage | Prompt / job | Output | Looks for |
|--------|--------------|--------|-----------|
| Triage | `triage.md` (Haiku, no tools, ~$0.10) | `review-state.json` | Risk + which specialists to run |
| Architecture | `architecture-review.md` (Haiku, if requested) | findings | The five rules above |
| Security | `security-review.md` (Haiku, if requested) | findings | Secrets, payload parse, auth |
| Aggregate | `.github/scripts/aggregate-review.sh` | one sticky comment | Merge only — no Claude |

**Advisory** — `ci.yml` decides merge. Review jobs fail only on infrastructure
(missing `CLAUDE_CODE_OAUTH_TOKEN`), never on a `request_changes` verdict.
Drafts and docs-only PRs (`docs/**`, `*.md`, `*.html`) are skipped. Low-risk
PRs skip specialists after triage. `code-review.md` is kept on disk but not
invoked until this loop is boring. Later and parked work: [docs/ci-policy.md](docs/ci-policy.md).

Mention `@claude` on any PR or issue to ask questions interactively
(`claude.yml`).

## Refactor scan

`refactor-scan.yml` is weekly (Monday) + `workflow_dispatch`. Whole-tree, not
a PR diff: Sonnet finds opportunities, a second Sonnet session scores
confidence, `jq` merges. It runs on the chain engine —
[`.github/chains/refactor-scan.json`](.github/chains/refactor-scan.json) — so
the stage contracts, blinding, and failure modes are the ones described under
**Backlog chains** below. CI creates or updates one GitHub issue per held
patch finding and per plan-tier `proposed_issues` item (dedupe open issues by
scan marker, then title; skip if a matching `refactor-scan` issue is already
closed). Advisory — not a merge gate, not on PRs. Local: `/refactor-scan` or
`bash .github/scripts/run-refactor-scan.sh` (prints JSON; does not open issues).

In CI, review, the refactor scan, and the backlog chains all need the repo
secret `CLAUDE_CODE_OAUTH_TOKEN` (`claude setup-token`); a missing one fails
the job as infrastructure. Locally they run on whatever login the `claude` CLI
already has — no token export needed.

## Backlog chains

Four agent chains keep the GitHub backlog honest. All advisory — `ci.yml` still
decides merge, and none of them closes or relabels an issue.

| Chain | Trigger | Does |
|-------|---------|------|
| `backlog-build.yml` | dispatch, `apply: false` by default | Builds epics + stories from the vision documents **and/or** the code — either alone is enough. An independent pass checks each candidate against the repo; `already-done` and `unfounded` are never filed. |
| `backlog-groom.yml` | weekly Wednesday + dispatch | Reads the open backlog: needs refinement, belongs under a common parent, duplicate, stale, gap, ordering. One sticky **Backlog grooming report** issue, rewritten in place. |
| `backlog-readiness.yml` | `issues: opened, edited` | One Haiku session, no tools: could someone pick this up on Monday? Sticky comment. Skips machine-written issues. |
| `backlog-slice` | local `/backlog-slice <issue#>` | Cuts one oversized issue into tracer-bullet vertical slices, each leaving `main` runnable. |

A chain is a manifest in `.github/chains/`, run by
`.github/scripts/run-chain.py`. **Nothing crosses a stage boundary except a
structured object validated against a declared schema** — never prose. The
producing stage's schema has no confidence field, so it cannot score itself,
and `jq` strips its argument before the assessor sees the claims. The refactor
scan runs on the same engine; `merge-confidence.sh` and `issue_plan.py` are
shared by all of them. Read [docs/agent-chains.md](docs/agent-chains.md)
before adding or changing one.

Fixture tests need no token and no network: `bash .github/scripts/test-backlog.sh`
and `bash .github/scripts/test-refactor-scan.sh`. Both run in `ci.yml`.
Dry-run any chain with `python3 .github/scripts/run-chain.py <manifest> <outdir> --dry-run`.

## Agent automation opportunities (CCA-F)

DuckNet is a CCA-F study lab. While working, **spot and propose** reusable agent machinery — do not silently add files, and do not dump exam theory here. Prefer project-scoped paths so teammates inherit them on clone.

**Pick the primitive** (D3): `CLAUDE.md` is always-on standards; skills/commands are on-demand. Hooks enforce what prompts only hope for.

| Need | Where |
|------|--------|
| Always-on rules, layout, commands to run | this file |
| Task-specific procedure Claude may auto-invoke | `.claude/skills/<name>/SKILL.md` |
| Human-triggered team workflow (`/foo`) | `.claude/commands/<name>.md`, or a skill with `disable-model-invocation: true` |
| Must-happen side effect (format, block, gate) | hook (`PreToolUse` / `PostToolUse`), not a prompt |
| Verbose or exploratory work | skill with `context: fork`, or a subagent — keep the parent context clean |

**Hunt for:**

- Repeated prompts, step checklists, or “remember to…” — skill with a sharp `description` (drives auto-invoke), `argument-hint`, and supporting files. Fork if the output is noisy.
- Shared `/review`, `/run-demo`, `/new-center` style workflows — project command in `.claude/commands/` (not `~/.claude/commands/`).
- Prompt-only “must” rules that still fail — hook. Deterministic compliance beats hoping the model obeys.
- Work with a verifiable stop (tests green, demo runs, schema valid) — an **agentic loop**: model-driven `tool_use` until `end_turn`; stop on evidence, not an arbitrary turn cap. Feed tool results back in; do not parse prose for “done”.
- Multi-concern or parallel investigation — coordinator + isolated subagents; pass complete findings in the child prompt (subagents do not inherit parent context).
- Headless/CI work — structured JSON + schema, independent session from the author (already the PR-review pattern).

When you find one, propose the path, frontmatter (`description`, `allowed-tools`, `argument-hint`, `context: fork` if needed), and why. Wait for approval.

Live: skills `ducknet-kernel`, `ducknet-center`, and `ducknet-event-contract`; commands `/run-demo`, `/mis-demo`, `/run-aspire`, `/refactor-scan`, `/backlog-build`, `/backlog-groom`, `/backlog-slice`; agent chains in `.github/chains/` ([docs/agent-chains.md](docs/agent-chains.md)); PostToolUse hook `dotnet format` on `*.cs`. Planned: `ducknet-mcp-ops` (Step 9+). See [ImplementationPlan.md](./ImplementationPlan.md#cca-f-integration--development--cicd--system).

## Step progress

| Step | Status | Branch |
|------|--------|--------|
| 0 | complete | `step-0` → `main` |
| 1 | complete | `step-1` → `main` |
| 2 | complete | `step-2` → `main` |
| 3 | complete | `step-3` → `main` |
| 4 | complete | `step-4` → `main` |
| 5 | complete | `step-5` → `main` |
| 6 | complete | `step-6` → `main` |
| 7 | complete | `step-7` → `main` |
| 8 | complete | `step-8` → `main` |
| 9 | complete | `step-9` → `main` |
| 10 | complete | `step-10` → `main` |
| 11 | complete | `step-11` → `main` |
| 12a | complete | `step-12a` → `main` |
| 12b | complete | `step-12b` |
| 12c | planned | First Azure environment (needs subscription) |

See [ImplementationPlan.md](./ImplementationPlan.md) for full roadmap.
