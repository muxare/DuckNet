# CI and ReviewFlow — policy and later work

Live behavior is in [`claude-review.yml`](../.github/workflows/claude-review.yml), [`refactor-scan.yml`](../.github/workflows/refactor-scan.yml), the `backlog-*.yml` chains, and [`ci.yml`](../.github/workflows/ci.yml). How the multi-stage chains are wired: [agent-chains.md](./agent-chains.md). This file is the backlog after the ReviewFlow MVP: what to do next, what to leave parked.

CCA-F Scenario 5 expansion (test generation, `--bare`/retry, failed-CI diagnose) is specified in [`cca-f-ci-cd.md`](./cca-f-ci-cd.md) — implement that; do not treat it as live until a phase ships.

## Current policy

- **Required to merge:** `ci.yml` (`build-and-test`). Tests decide.
- **Advisory (PR):** `claude-review.yml` (triage → architecture/security if requested → one aggregated comment). Verdict never fails the workflow. Jobs fail only on infrastructure (missing `CLAUDE_CODE_OAUTH_TOKEN`). Each job writes a GitHub Actions summary (what the stage does + its structured object); the sticky comment has a pipeline table and the same JSON in a collapsed block.
- **Advisory (tree):** `refactor-scan.yml` — weekly Monday + `workflow_dispatch`. Two isolated Sonnet sessions (scan, then independent confidence) merged by `jq`, declared in [`.github/chains/refactor-scan.json`](../.github/chains/refactor-scan.json) and run by `run-chain.py`. One GitHub issue per held patch finding and per plan-tier `proposed_issues` item; later runs update matching open issues (scan marker, then title) and skip closed `refactor-scan` issues. Not on `pull_request` (step PRs must not pick up unrelated refactors; ~`$1.50` per run). Scheduled runs skip if HEAD SHA already has a successful scan run. Local `/refactor-scan` does not open issues.
- **Advisory (backlog):** four chains over GitHub issues, none of which closes, relabels, or rewrites an issue.
  - `backlog-build.yml` — `workflow_dispatch` only, `apply: false` by default. Builds epics and stories from the description/vision documents and/or the code (either alone suffices), then an independent Sonnet session checks every candidate against the repo. `already-done`, `unfounded`, and anything under `0.6` confidence are reported and never filed. Re-runs update by `<!-- ducknet-backlog:KEY -->` marker, then title; closed matches are skipped. ~`$1.50`–`$2.50` per run.
  - `backlog-groom.yml` — weekly Wednesday + `workflow_dispatch` (offset from Monday's refactor scan, so it grooms what that filed). Refinement / grouping / duplicate / stale / gap / ordering findings, each re-checked by an independent pass. One sticky **Backlog grooming report** issue, rewritten in place; dismissed findings stay visible rather than vanishing. ~`$0.50`–`$1.00`.
  - `backlog-readiness.yml` — `issues: opened, edited`. One Haiku session, no tools, judging the issue text only; one sticky comment, edited in place. Skips bot actors and issues labelled `refactor-scan` / `backlog-build` / `backlog-groom`. ~`$0.02`.
  - `backlog-slice` — local `/backlog-slice <issue#>` only, no workflow. Cuts one issue into tracer-bullet vertical slices; refusing to slice is a valid result.
- **Local backlog commands** (`/backlog-build`, `/backlog-groom`, `/backlog-slice`) never touch GitHub. Fixture tests for the merge, plan, and markdown run without a token or network: `bash .github/scripts/test-backlog.sh`.
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
- `workflow_run` on **failed** `ci.yml` only: specified in [`cca-f-ci-cd.md`](./cca-f-ci-cd.md) Phase C.
- Optional: `workflow_dispatch` on `claude-review.yml` with a PR number to re-run without pushing.

### F — Skill for triage

- Project skill `ducknet-review-triage` so the workflow invokes `/ducknet-review-triage` instead of inlining rules in the prompt file. Add only after the YAML loop is reliable.

## Parked (maybe never in this repo)

ReviewFlow-as-a-platform. Interesting elsewhere; not required for DuckNet.

- C# orchestrator (`src/ReviewFlow`), `ICodeReviewPlatform`, adapters (Azure DevOps, GitLab, Bitbucket, local CLI)
- YAML `review:` product config (per-reviewer `minimum-risk`, `max-total-budget`, `max-parallel-reviewers`)
- Extra specialists: performance, domain, docs, maintainability (beyond architecture/security/code). **Testing** is specified in [`cca-f-ci-cd.md`](./cca-f-ci-cd.md) Phase B, not parked.
- Confidence-based discard and a “human review” section (schema already allows `confidence`; aggregator ignores it)
- ADO work items from findings (GitHub issues from the refactor scan are live)
- Review memory across PRs (recurring problems, known exceptions)
- Anthropic `code-review` plugin in CI (interactive plugin; known silent-failure risk)
- Making Claude a required merge check
- MCP-enriched review (Step 9+: `list_dlq`, lag) until those tools exist

## Chain consolidation — done

The refactor scan predates the chain engine and was hand-coded in bash. It now
runs on the same machinery as the backlog chains, so there is one way to add a
stage and one place a stage boundary is enforced:

- `run-refactor-scan.sh` is a wrapper over `run-chain.py` + [`.github/chains/refactor-scan.json`](../.github/chains/refactor-scan.json). Behaviour it used to hand-code — extract `.structured_output`, blind the verifier with `jq`, degrade when the confidence pass returns nothing, skip the pass when there is nothing to assess — is now declared in the manifest and enforced for every chain at once.
- `plan-refactor-issues.py` builds on the shared `issue_plan.IssuePlanner` (namespace `ducknet-refactor`). The ~150 duplicated lines of marker parsing, title matching and body splicing are gone; the rendering, the confidence bar and the label rules stayed put.
- `merge-refactor-confidence.sh` is deleted. `merge-confidence.sh` does the same join for all four chains; the manifest passes the array, the id field and the noun for the unassessed note.
- `run-claude.sh` only hard-fails on a missing `CLAUDE_CODE_OAUTH_TOKEN` under `GITHUB_ACTIONS`. Locally it falls through to the CLI's own login, which is what `/refactor-scan` and `/backlog-*` always claimed to need.

One behaviour change worth knowing: an empty scan used to short-circuit before
the merge, so `findings-final.json` was a copy of `findings.json`. It now goes
through the merge with a skipped verify stage, so the file gains a
`no findings to assess` note. Same findings, one extra line of provenance.

## Backlog chains — later work

- Consider a `needs-refinement` label applied from grooming findings. Deliberately not in v1: a weekly workflow that relabels the board is harder to trust than one that only reports.

## Never

- Claude pushing to `main` or auto-merging
- `pull_request_target` + an agent (secret exfil)
- Widening `@claude` to anyone on a public repo
- Parsing “LGTM” from prose — keep JSON schemas
