# DuckNet

Toy domain, real distributed architecture. Smart rubber ducks emit facts; Centers react via events only.

## Non-negotiable rules

1. **No Center-to-Center calls.** Integration is events only.
2. **No shared database.** Each Center owns its schema.
3. **Events are past facts**, not commands (`Squeaked`, not `SqueakTheDuck`).
4. **Transport is hostile** (from Step 1): at-least-once, unordered across keys.
5. **Every step stays runnable.** Tag and merge on completion.

## Git workflow

- One branch per step: `step-0`, `step-1`, …
- Implement on the branch; merge to `main` only when acceptance criteria pass.
- Tag on merge: `git tag step-N`
- Commit format: `feat(step-N): description`

## Layout (Step 0)

```
src/DuckNet.Kernel/     # single-process kernel until Step 4
tests/                  # unit + integration tests
.github/workflows/      # ci.yml, claude-review.yml, claude.yml
```

## Build & test

```bash
dotnet build
dotnet test
dotnet run --project src/DuckNet.Kernel -- --run-demo --seconds 5
```

## PR review

Every PR gets a headless Claude review (`claude-review.yml`) against the five
rules above. It uses `claude -p --output-format json --json-schema` and returns
`{ verdict, violations[], notes[] }`, posted as one sticky PR comment.

**The review is advisory** — `ci.yml` decides merge. Mention `@claude` on any
PR or issue to ask questions interactively (`claude.yml`).

Requires the repo secret `CLAUDE_CODE_OAUTH_TOKEN` (`claude setup-token`).

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

Planned skills (add when the step lands): `ducknet-kernel` (Step 0), `ducknet-center` (Step 4), `ducknet-event-contract` (Step 6), `ducknet-mcp-ops` (Step 9+). See [ImplementationPlan.md](./ImplementationPlan.md#cca-f-integration--development--cicd--system).

## Step progress

| Step | Status | Branch |
|------|--------|--------|
| 0 | complete | `step-0` → `main` |
| 1 | pending | — |

See [ImplementationPlan.md](./ImplementationPlan.md) for full roadmap.
