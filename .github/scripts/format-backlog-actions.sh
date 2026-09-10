#!/usr/bin/env bash
# Render an issue action plan as markdown. Merge only — no Claude.
#
# This is the text a human reads *before* approving the apply job, so it says
# exactly what will be written to the backlog: every title, every issue number,
# every drop and why. The plan JSON is the artifact that gets applied; this is a
# faithful rendering of that file, not a fresh summary of the backlog.
#
# usage: format-backlog-actions.sh ACTIONS.json [RUN_URL]
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 ACTIONS.json [RUN_URL]" >&2
  exit 2
fi

python3 - "$1" "${2:-}" <<'PY'
import json
import sys
from pathlib import Path

plan = json.loads(Path(sys.argv[1]).read_text())
run_url = sys.argv[2] if len(sys.argv) > 2 else ""

actions = list(plan.get("actions") or [])
dropped = list(plan.get("dropped") or [])
summary = plan.get("summary") or {}


def cell(value):
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


def of(kind):
    return [a for a in actions if a.get("action") == kind]


create, update, skip = of("create"), of("update"), of("skip")

lines = [
    "## Backlog plan",
    "",
    f"**{len(create)} to create, {len(update)} to update, {len(skip)} skipped "
    f"(already closed), {len(dropped)} dropped by the independent pass.**",
    "",
    "Nothing reaches the backlog until the `apply` job is approved.",
    "",
]

if create:
    lines += ["### Create", "", "| key | title | labels |", "| --- | --- | --- |"]
    for a in create:
        labels = ", ".join(f"`{cell(l)}`" for l in (a.get("labels") or [])) or "—"
        lines.append(f"| `{cell(a.get('key'))}` | {cell(a.get('title'))} | {labels} |")
    lines.append("")

if update:
    lines += ["### Update", "", "| key | issue | title | why |", "| --- | --- | --- | --- |"]
    for a in update:
        lines.append(
            f"| `{cell(a.get('key'))}` | #{cell(a.get('number'))} | {cell(a.get('title'))} "
            f"| {cell(a.get('reason') or 'matched an existing issue')} |"
        )
    lines.append("")

if skip:
    lines += ["### Skipped — closed already", "", "| key | issue | title |", "| --- | --- | --- |"]
    for a in skip:
        lines.append(
            f"| `{cell(a.get('key'))}` | #{cell(a.get('number'))} | {cell(a.get('title'))} |"
        )
    lines.append("")

if dropped:
    lines += ["### Dropped — never filed", "", "| id | title | why |", "| --- | --- | --- |"]
    for d in dropped:
        lines.append(
            f"| `{cell(d.get('id'))}` | {cell(d.get('title'))} | {cell(d.get('reason'))} |"
        )
    lines.append("")

if not actions and not dropped:
    lines += ["Nothing to file. The chain proposed no items.", ""]

if summary:
    lines += [
        "<details><summary>Raw counts</summary>",
        "",
        "```json",
        json.dumps(summary, indent=2),
        "```",
        "",
        "</details>",
        "",
    ]

footer = "_Plan produced by `backlog-build`"
if run_url:
    footer += f" · [run]({run_url})"
footer += " · advisory: the chain proposes, a human approves._"
lines.append(footer)

print("\n".join(lines))
PY
