#!/usr/bin/env bash
# Join an independent assessment pass into a producing stage's array, by id.
# Merge only — no Claude. Prints the merged object on stdout.
#
# usage: merge-confidence.sh ITEMS.json VERDICTS.json [ARRAY] [ID_FIELD] [NOUN]
#   ITEMS.json    producing stage output, with .<ARRAY>[] each carrying <ID_FIELD>
#   VERDICTS.json assessment stage output, with .assessments[] {id, confidence, rationale, ...}
#   ARRAY         default "items"
#   ID_FIELD      default "id"
#   NOUN          what one element is called in the unassessed note; default ID_FIELD
#
# Every field the assessor returned other than `id` is copied onto the item,
# prefixed nowhere and renamed only for `rationale` -> `confidence_rationale`,
# so an assessment schema can carry chain-specific verdicts (e.g. `state`)
# without this script needing to know about them. An item with no assessment
# gets nulls and a note: a silently unassessed item must not look assessed.
set -euo pipefail

if [[ $# -lt 2 || $# -gt 5 ]]; then
  echo "usage: $0 ITEMS.json VERDICTS.json [ARRAY] [ID_FIELD] [NOUN]" >&2
  exit 2
fi

array=${3:-items}
id_field=${4:-id}
noun=${5:-$id_field}

jq --slurpfile v "$2" --arg array "$array" --arg idf "$id_field" --arg noun "$noun" '
  (($v[0].assessments // []) | map({key: (.id | tostring), value: .}) | from_entries) as $byid
  | .[$array] = ((.[$array] // []) | map(
      . as $item
      | ($byid[$item[$idf] | tostring]) as $a
      | $item
        + (($a // {}) | del(.id, .rationale))
        + {
            confidence: ($a.confidence // null),
            confidence_rationale: ($a.rationale // null)
          }
    ))
  | .notes = ((.notes // [])
      + ($v[0].notes // [])
      + [.[$array][] | select(.confidence == null)
         | "no independent assessment for \($noun) \(.[$idf])"])
' "$1"
