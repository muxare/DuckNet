---
description: Cut one oversized GitHub issue into tracer-bullet vertical slices, each of which leaves main building, tested, and demo-able. Prints the slices; does not open issues.
argument-hint: "<issue-number> [outdir]"
allowed-tools: Bash
disable-model-invocation: true
---

Cut one issue into independently mergeable slices. Local and human-triggered — it prints slices, it does not file them.

1. Issue number = the first argument. Refuse with a one-line message if it is missing or not a number; do not guess which issue was meant.
2. Outdir = the second argument, otherwise `/tmp/backlog-slice`.
3. From the repo root. Requires `claude` on PATH (logged in), `gh` authenticated for this repo, `jq`, and `python3`.

```bash
bash .github/scripts/run-backlog-slice.sh <issue-number> <outdir>
```

4. Print the readable form:

```bash
bash .github/scripts/format-backlog-slice.sh <outdir>/slice.json
```

5. Report the cost from `<outdir>/chain-meta.json`. Expect roughly `$0.30`–`$0.60`.

A slice is vertical: producer, transport, consumer, side effect. "Add the tables" is not a slice, because nothing runs at the end of it. Each slice names in `demo` what still runs after it lands.

`not_sliced_because` with an empty `slices` array is a real answer, not a failure — say so plainly rather than pressing for a split. An issue cut into four pieces that must all land together is worse than the one you started with.

Filing the slices is a human action. Offer to open them, do not open them unasked.
