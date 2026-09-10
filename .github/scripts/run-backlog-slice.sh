#!/usr/bin/env bash
# Cut one oversized issue into tracer-bullet vertical slices.
#
# usage: run-backlog-slice.sh ISSUE_NUMBER [OUTDIR] [--dry-run]
# Result: OUTDIR/slice.json (+ OUTDIR/chain-meta.json)
# Readable form: format-backlog-slice.sh OUTDIR/slice.json
# env overrides: SLICE_MODEL (sonnet), SLICE_BUDGET_USD (0.60), SLICE_MAX_TURNS
#   SLICE_ISSUE_FILE (use this file instead of calling gh)
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
scripts="$root/.github/scripts"

number=
outdir=
dry_run=()
for arg in "$@"; do
  case "$arg" in
    "") ;;
    --dry-run) dry_run+=("--dry-run") ;;
    -*) echo "unknown option: $arg" >&2; exit 2 ;;
    # With SLICE_ISSUE_FILE set there is no number to give, so the first
    # positional is the output directory instead.
    [0-9]*)
      if [[ -z "$number" ]]; then number=$arg
      elif [[ -z "$outdir" ]]; then outdir=$arg
      else echo "unexpected argument: $arg" >&2; exit 2; fi ;;
    *)
      if [[ -z "$outdir" ]]; then outdir=$arg
      else echo "unexpected argument: $arg" >&2; exit 2; fi ;;
  esac
done

if [[ -z "$number" && -z "${SLICE_ISSUE_FILE:-}" ]]; then
  echo "usage: $0 ISSUE_NUMBER [OUTDIR] [--dry-run]" >&2
  exit 2
fi

outdir=${outdir:-/tmp/backlog-slice}
mkdir -p "$outdir"

if [[ -z "${SLICE_ISSUE_FILE:-}" ]]; then
  if ! [[ "$number" =~ ^[0-9]+$ ]]; then
    echo "run-backlog-slice: '$number' is not an issue number" >&2
    exit 2
  fi
  SLICE_ISSUE_FILE="$outdir/issue.json"
  export SLICE_ISSUE_FILE
  gh issue view "$number" --json number,title,body,labels,url \
    | jq '{number, title, url, labels: [ .labels[].name ], body: (.body // "")}' \
    > "$SLICE_ISSUE_FILE"
  echo "run-backlog-slice: read issue #$number" >&2
else
  export SLICE_ISSUE_FILE
fi

python3 "$scripts/run-chain.py" \
  "$root/.github/chains/backlog-slice.json" "$outdir" "${dry_run[@]+"${dry_run[@]}"}"

if [[ ${#dry_run[@]} -gt 0 ]]; then
  exit 0
fi

echo "wrote $outdir/slice.json"
jq '{issue, summary, not_sliced_because,
     slices: [.slices[] | {order, title, size, demo}]}' "$outdir/slice.json"
