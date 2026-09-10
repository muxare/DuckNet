#!/usr/bin/env bash
# Judge whether one issue is ready to be picked up. One Haiku session, no
# tools, one validated verdict. Cheap enough to run on every issue edit.
#
# usage: run-backlog-readiness.sh ISSUE.json [OUTDIR] [--dry-run]
#   ISSUE.json  {number, title, body, labels?}
# Result: OUTDIR/readiness.json (+ OUTDIR/chain-meta.json)
# Comment markdown: format-readiness.sh OUTDIR/readiness.json [RUN_URL]
# env overrides: READINESS_MODEL (haiku), READINESS_BUDGET_USD (0.10)
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
scripts="$root/.github/scripts"

issue=
outdir=
dry_run=()
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run+=("--dry-run") ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    *)
      if [[ -z "$issue" ]]; then issue=$arg
      elif [[ -z "$outdir" ]]; then outdir=$arg
      else echo "unexpected argument: $arg" >&2; exit 2; fi ;;
  esac
done

if [[ -z "$issue" ]]; then
  echo "usage: $0 ISSUE.json [OUTDIR] [--dry-run]" >&2
  exit 2
fi
if [[ ! -f "$issue" ]]; then
  echo "run-backlog-readiness: no such file: $issue" >&2
  exit 2
fi

outdir=${outdir:-/tmp/backlog-readiness}
mkdir -p "$outdir"

READINESS_ISSUE_FILE=$(cd -- "$(dirname -- "$issue")" && pwd)/$(basename -- "$issue")
export READINESS_ISSUE_FILE

python3 "$scripts/run-chain.py" \
  "$root/.github/chains/backlog-readiness.json" "$outdir" "${dry_run[@]+"${dry_run[@]}"}"

if [[ ${#dry_run[@]} -gt 0 ]]; then
  exit 0
fi

jq '{issue, verdict, summary, missing}' "$outdir/readiness.json"
