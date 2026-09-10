#!/usr/bin/env bash
# Analyse the existing GitHub backlog: what needs refinement, what belongs
# under a common parent, what is duplicated, stale, missing, or out of order.
#
# Two isolated Claude sessions joined by schema: groom observes, an independent
# pass re-checks every claim against the issues and the code, jq merges. The
# result is a report — this script never touches an issue.
#
# usage: run-backlog-groom.sh [OUTDIR] [--dry-run] [--state open|closed|all]
#   OUTDIR  default /tmp/backlog-groom
# Result: OUTDIR/groom-final.json (+ OUTDIR/chain-meta.json)
# Report markdown: format-backlog-groom.sh OUTDIR/groom-final.json SHA [RUN_URL]
# env overrides: GROOM_MODEL / GROOM_VERIFY_MODEL, GROOM_BUDGET_USD (0.75),
#   GROOM_VERIFY_BUDGET_USD (0.60), GROOM_MAX_TURNS, GROOM_VERIFY_MAX_TURNS,
#   BACKLOG_ISSUES_FILE (skip the gh dump and groom this file instead)
# Nested scripts are invoked with bash so GitHub Actions does not depend on +x.
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
scripts="$root/.github/scripts"

outdir=
dry_run=()
state=open
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) dry_run+=("--dry-run"); shift ;;
    --state) state=$2; shift 2 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *)
      if [[ -z "$outdir" ]]; then outdir=$1; shift
      else echo "unexpected argument: $1" >&2; exit 2; fi ;;
  esac
done
outdir=${outdir:-/tmp/backlog-groom}
mkdir -p "$outdir"

# Unlike the build chain, the backlog is the input here: without it there is
# nothing to groom, so a missing dump is a hard failure rather than a note.
if [[ -z "${BACKLOG_ISSUES_FILE:-}" ]]; then
  BACKLOG_ISSUES_FILE="$outdir/issues.json"
  export BACKLOG_ISSUES_FILE
  bash "$scripts/dump-issues.sh" "$BACKLOG_ISSUES_FILE" "$state" 200
fi

count=$(jq 'length' "$BACKLOG_ISSUES_FILE")
if [[ "$count" -eq 0 ]]; then
  echo "run-backlog-groom: the backlog is empty; nothing to groom" >&2
  exit 0
fi

python3 "$scripts/run-chain.py" \
  "$root/.github/chains/backlog-groom.json" "$outdir" "${dry_run[@]+"${dry_run[@]}"}"

if [[ ${#dry_run[@]} -gt 0 ]]; then
  exit 0
fi

echo "wrote $outdir/groom-final.json"
jq '{summary, backlog_size,
     findings: [.findings[] | {id, kind, issues, severity, verdict, confidence}],
     notes: (.notes | length)}' "$outdir/groom-final.json"
