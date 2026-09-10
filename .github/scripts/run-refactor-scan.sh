#!/usr/bin/env bash
# Two-stage refactoring scan: scan -> independent confidence pass -> jq merge.
#
# The chain itself lives in .github/chains/refactor-scan.json and is run by
# run-chain.py, the same engine as the backlog chains: nothing crosses a stage
# boundary except an object validated against the stage's declared schema.
# This script only decides where the output goes.
#
# usage: run-refactor-scan.sh [OUTDIR] [--dry-run]   (default /tmp/refactor-scan)
# Final result: OUTDIR/findings-final.json
# Issue markdown: format-refactor-scan.sh FINDINGS.json SHA [RUN_URL]
# Issue actions:  plan-refactor-issues.py FINDINGS.json EXISTING.json SHA [RUN_URL]
# env overrides: SCAN_MODEL / VERIFY_MODEL (sonnet), SCAN_BUDGET_USD (1.00),
#   VERIFY_BUDGET_USD (0.50), SCAN_MAX_TURNS (40), VERIFY_MAX_TURNS (30)
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)

outdir=
dry_run=()
for arg in "$@"; do
  case "$arg" in
    "") ;;
    --dry-run) dry_run+=("--dry-run") ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *)
      if [[ -z "$outdir" ]]; then outdir=$arg
      else echo "unexpected argument: $arg" >&2; exit 2; fi ;;
  esac
done

outdir=${outdir:-/tmp/refactor-scan}
mkdir -p "$outdir"

python3 "$root/.github/scripts/run-chain.py" \
  "$root/.github/chains/refactor-scan.json" "$outdir" "${dry_run[@]+"${dry_run[@]}"}"

if [[ ${#dry_run[@]} -gt 0 ]]; then
  exit 0
fi

echo "wrote $outdir/findings-final.json"
jq '{summary, findings: [.findings[] | {id, tier, category, confidence}]}' \
  "$outdir/findings-final.json"
