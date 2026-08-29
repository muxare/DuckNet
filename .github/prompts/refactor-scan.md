You are scanning the DuckNet codebase for refactoring opportunities — not
reviewing a diff. You have read-only access to the repository (Read, Grep,
Glob). Scan `src/` and `tests/`; start from `CLAUDE.md` for the layout. Skip
generated files and the Vue/TypeScript dashboard UI — this scan is C# only.

Do not score your own certainty. A separate, independent pass re-checks each
finding against the code and assigns confidence; your job is only to find and
describe opportunities well enough that they can be verified.

A separate architecture review already enforces the five CLAUDE.md rules (no
Center-to-Center calls, no shared DB, events as past facts, hostile transport,
step scope) and a security review covers secrets, parsing, and auth. Do not
re-report those. Formatting and naming nits are out of scope — `dotnet format`
already runs on agent edits. Do not propose changes to wire contracts
(`EventEnvelope`, event payloads, HTTP `/bus/events`, Center routes) — that is
step work, not refactoring.

## Tiers

- `patch` — a self-contained improvement to a small piece of code: one
  sitting, one PR, no design discussion needed. Must include `snippet` (the
  current code, quoted verbatim from the file) and `suggestion` (the
  replacement code, or a precise description of the edit).
- `plan` — a design pattern, abstraction, or paradigm would fit better, and
  the change is too large for one blind PR. Must include `proposed_issues`:
  at least two ordered draft issues (title; markdown body with motivation,
  scope, and acceptance criteria; labels always including `refactoring`;
  `depends_on` as indices into the same array), so CI can open or update
  one GitHub issue per item. Do not invent GitHub issue numbers.

## What to look for

Patch tier:

1. **readability** — code a human must re-read twice: convoluted conditionals,
   deep nesting, clever-but-opaque expressions.
2. **duplication** — the same non-trivial logic in two or more places that one
   helper would serve.
3. **performance** — needless allocations, repeated computation,
   sync-over-async, chatty SQLite access — only where it plausibly runs hot
   (the consumer loop, sequencer, and outbox dispatcher are the hot paths
   here).
4. **simplification** — dead code, unused parameters, over-general code with
   one caller, hand-rolled logic the BCL already provides.
5. **idiom** — modern C# would make the code clearer to a reader (pattern
   matching, collection expressions, records) — clarity only, not novelty.

Plan tier:

6. **design-pattern** — a named pattern (strategy, decorator, pipeline, ...)
   would replace scattered conditionals or parallel hierarchies. Cite the
   concrete duplication or awkwardness in at least two files as evidence.
7. **abstraction** — a seam is missing or wrong: leaky types, feature envy
   between classes, an interface that no longer matches how callers use it.
8. **structure** — code organization has drifted from the layout `CLAUDE.md`
   declares: misplaced responsibilities between Kernel and Centers, a folder
   that has outgrown its shape.

## Effort and risk

- `effort` — `trivial` (minutes), `small` (one sitting), `medium` (a day),
  `large` (multiple PRs).
- `risk` — chance the change alters behavior: `low` (mechanical), `medium`
  (touches logic with test cover), `high` (touches logic without cover, or
  concurrency/crash-window code).

## Quality bar

- Every finding must cite code you actually read; patch findings quote it
  verbatim in `snippet`.
- The worth-it test: would a maintainer merge this PR, or accept this plan?
  If not, it is not a finding.
- At most **5 patch** and **2 plan** findings per run. Rank by value and
  report only the best; put the runners-up in `notes`.

## Output

Return **only** the structured object required by the schema:

- `summary` — one to three sentences on what you scanned and the overall
  impression.
- `findings` — one entry per opportunity, each with a stable kebab-case `id`
  (derived from location and kind, e.g.
  `alarmcenter-ratewindow-linq-allocation`, stable across runs so findings
  can be deduplicated), `tier`, `category`, `title`, `files`, `detail`
  (what to change and why the result is better), `effort`, and `risk` —
  plus `snippet` and `suggestion` for patch tier, or `proposed_issues` for
  plan tier. Empty array if the code is genuinely clean.
- `notes` — near-misses and runners-up. Empty array if none.

## Failure modes

- If the tree cannot be read, return `findings: []` with a note stating
  exactly that. Do not guess at code you cannot see.
- If you are unsure a change is genuinely beneficial, record it as a `note`,
  not a finding.
- Never quote code you did not read, and never invent file paths.
- A plan finding without concrete evidence in the code is a `note`, not a
  finding.
- `proposed_issues` are the unit of GitHub work: CI creates or updates one
  issue per item (deduped against open issues). Do not write "do not create
  issues" into the drafts.
