#!/usr/bin/env bash
# Merge git file list + optional Claude triage JSON into review-state.json.
# Usage: merge-triage-state.sh PR.json FILES.json [TRIAGE.json] > review-state.json
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 PR.json FILES.json [TRIAGE.json]" >&2
  exit 2
fi

pr_file=$1
files_file=$2
claude_file=${3:-}

dir=$(cd "$(dirname "$0")" && pwd)

if [[ -n "$claude_file" ]]; then
  jq -n \
    --argjson pr "$(cat "$pr_file")" \
    --argjson git "$(cat "$files_file")" \
    --argjson claude "$(cat "$claude_file")" \
    -f "$dir/merge-triage-state.jq"
else
  jq -n \
    --argjson pr "$(cat "$pr_file")" \
    --argjson git "$(cat "$files_file")" \
    --argjson claude 'null' \
    -f "$dir/merge-triage-state.jq"
fi
