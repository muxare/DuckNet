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

- One branch per step: `step-0`, `step-1`, …
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

## Layout (Step 8)

```
src/DuckNet.AppHost/          # Aspire: telemetry + alarm + dashboard
src/DuckNet.Contracts/        # EventEnvelope, Squeaked v1/v2, AlarmRaised
src/DuckNet.EventBus/         # IEventBus, hostile wrappers, HttpLogClient, upcasters
src/DuckNet.Kernel/           # primitives + Step 3 console
  Transport/                  # LogTailFeeder (SQLite)
  Consumer/                   # Inbox + PerKeySequencer + checkpoint + RetryPipeline + ShardWorkerPool
  Producer/                   # DuckSimulator (LoudDuck), TransactionalPublisher, OutboxDispatcher
  Persistence/                # KernelDb + per-Center schema + dead_letter_queue
src/DuckNet.TelemetryCenter/  # owns event_log writes; GET/POST /bus/events; POST /bus/poison; LoudDuck
src/DuckNet.AlarmCenter/      # own DB; rate window; AlarmRaised via outbox; upcast Squeaked; DLQ; shards
src/DuckNet.DashboardCenter/  # own DB; Vue UI; squeaks_by_duck_hour + volume_db; DLQ; GET /metrics
tests/                        # kernel + AlarmCenter + DashboardCenter
infra/docker/                 # one Dockerfile per Center
.github/workflows/            # ci.yml, claude-review.yml, refactor-scan.yml, deploy-center.yml
```

## Build & test

```bash
dotnet build
dotnet test
dotnet run --project src/DuckNet.Kernel -- --reset-db --seconds 5
dotnet run --project src/DuckNet.AppHost
```

Slash commands: `/run-demo`, `/mis-demo` (kernel), `/run-aspire` (Step 5), `/refactor-scan`. Format hook: `dotnet format` on `*.cs` after agent edits.

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
confidence, `jq` merges. CI creates or updates one GitHub issue per held
patch finding and per plan-tier `proposed_issues` item (dedupe open issues by
scan marker, then title; skip if a matching `refactor-scan` issue is already
closed). Advisory — not a merge gate, not on PRs. Local: `/refactor-scan` or
`bash .github/scripts/run-refactor-scan.sh` (prints JSON; does not open issues).

Both review and the refactor scan need the repo secret `CLAUDE_CODE_OAUTH_TOKEN`
(`claude setup-token`).

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

Live: skills `ducknet-kernel`, `ducknet-center`, and `ducknet-event-contract`; commands `/run-demo`, `/mis-demo`, `/run-aspire`, `/refactor-scan`; PostToolUse hook `dotnet format` on `*.cs`. Planned: `ducknet-mcp-ops` (Step 9+). See [ImplementationPlan.md](./ImplementationPlan.md#cca-f-integration--development--cicd--system).

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
| 8 | in progress | `step-8` |

See [ImplementationPlan.md](./ImplementationPlan.md) for full roadmap.
