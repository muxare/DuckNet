---
name: "source-command-refactor-scan"
description: "Run the DuckNet whole-tree refactor scan (scan + independent confidence) and print findings."
---

# source-command-refactor-scan

Use this skill when the user asks to run the migrated source command `refactor-scan`.

## Command Template

Run the two-stage refactor scan locally (team-shared, human-triggered). Advisory — not a merge gate. Does not open GitHub issues; weekly CI creates or updates one issue per held task.

1. Outdir = the user's argument when it is a non-empty path; otherwise `/tmp/refactor-scan`.
2. From the repo root. Requires `claude` on PATH (logged in) and `jq`.

```bash
bash .github/scripts/run-refactor-scan.sh <outdir>
```

3. Print the summary the script already writes, then `outdir/findings-final.json`.
4. Optional readable markdown:

```bash
bash .github/scripts/format-refactor-scan.sh <outdir>/findings-final.json "$(git rev-parse HEAD)"
```

5. Weekly CI is `refactor-scan.yml` (per-task GitHub issues). This command is the local equivalent.

This is not a substitute for `dotnet test` or PR review.
