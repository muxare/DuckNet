#!/usr/bin/env bash
# Join independent confidence assessments into scan findings by id.
# Merge only — no Claude. Prints the merged findings object on stdout.
# usage: merge-refactor-confidence.sh FINDINGS.json VERDICTS.json
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 FINDINGS.json VERDICTS.json" >&2
  exit 2
fi

jq --slurpfile v "$2" '
  (($v[0].assessments // []) | map({key: .id, value: .}) | from_entries) as $byid
  | .findings = (.findings | map(
      . as $f
      | $f + {
          confidence: ($byid[$f.id].confidence // null),
          confidence_rationale: ($byid[$f.id].rationale // null)
        }
    ))
  | .notes = (.notes
      + ($v[0].notes // [])
      + [.findings[] | select(.confidence == null)
         | "no independent confidence assessment for finding \(.id)"])
' "$1"
