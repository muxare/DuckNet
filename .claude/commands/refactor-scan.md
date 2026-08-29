---
description: Run the DuckNet whole-tree refactor scan (scan + independent confidence) and print findings.
argument-hint: "[outdir]"
allowed-tools: Bash
disable-model-invocation: true
---

Run the two-stage refactor scan locally (team-shared, human-triggered). Advisory — not a merge gate. Does not open GitHub issues; `proposed_issues` stay drafts in the JSON.

1. Outdir = `$ARGUMENTS` when it is a non-empty path; otherwise `/tmp/refactor-scan`.
2. From the repo root. Requires `claude` on PATH (logged in) and `jq`.

```bash
bash .github/scripts/run-refactor-scan.sh <outdir>
```

3. Print the summary the script already writes, then `outdir/findings-final.json`.
4. Optional readable markdown:

```bash
bash .github/scripts/format-refactor-scan.sh <outdir>/findings-final.json "$(git rev-parse HEAD)"
```

5. Weekly CI is `refactor-scan.yml` (sticky GitHub issue). This command is the local equivalent.

This is not a substitute for `dotnet test` or PR review.
