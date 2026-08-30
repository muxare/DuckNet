#!/usr/bin/env bash
# Deterministic merge of specialist findings into review-state + PR comment markdown.
# Usage:
#   aggregate-review.sh --state FILE --out-state FILE --out-comment FILE
#                       [--out-summary FILE] [--sha HEX] [--findings FILE]...
set -euo pipefail

state=
out_state=
out_comment=
out_summary=
sha=unknown
findings_files=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --state) state=$2; shift 2 ;;
    --out-state) out_state=$2; shift 2 ;;
    --out-comment) out_comment=$2; shift 2 ;;
    --out-summary) out_summary=$2; shift 2 ;;
    --sha) sha=$2; shift 2 ;;
    --findings) findings_files+=("$2"); shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$state" || -z "$out_state" || -z "$out_comment" ]]; then
  echo "usage: $0 --state FILE --out-state FILE --out-comment FILE [--out-summary FILE] [--sha HEX] [--findings FILE]..." >&2
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
        specialists: $specialists,
        sha: $sha
      }
  ' > "$work"

jq '.state' "$work" > "$out_state"

if [[ -n "$out_summary" ]]; then
  jq '{
    verdict,
    requested,
    ran,
    degraded,
    skipped: .state.skipped,
    risk: .state.risk,
    specialists
  }' "$work" > "$out_summary"
fi

python3 - "$work" "$out_comment" <<'PY'
import json
import sys
from pathlib import Path

agg = json.loads(Path(sys.argv[1]).read_text())
out = Path(sys.argv[2])
state = agg["state"]
verdict = agg["verdict"]
degraded = agg["degraded"]
requested = agg["requested"]
ran = agg["ran"]
specialists = agg.get("specialists") or []
sha = (agg.get("sha") or "unknown")[:7]
approved = verdict == "approve"
icon = "✅" if approved else "⚠️"
label = "approve" if approved else "request changes"
risk = state["risk"]["level"]
reasons = state["risk"].get("reasons") or []
skipped = bool(state.get("skipped"))
findings = state.get("findings") or []
files = state.get("files") or []
ran_set = set(ran)
req_set = set(requested)
FILE_COLLAPSE = 15

ROLES = {
    "triage": "Classify risk, tag files, pick specialists",
    "architecture": "Five CLAUDE.md rules on tagged diff",
    "security": "Secrets, envelope parse, /bus, auth",
    "aggregate": "Merge JSON → this comment",
}


def cell(value):
    return str(value or "").replace("|", "\\|").replace("\n", " ")


def specialist_result(name):
    spec = next((s for s in specialists if s.get("reviewer") == name), None)
    n = len((spec or {}).get("findings") or [])
    err = (spec or {}).get("error") or ""
    if name in ran_set:
        extra = f" · {err}" if err else f" · {n} finding{'s' if n != 1 else ''}"
        return f"`{name}` ran{extra}"
    if name in req_set:
        return f"`{name}` requested but produced no artifact"
    return f"`{name}` skipped"


if skipped or not requested:
    triage_result = f"{risk} · no specialists"
else:
    names = ", ".join(requested) if requested else "none"
    triage_result = f"{risk} · {names}"

lines = [
    f"## {icon} Claude review — **{label}**",
    "",
    "Advisory — `ci.yml` decides merge.",
    "",
    "### What ran",
    "",
    "| Stage | Role | Result |",
    "|---|---|---|",
    f"| triage | {ROLES['triage']} | {triage_result} |",
    f"| architecture | {ROLES['architecture']} | {specialist_result('architecture')} |",
    f"| security | {ROLES['security']} | {specialist_result('security')} |",
    f"| aggregate | {ROLES['aggregate']} | {label} |",
]

if skipped or not requested:
    lines.extend(["", "No specialist review (low risk or nothing to inspect)."])

lines.extend(["", f"### Why this is {risk} risk"])
if reasons:
    lines.append("")
    for reason in reasons:
        lines.append(f"- {reason}")
else:
    lines.extend(["", "_No triage reasons._"])

if files:
    file_heading = f"Files ({len(files)})"
    if len(files) > FILE_COLLAPSE:
        lines.extend(["", f"<details><summary>{file_heading}</summary>", ""])
    else:
        lines.extend(["", f"### {file_heading}", ""])
    lines.append("| Path | Risk | Areas |")
    lines.append("|---|---|---|")
    for item in files:
        areas = ", ".join(item.get("areas") or []) or "—"
        lines.append(
            f"| `{cell(item.get('path'))}` | {cell(item.get('risk'))} | {cell(areas)} |"
        )
    if len(files) > FILE_COLLAPSE:
        lines.extend(["", "</details>"])

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
    lines.extend(["", "### Findings", "", "None"])

notes_by_reviewer = []
for spec in specialists:
    notes = spec.get("notes") or []
    if notes:
        notes_by_reviewer.append((spec.get("reviewer") or "unknown", notes))

if notes_by_reviewer:
    lines.extend(["", "### Notes"])
    for reviewer, notes in notes_by_reviewer:
        lines.extend(["", f"**{reviewer}**"])
        for note in notes:
            lines.append(f"- {note}")

if degraded:
    lines.extend(["", "### Degraded specialists"])
    for item in degraded:
        lines.append(f"- `{item.get('reviewer')}`: {item.get('error')}")

triage_obj = {
    "risk": state.get("risk"),
    "files": files,
    "requestedReviewers": requested,
    "skipped": skipped,
}
aggregate_obj = {
    "verdict": verdict,
    "requested": requested,
    "ran": ran,
    "degraded": degraded,
}


def fence_json(obj):
    return "```json\n" + json.dumps(obj, indent=2, ensure_ascii=False) + "\n```"


lines.extend(
    [
        "",
        "<details>",
        "<summary>Structured objects</summary>",
        "",
        "**Triage**",
        "",
        fence_json(triage_obj),
        "",
    ]
)
for spec in specialists:
    name = spec.get("reviewer") or "specialist"
    lines.extend([f"**{name}**", "", fence_json(spec), ""])
lines.extend(
    [
        "**Aggregate**",
        "",
        fence_json(aggregate_obj),
        "",
        "</details>",
        "",
        f"<sub>Advisory only — `ci.yml` decides merge. Commit {sha}</sub>",
    ]
)
out.write_text("\n".join(lines) + "\n")
PY
