#!/usr/bin/env bash
# Print a unified diff limited to files triage tagged with AREA.
# Usage: filter-diff.sh REVIEW-STATE.json AREA BASE...HEAD [max-bytes]
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "usage: $0 REVIEW-STATE.json AREA GIT_RANGE [max-bytes]" >&2
  exit 2
fi

state=$1
area=$2
range=$3
max_bytes=${4:-200000}

mapfile -t paths < <(jq -r --arg a "$area" '
  .files[] | select(.areas | index($a)) | .path
' "$state")

if [[ ${#paths[@]} -eq 0 ]]; then
  printf '(no files tagged %s)\n' "$area"
  exit 0
fi

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT

# git diff -- paths; ignore untracked-path noise from renames/deletes
git diff "$range" -- "${paths[@]}" > "$tmp" || true

bytes=$(wc -c < "$tmp")
head -c "$max_bytes" "$tmp"
if [[ "$bytes" -gt "$max_bytes" ]]; then
  printf '\n\n**NOTE: this specialist diff was TRUNCATED at %s of %s bytes.**\n' \
    "$max_bytes" "$bytes"
fi
