# Agent chains

How DuckNet runs more than one agent on a task without letting them talk in
prose. Live implementations: [`refactor-scan`](../.github/chains/refactor-scan.json),
[`backlog-build`](../.github/chains/backlog-build.json),
[`backlog-groom`](../.github/chains/backlog-groom.json),
[`backlog-readiness`](../.github/chains/backlog-readiness.json),
[`backlog-slice`](../.github/chains/backlog-slice.json).

The refactor scan is where the shape came from: it was hand-coded in bash first,
and the engine is that script generalised until the bash had nothing left to say.
It now runs on the manifest like everything else. The PR review loop
(`claude-review.yml`) still hand-codes its own variant — it fans out to parallel
specialists rather than running a line of stages, which the manifest does not yet
express.

## The rule

**Nothing crosses a stage boundary except a structured object that has been
validated against a declared schema.**

Not a transcript, not a summary, not "the previous agent said". A stage's
entire contribution to the chain is one JSON document, and if it does not
satisfy the schema, the chain either stops or falls back to a declared default.
There is no path where prose becomes an input.

This is not ceremony. It buys four things:

- **A stage can fail loudly.** "No valid output" is a state the runner handles,
  rather than a plausible paragraph the next stage acts on.
- **A stage can be replaced.** Swap the model, the prompt, the tool set — the
  contract is the schema, and the next stage cannot tell.
- **A stage can be blinded.** Because the hand-off is a document, a `jq` filter
  can remove fields before the next stage sees them. That is how the assessment
  passes stay independent (below).
- **CI can act without reading.** `jq` decides what gets filed. No workflow ever
  greps a model's prose for "LGTM" — a rule this repo already keeps.

## Anatomy

A chain is a JSON manifest, itself validated against
[`chain.schema.json`](../.github/schemas/chain.schema.json):

```jsonc
{
  "name": "backlog-build",
  "stages": [
    {
      "id": "build",
      "prompt": ".github/prompts/backlog-build.md",
      "schema": ".github/schemas/backlog-items.schema.json",
      "model": "sonnet", "budget": "1.50", "tools": "Read,Grep,Glob",
      "context": [{ "from": "file", "path_env": "BACKLOG_SOURCES_FILE" }],
      "on_missing_output": "fail"
    },
    {
      "id": "verify",
      "prompt": ".github/prompts/backlog-verify.md",
      "schema": ".github/schemas/backlog-verdicts.schema.json",
      "context": [{
        "from": "stage", "stage": "build",
        "jq": "{items: [.items[] | {id, title, acceptance_criteria}]}"
      }],
      "skip_if":  { "stage": "build", "jq": "(.items | length) == 0" },
      "on_missing_output": "degrade",
      "degrade_output": { "assessments": [], "notes": ["..."] }
    }
  ],
  "merge": {
    "script": ".github/scripts/merge-confidence.sh",
    "args": ["{{stage:build}}", "{{stage:verify}}", "items", "id", "item"],
    "output": "backlog-final.json",
    "schema": ".github/schemas/backlog-final.schema.json"
  }
}
```

[`run-chain.py`](../.github/scripts/run-chain.py) executes it: build each
stage's input (prompt + context blocks), call
[`run-claude.sh`](../.github/scripts/run-claude.sh) headless with
`--json-schema`, extract `.structured_output`, validate, hand on. It writes
`<stage>-input.md`, `<stage>-raw.json`, `<stage>.json`, and a
[`chain-meta.json`](../.github/schemas/chain-meta.schema.json) run report
carrying each stage's exit, subtype, turns, and cost.

Schema validation is
[`validate-json.py`](../.github/scripts/validate-json.py) — a dependency-free
subset validator, because the only thing this repo installs is the Claude CLI.
The CLI already constrains output to the schema; validating again is what turns
"probably fine" into a checked precondition before anything touches GitHub.

## The four patterns worth stealing

### 1. The producer cannot score itself

Every chain that files or reports something splits into a producer and an
independent assessor, and **the producer's schema has no confidence field**. It
is not asked to be humble; it is structurally unable to self-certify. A second
session, with its own context, scores the claims.

This is the refactor scan's design, generalised — and now the refactor scan runs
on the generalisation. It matters most in `backlog-build`: a backlog built from vision documents against a twelve-step
repository will confidently propose work that is already merged. The assessor's
`already-done` verdict is what stops it being filed.

### 2. Blind the assessor with jq, not with instructions

Asking a model to ignore an argument it can see does not work. Deleting the
argument does:

```jsonc
"jq": "{items: [.items[] | {id, kind, title, parent, acceptance_criteria, depends_on, evidence: [.evidence[] | {source}]}]}"
```

The assessor gets the claim and the paths to check. It never sees the
producer's `body`, `size`, or its argument for why the evidence supports the
claim. `test-backlog.sh` asserts this — a `detail` field appearing in a verify
input is a test failure, not a style question. `test-refactor-scan.sh` runs the
same assertion against that chain's filter: `detail`, `effort` and `risk` must
not survive it.

### 3. Failure modes are declared, not improvised

`on_missing_output` says what losing a stage costs:

- `fail` for the stage that produces the work. No candidates, no chain.
- `degrade` for an assessment stage, with a `degrade_output` that yields empty
  assessments. Losing the assessment must not lose the work — every item merges
  with `confidence: null`, the merge adds a note saying so, and the planner
  treats unassessed as "file it, and say on the issue that nobody checked".

