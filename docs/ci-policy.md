# CI and ReviewFlow — policy and later work

Live behavior is in [`claude-review.yml`](../.github/workflows/claude-review.yml), [`refactor-scan.yml`](../.github/workflows/refactor-scan.yml), and [`ci.yml`](../.github/workflows/ci.yml). This file is the backlog after the ReviewFlow MVP: what to do next, what to leave parked.

## Current policy

- **Required to merge:** `ci.yml` (`build-and-test`). Tests decide.
- **Advisory (PR):** `claude-review.yml` (triage → architecture/security if requested → one aggregated comment). Verdict never fails the workflow. Jobs fail only on infrastructure (missing `CLAUDE_CODE_OAUTH_TOKEN`).
- **Advisory (tree):** `refactor-scan.yml` — weekly Monday + `workflow_dispatch`. Two isolated Sonnet sessions (scan, then independent confidence) merged by `jq`. One GitHub issue per held patch finding and per plan-tier `proposed_issues` item; later runs update matching open issues (scan marker, then title) and skip closed `refactor-scan` issues. Not on `pull_request` (step PRs must not pick up unrelated refactors; ~`$1.50` per run). Scheduled runs skip if HEAD SHA already has a successful scan run. Local `/refactor-scan` does not open issues.
- **Skipped:** draft PRs, fork PRs, docs-only diffs (`docs/**`, `*.md`, `*.html`).
- **Interactive:** `@claude` via [`claude.yml`](../.github/workflows/claude.yml) (OWNER/MEMBER/COLLABORATOR only).
- Do not make Claude a required status check until the loop is boringly stable.

## Later (when the MVP is boring)

Do these in DuckNet. Order is a suggestion, not a gate.

### A — Cheaper PR CI

- Add a concurrency group on `ci.yml` (cancel in-progress runs on the same PR), same pattern as `claude-review.yml`.
- Move Docker image builds off every PR; run them on `push` to `main` and/or nightly. Largest GitHub-minutes save; a broken Dockerfile can merge and fail later.

### B — Nightly CI (no model)

- New `nightly.yml`: `schedule` (offset from `:00`) + `workflow_dispatch`.
- Skip if HEAD SHA is unchanged since the last successful run (public-repo cron also dies after 60 days of inactivity — dispatch is the safety net).
- Same `dotnet test` + kernel smoke as PR, plus the Docker builds moved off the PR path.

### C — Nightly Claude audit

- Not the refactor scan — that is live (`refactor-scan.yml`). This item is architecture/docs drift.
- Reuse `review-state.json` for a **whole-tree** pass (PR review is diff-only).
- Architecture drift vs the five `CLAUDE.md` rules; docs vs as-built (`docs/architecture/step-N.md`).
- Optional: contract upcast checklist (`ducknet-event-contract`); CCA-F hygiene (`CLAUDE.md` vs folders/skills/hooks).
- Read-only tools. **One sticky GitHub issue**, update in place. Do not push to `main`.
- Skill later: `ducknet-nightly-audit` (workflow invokes the skill; do not dump the procedure into YAML).

### D — Smarter PR Claude

- Re-introduce the **code** specialist (bugs/tests/contracts) behind triage `requestedReviewers`. Prompt already at [`.github/prompts/code-review.md`](../.github/prompts/code-review.md).
- Path-conditioned extra job when `src/DuckNet.Contracts/**` or `src/DuckNet.EventBus/**` change (event-contract skill).
- Deterministic step-diagram reminder (grep/job, not a model): `src/**` changed on a `step-N` PR and `docs/architecture/step-N.md` did not.

### E — On-demand depth

- `/deep-review` command (`disable-model-invocation: true`) — Opus, more turns, still advisory. Do not pay Opus on every push.
- `workflow_run` on **failed** `ci.yml` only: Claude reads logs and comments the likely cause.
- Optional: `workflow_dispatch` on `claude-review.yml` with a PR number to re-run without pushing.

### F — Skill for triage

- Project skill `ducknet-review-triage` so the workflow invokes `/ducknet-review-triage` instead of inlining rules in the prompt file. Add only after the YAML loop is reliable.

## Parked (maybe never in this repo)

ReviewFlow-as-a-platform. Interesting elsewhere; not required for DuckNet.

- C# orchestrator (`src/ReviewFlow`), `ICodeReviewPlatform`, adapters (Azure DevOps, GitLab, Bitbucket, local CLI)
- YAML `review:` product config (per-reviewer `minimum-risk`, `max-total-budget`, `max-parallel-reviewers`)
- Extra specialists: testing, performance, domain, docs, maintainability (beyond architecture/security/code)
- Confidence-based discard and a “human review” section (schema already allows `confidence`; aggregator ignores it)
- ADO work items from findings (GitHub issues from the refactor scan are live)
- Review memory across PRs (recurring problems, known exceptions)
- Anthropic `code-review` plugin in CI (interactive plugin; known silent-failure risk)
- Making Claude a required merge check
- MCP-enriched review (Step 9+: `list_dlq`, lag) until those tools exist

## Never

- Claude pushing to `main` or auto-merging
- `pull_request_target` + an agent (secret exfil)
- Widening `@claude` to anyone on a public repo
- Parsing “LGTM” from prose — keep JSON schemas
