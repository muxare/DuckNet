---
description: Build a DuckNet backlog from the vision/description documents and the code, with an independent pass that checks every candidate against the repository. Prints the plan; does not open issues.
argument-hint: "[outdir] [-- doc-path ...]"
allowed-tools: Bash
disable-model-invocation: true
---

Run the two-stage backlog build locally (team-shared, human-triggered). Advisory — it proposes, humans decide. Does not open GitHub issues; that is `backlog-build.yml` dispatched with `apply: true`.

1. Outdir = the first argument when it is a non-empty path that does not start with `-`; otherwise `/tmp/backlog-build`.
2. Everything after `--` is a document or directory to read **instead of** the repo defaults (`README.md`, `ImplementationPlan.md`, `CentersBuildPlan.md`, `DuckNetArchitectureSteps.html`, `docs/industry-mappings.md`). Either input suffices: with no readable document the chain builds from the code alone and says so.
3. From the repo root. Requires `claude` on PATH (logged in), `jq`, `python3`, and — only for the existing-backlog context — `gh`.

```bash
bash .github/scripts/run-backlog-build.sh <outdir> [-- <doc-path> ...]
```

Add `--dry-run` to assemble every stage input without spending anything; read `<outdir>/build-input.md` and `<outdir>/verify-input.md` to review the prompts.

4. Print the summary the script writes, then the readable form:

```bash
bash .github/scripts/format-backlog-build.sh <outdir>/backlog-final.json "$(git rev-parse HEAD)"
```

5. To see what would be filed without filing it:

```bash
bash .github/scripts/dump-issues.sh <outdir>/existing.json all 300
python3 .github/scripts/plan-backlog-issues.py \
  <outdir>/backlog-final.json <outdir>/existing.json "$(git rev-parse HEAD)"
```

6. Report the per-stage cost from `<outdir>/chain-meta.json`. Expect roughly `$1.50`–`$2.50`.

Items the independent pass marked `already-done` or `unfounded`, or scored below `0.6`, are reported and never filed. If many come back `already-done`, the vision documents have drifted behind the code — that is a finding about the documents, worth saying out loud.

This is not a substitute for `dotnet test` or PR review.
