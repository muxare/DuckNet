---
description: Analyse the open GitHub backlog — what needs refinement, what belongs under a common parent or user story, what is duplicated, stale, missing, or out of order. Prints the report; does not touch any issue.
argument-hint: "[outdir] [--state open|all]"
allowed-tools: Bash
disable-model-invocation: true
---

Run the two-stage backlog groom locally (team-shared, human-triggered). Advisory — it reports, humans decide. Does not publish the report issue; that is weekly `backlog-groom.yml`.

1. Outdir = the first argument when it is a non-empty path that does not start with `-`; otherwise `/tmp/backlog-groom`.
2. From the repo root. Requires `claude` on PATH (logged in), `gh` authenticated for this repo, `jq`, and `python3`.

```bash
bash .github/scripts/run-backlog-groom.sh <outdir> [--state open|all]
```

Add `--dry-run` to assemble both stage inputs without spending anything. Set `BACKLOG_ISSUES_FILE` to groom a saved dump instead of calling `gh`.

3. Print the summary the script writes, then the report as CI would publish it:

```bash
bash .github/scripts/format-backlog-groom.sh <outdir>/groom-final.json "$(git rev-parse HEAD)"
```

4. Report the per-stage cost from `<outdir>/chain-meta.json`. Expect roughly `$0.50`–`$1.00`.

Findings the independent pass marked `does-not-hold`, or scored below `0.5`, appear under **Dismissed** rather than vanishing — a claim that keeps being dismissed week after week is itself worth seeing.

Two findings deserve human care before acting:

- **stale** is a close recommendation. Read the evidence; do not close on the report alone.
- **grouping** proposes a parent issue. Creating it is a human action — this command and the weekly workflow only suggest the title and body.

This is not a substitute for `dotnet test` or PR review.
