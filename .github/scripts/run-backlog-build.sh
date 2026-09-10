#!/usr/bin/env bash
# Build a DuckNet backlog from description/vision documents and/or the code.
#
# Two isolated Claude sessions joined by schema: build proposes, an independent
# pass checks every candidate against the repository, jq merges. Neither the
# script nor CI reads prose — only validated structured output.
#
# usage: run-backlog-build.sh [OUTDIR] [--dry-run] [-- SOURCE_PATH ...]
#   OUTDIR        default /tmp/backlog-build
#   --dry-run     assemble every stage input, skip the model calls
#   SOURCE_PATH   documents or directories to read instead of the repo defaults
#
# Either input suffices: with no readable document the chain builds from code,
# and it says so. Result: OUTDIR/backlog-final.json (+ OUTDIR/chain-meta.json).
# Issue markdown: format-backlog-build.sh; issue actions: plan-backlog-issues.py.
# env overrides: BUILD_MODEL / VERIFY_MODEL, BUILD_BUDGET_USD (1.50),
#   VERIFY_BUDGET_USD (1.00), BUILD_MAX_TURNS, VERIFY_MAX_TURNS,
#   SKIP_ISSUE_DUMP=1 (do not call gh)
# Nested scripts are invoked with bash so GitHub Actions does not depend on +x.
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
scripts="$root/.github/scripts"

outdir=
dry_run=()
sources=()
seen_sep=0
for arg in "$@"; do
  if [[ $seen_sep -eq 1 ]]; then
    sources+=("$arg")
  elif [[ "$arg" == "--" ]]; then
    seen_sep=1
  elif [[ "$arg" == "--dry-run" ]]; then
    dry_run+=("--dry-run")
  elif [[ -z "$outdir" ]]; then
    outdir=$arg
  else
    echo "unexpected argument: $arg" >&2
    exit 2
  fi
done
outdir=${outdir:-/tmp/backlog-build}
mkdir -p "$outdir"

BACKLOG_SOURCES_FILE="$outdir/sources.md"
export BACKLOG_SOURCES_FILE
bash "$scripts/collect-backlog-sources.sh" \
  "$BACKLOG_SOURCES_FILE" "${sources[@]+"${sources[@]}"}"

# The existing backlog is context, not a requirement: a checkout without gh, or
# without repo access, must still be able to build from documents and code.
if [[ "${SKIP_ISSUE_DUMP:-0}" != "1" ]]; then
  if bash "$scripts/dump-issues.sh" "$outdir/issues.json" open 200; then
    BACKLOG_ISSUES_FILE="$outdir/issues.json"
    export BACKLOG_ISSUES_FILE
  else
    echo "run-backlog-build: no existing backlog available; continuing without it" >&2
  fi
fi

python3 "$scripts/run-chain.py" \
  "$root/.github/chains/backlog-build.json" "$outdir" "${dry_run[@]+"${dry_run[@]}"}"

if [[ ${#dry_run[@]} -gt 0 ]]; then
  exit 0
fi

echo "wrote $outdir/backlog-final.json"
jq '{summary,
     items: [.items[] | {id, kind, title, state, confidence}],
     notes: (.notes | length)}' "$outdir/backlog-final.json"