`skip_if` handles the ordinary empty case: no candidates, no assessment call, no
spend.

### 4. The merge is a script

`merge-confidence.sh` joins assessments onto items by id, in `jq`. One script for
all four chains: the manifest tells it the array (`items`, `findings`), the id
field, and the noun for the note. It copies every assessor field except `id` and
`rationale` onto the item, so a chain can add its own verdict vocabulary
(`state`, `verdict`) without the merge script learning about it. An item with no
assessment gets `null` and a note — a silently unassessed item must never look
assessed.

No model decides what gets filed.

## Where a chain does *not* need a second stage

`backlog-slice` is one stage. A human runs it and reads the output in the same
minute — the reviewer is already in the loop, and a second model pass would be
paying to duplicate them.

The rule: **the independent pass exists where output reaches GitHub
unattended.** `backlog-readiness` is also one stage, for a different reason: it
runs on every issue edit, on Haiku, with no tools, judging text as written.
Cheap and frequent has a different failure budget than slow and consequential.

## Adding a chain

1. Write the schema first. If you cannot describe the output as a document,
   the task is not ready to be a chain. Keep the `$schema` declaration in the
   file for editors and for `validate-json.py`; `run-claude.sh` strips it on the
   way to the CLI, whose `--json-schema` validator has no meta-schemas
   registered and rejects any declaration it cannot resolve
   (`no schema with key or ref "..."`) before the model ever runs.
2. Write the prompt against that schema, with an explicit **Failure modes**
   section. Every prompt here ends with one.
3. Write the manifest. Declare `on_missing_output` deliberately.
4. Add a fixture to `.github/examples/` and a case to
   [`test-backlog.sh`](../.github/scripts/test-backlog.sh) (or
   [`test-refactor-scan.sh`](../.github/scripts/test-refactor-scan.sh)). Fixture
   tests need no token and no network, so the merge, the plan, and the markdown
   are testable without spending anything. Both suites run in `ci.yml`.
5. Dry-run it: `python3 .github/scripts/run-chain.py <manifest> <outdir> --dry-run`
   assembles every stage input and calls no model. Read the inputs.
6. Only then spend money.

## Cost and safety

| Chain | Stages | Model | Typical | Trigger |
| --- | --- | --- | ---: | --- |
| `refactor-scan` | 2 | Sonnet | `$1.50` | weekly Monday + dispatch |
| `backlog-build` | 2 | Sonnet | `$1.50`–`$2.50` | dispatch only; filing is gated on approval |
| `backlog-groom` | 2 | Sonnet | `$0.50`–`$1.00` | weekly Wednesday + dispatch |
| `backlog-slice` | 1 | Sonnet | `$0.30`–`$0.60` | local command only |
| `backlog-readiness` | 1 | Haiku, no tools | `~$0.02` | `issues: opened, edited` |

Every one of them is advisory. `ci.yml` decides merge; these decide nothing.
The strongest action any of them takes is creating an issue: `refactor-scan`
weekly, and `backlog-build` only after a human releases the `backlog-apply`
environment gate.

A chain that files issues stops at a **plan**, and a separate un-modelled step
applies it. `backlog-build.yml` ends by uploading a schema-validated
`backlog-actions.json`; [`backlog-apply.yml`](../.github/workflows/backlog-apply.yml)
waits on the `backlog-apply` environment, then downloads *that run's* artifact
and files it. The plan is the approval boundary for the same reason a schema is
the stage boundary: what the reviewer read is what runs. Re-dispatching the
build to "approve" it would have re-run the model and filed something else.

### An approval outlives the state it was resolved against

Approval takes minutes or days, and the backlog moves underneath it. A plan
pins issue numbers, open/closed state and human-edited bodies at build time, so
filing it verbatim writes to issues that have since closed, duplicates issues
that have since appeared, and overwrites text someone added in the meantime.

The fix falls out of the stage discipline. `plan-backlog-issues.py` is a *pure
function* of `(final.json, issue dump, sha, run_url)` — model-free, like every
apply step here — so at apply time
[`reconcile-backlog-plan.py`](../.github/scripts/reconcile-backlog-plan.py) can
run it twice:

```
replay = plan(final, dump taken at build time)   # must equal the approved plan
fresh  = plan(final, dump taken just now)        # this is what gets filed
```

The replay is the integrity check, and it is worth more than its size. Because
the function is pure, reproducing the approved plan byte for byte proves *every*
frozen input is unchanged at once: the chain's output, the planner's own source,
the commit sha in the issue footers, the artifact. A mismatch files nothing. It
is the same move as validating a stage boundary — re-derive the claim instead of
trusting the envelope — which is why apply needs no pinned script version and no
re-checkout of the build's commit.

What the human approved is therefore the **content** (frozen model output: which
items, what titles, what generated bodies). Where each item lands is bookkeeping,
and bookkeeping is re-resolved against current facts. Every move is toward the
safer outcome and every move is reported in the job summary; the item set and
titles cannot move, and it is a hard failure if they do.

Local commands (`/refactor-scan`, `/backlog-build`, `/backlog-groom`,
`/backlog-slice`) never touch GitHub at all, and run on whatever login the
`claude` CLI already has. `run-claude.sh` hard-requires
`CLAUDE_CODE_OAUTH_TOKEN` only under `GITHUB_ACTIONS`, where a missing token is
infrastructure rather than a prompt to log in.
