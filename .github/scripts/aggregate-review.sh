#!/usr/bin/env bash
# Deterministic merge of specialist findings into review-state + PR comment markdown.
# Usage:
#   aggregate-review.sh --state FILE --out-state FILE --out-comment FILE
#                       [--sha HEX] [--findings FILE]...
set -euo pipefail

state=
out_state=
out_comment=
sha=unknown
findings_files=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --state) state=$2; shift 2 ;;
    --out-state) out_state=$2; shift 2 ;;
    --out-comment) out_comment=$2; shift 2 ;;
    --sha) sha=$2; shift 2 ;;
    --findings) findings_files+=("$2"); shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$state" || -z "$out_state" || -z "$out_comment" ]]; then
  echo "usage: $0 --state FILE --out-state FILE --out-comment FILE [--sha HEX] [--findings FILE]..." >&2
  exit 2
fi

payload='[]'
for f in "${findings_files[@]+"${findings_files[@]}"}"; do
  if [[ -f "$f" ]]; then
    payload=$(jq -c --argjson acc "$payload" '. as $x | $acc + [$x]' "$f")
  fi
done

work=$(mktemp)
trap 'rm -f "$work"' EXIT

jq -n \
  --argjson state "$(cat "$state")" \
  --argjson specialists "$payload" \
  --arg sha "$sha" \
  '
    def tag($r):
      . + {reviewer: (.reviewer // $r)};

    ($specialists | map(select((.error // "") != "")) | map({reviewer, error})) as $degraded
    | ($specialists | map(.notes // []) | add // []) as $notes
    | ($specialists | map(
        . as $s
        | ($s.findings // [])
        | map(tag($s.reviewer))
      ) | add // []) as $all
    | ($state + {findings: $all}) as $merged
    | {
        state: $merged,
        verdict: (
          if any($all[]; .severity == "critical" or .severity == "major")
          then "request_changes" else "approve" end
        ),
        degraded: $degraded,
        notes: $notes,
        requested: ($merged.requestedReviewers // []),
        ran: ($specialists | map(.reviewer) | unique),
        sha: $sha
      }
  ' > "$work"

jq '.state' "$work" > "$out_state"

python3 - "$work" "$out_comment" <<'PY'
import json
import sys
from pathlib import Path

agg = json.loads(Path(sys.argv[1]).read_text())
out = Path(sys.argv[2])
state = agg["state"]
verdict = agg["verdict"]
degraded = agg["degraded"]
notes = agg.get("notes") or []
requested = agg["requested"]
ran = agg["ran"]
sha = (agg.get("sha") or "unknown")[:7]
approved = verdict == "approve"
icon = "✅" if approved else "⚠️"
label = "approve" if approved else "request changes"
risk = state["risk"]["level"]
reasons = state["risk"].get("reasons") or []
skipped = bool(state.get("skipped"))
findings = state.get("findings") or []
ran_set = set(ran)
req_set = set(requested)


def cell(value):
    return str(value or "").replace("|", "\\|").replace("\n", " ")


lines = [
    f"## {icon} Claude review — **{label}**",
    "",
    f"Triage: **{risk}** risk",
]
if reasons:
    lines.append("")
    for reason in reasons:
        lines.append(f"- {reason}")

if skipped or not requested:
    lines.extend(["", "No specialist review (low risk or nothing to inspect)."])
else:
    parts = []
    for name in ("architecture", "security"):
        if name in ran_set:
            parts.append(f"`{name}` ran")
        elif name in req_set:
            parts.append(f"`{name}` requested but produced no artifact")
        else:
            parts.append(f"`{name}` skipped")
    lines.extend(["", "Reviewers: " + "; ".join(parts) + "."])

if findings:
    lines.extend(["", f"### Findings ({len(findings)})", ""])
    lines.append("| Reviewer | Severity | File | Detail |")
    lines.append("|---|---|---|---|")
    for item in findings:
        path = item.get("file")
        file_cell = f"`{cell(path)}`" if path else "—"
        extra = f" (rule {item['rule']})" if item.get("rule") is not None else ""
        lines.append(
            f"| {cell(item.get('reviewer'))} | {cell(item.get('severity'))} | {file_cell} | {cell(item.get('detail'))}{extra} |"
        )
elif not skipped and requested:
    lines.extend(["", "No specialist findings."])

if notes:
    lines.extend(["", "### Notes"])
    for note in notes:
        lines.append(f"- {note}")

if degraded:
    lines.extend(["", "### Degraded specialists"])
    for item in degraded:
        lines.append(f"- `{item.get('reviewer')}`: {item.get('error')}")

lines.extend(
    [
        "",
        f"<sub>Advisory only — `ci.yml` decides merge. Commit {sha}</sub>",
    ]
)
out.write_text("\n".join(lines) + "\n")
PY
