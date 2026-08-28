#!/usr/bin/env bash
# Pull structured_output from a Claude envelope, or write a degraded findings file.
# Usage: extract-findings.sh REVIEWER RAW.json OUT.json
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 REVIEWER RAW.json OUT.json" >&2
  exit 2
fi

reviewer=$1
raw=$2
out=$3

if [[ -s "$raw" ]] && jq -e '.structured_output | type == "object"' "$raw" >/dev/null 2>&1; then
  jq --arg r "$reviewer" '
    .structured_output
    | .reviewer = $r
    | .findings = (.findings // [])
    | .notes = (.notes // [])
  ' "$raw" > "$out"
else
  subtype="no_structured_output"
  if [[ -s "$raw" ]]; then
    subtype=$(jq -r '.subtype // "no_structured_output"' "$raw" 2>/dev/null || echo "no_structured_output")
  fi
  jq -n --arg r "$reviewer" --arg e "$subtype" \
    '{reviewer: $r, findings: [], notes: [], error: $e}' > "$out"
fi
