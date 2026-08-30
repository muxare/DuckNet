#!/usr/bin/env bash
# Render a GitHub Actions job summary for one ReviewFlow stage.
# Prints markdown on stdout — append to $GITHUB_STEP_SUMMARY.
# Usage:
#   write-review-job-summary.sh --role ROLE --object FILE
#     [--model NAME] [--budget USD] [--schema PATH] [--tools none]
#     [--cost USD] [--turns N]
set -euo pipefail

role=
object=
model=
budget=
schema=
tools=
cost=
turns=

while [[ $# -gt 0 ]]; do
  case "$1" in
    --role) role=$2; shift 2 ;;
    --object) object=$2; shift 2 ;;
    --model) model=$2; shift 2 ;;
    --budget) budget=$2; shift 2 ;;
    --schema) schema=$2; shift 2 ;;
    --tools) tools=$2; shift 2 ;;
    --cost) cost=$2; shift 2 ;;
    --turns) turns=$2; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$role" || -z "$object" ]]; then
  echo "usage: $0 --role ROLE --object FILE [--model NAME] [--budget USD] [--schema PATH] [--tools none] [--cost USD] [--turns N]" >&2
  exit 2
fi

if [[ ! -f "$object" ]]; then
  echo "object file not found: $object" >&2
  exit 2
fi

python3 - "$role" "$object" "$model" "$budget" "$schema" "$tools" "$cost" "$turns" <<'PY'
import json
import sys
from pathlib import Path

role, object_path, model, budget, schema, tools, cost, turns = sys.argv[1:9]

ROLES = {
    "triage": (
        "Classify risk, tag files, and pick specialists.",
        "First stage. Writes `review-state.json` for later jobs. Isolated specialists read that artifact; they do not talk to each other.",
    ),
    "architecture": (
        "Enforce the five CLAUDE.md rules on architecture-tagged files.",
        "Isolated specialist after triage. No merge verdict — aggregation is `jq`.",
    ),
    "security": (
        "Check secrets, envelope parse, `/bus`, and auth on security-tagged files.",
        "Isolated specialist after triage. No merge verdict — aggregation is `jq`.",
    ),
    "aggregate": (
        "Merge specialist JSON into one sticky PR comment.",
        "Deterministic last stage (no model). Verdict never fails the workflow.",
    ),
}

if role not in ROLES:
    print(f"unknown role: {role}", file=sys.stderr)
    sys.exit(2)

what, where = ROLES[role]
obj = json.loads(Path(object_path).read_text())
pretty = json.dumps(obj, indent=2, ensure_ascii=False)

lines = [
    f"## ReviewFlow — {role}",
    "",
    what,
    "",
    where,
    "",
    "Pipeline: `triage` → (`architecture` / `security`) → `aggregate` → PR comment.",
]

ran = []
if model:
    ran.append(("Model", model))
if budget:
    ran.append(("Budget", f"${budget}"))
if schema:
    ran.append(("Schema", f"`{schema}`"))
if tools:
    ran.append(("Tools", tools))
if cost:
    ran.append(("Cost", f"${cost}"))
if turns:
    ran.append(("Turns", turns))

if ran:
    lines.extend(["", "### How it ran", "", "| | |", "|---|---|"])
    for label, value in ran:
        lines.append(f"| {label} | {value} |")

lines.extend(
    [
        "",
        "### Structured object",
        "",
        "Schema object only — not the raw Claude envelope (that can contain the prompt/diff).",
        "",
        "```json",
        pretty,
        "```",
        "",
    ]
)
print("\n".join(lines))
PY
