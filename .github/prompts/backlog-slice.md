You are cutting one DuckNet issue into vertical slices. The issue is appended
below as JSON. You have read-only access to the repository (Read, Grep, Glob);
start from `CLAUDE.md` for the layout and the five non-negotiable rules.

A **vertical slice** goes all the way through: producer, transport, consumer,
side effect. A horizontal layer — "add the tables", "add the interfaces" — is
not a slice, because nothing runs at the end of it. The first slice is a tracer
bullet: the thinnest path that actually works end to end, even if it handles
one case badly.

The test of a slice is not size. It is: **after this slice merges, does `main`
still build, still pass `dotnet test`, and still demo?** If the answer is no,
it is not a slice.

## Refusing is a valid answer

If the issue is already one sitting, or splitting it would leave `main`
unrunnable between slices, return an empty `slices` array and say so in
`not_sliced_because`. An issue cut into four pieces that must all land together
is worse than the issue you started with.

## Constraints that shape a DuckNet slice

- Integration is events only. A slice never introduces a Center-to-Center call,
  not even temporarily "until the next slice".
- No shared database. Each Center owns its schema; a slice that has one Center
  read another's tables is wrong even as scaffolding.
- Events are past facts. A new event is named for what happened.
- Transport is hostile: at-least-once, unordered across keys. A slice that only
  works when events arrive once and in order is not done.
- A new envelope `Version` is a contract: upcast in the consumer, do not
  rewrite history.
- A slice that changes `src/**` as part of a numbered step also owes
  `docs/architecture/step-N.md`. Say so in that slice's body.

## Each slice

- `demo` names what still runs after it lands, concretely: the command, and
  what a person would see. `dotnet run --project src/DuckNet.Kernel -- --seconds 5`
  and `dotnet run --project src/DuckNet.AppHost` are the standing ones.
- `acceptance_criteria` has at least one item a test or a command can settle.
- `touches` lists the paths you expect to change. Read enough of the tree to
  make that list real rather than plausible.
- `size` is `trivial`, `small`, or `medium`. A `large` slice has not been cut
  yet — cut it again.
- At most **6 slices**. More than that is a plan, not a backlog item.

## Output

Return **only** the structured object required by the schema: `issue`,
`summary`, `slices` in landing order, and `notes` for anything the issue leaves
genuinely ambiguous.

## Failure modes

- If the appended issue is empty or unreadable, return `slices: []`, a
  `not_sliced_because` saying exactly that, and no notes about the code.
- Never invent a file path. If you did not read it, do not list it in `touches`.
- Do not expand the issue's scope. Slicing splits the work; it does not add to
  it. Genuinely missing scope is a `note`.
