#!/usr/bin/env bash
# Render a merged backlog-build result as markdown. Merge only — no Claude.
# usage: format-backlog-build.sh FINAL.json SHA [RUN_URL]
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 FINAL.json SHA [RUN_URL]" >&2
  exit 2
fi

python3 - "$1" "$2" "${3:-}" <<'PY'
import json
import sys
from pathlib import Path

data = json.loads(Path(sys.argv[1]).read_text())
sha = sys.argv[2]
run_url = sys.argv[3] if len(sys.argv) > 3 else ""
short = sha[:7] if sha else "unknown"

items = list(data.get("items") or [])
notes = list(data.get("notes") or [])
sources = list(data.get("sources") or [])
DEAD = {"already-done", "unfounded"}
BAR = 0.6


def cell(value):
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


def conf(item):
    c = item.get("confidence")
    return "—" if c is None else f"{float(c):.2f}"


def dropped(item):
    if item.get("state") in DEAD:
        return True
    c = item.get("confidence")
    return c is not None and float(c) < BAR


kept = [i for i in items if not dropped(i)]
gone = [i for i in items if dropped(i)]
# Epics first, then by confidence descending: the reading order is the board order.
kept.sort(key=lambda i: (0 if i.get("kind") == "epic" else 1,
                         -(1.0 if i.get("confidence") is None else float(i["confidence"]))))

lines = [
    "## Backlog build",
    "",
    (data.get("summary") or "No summary.").strip(),
    "",
    f"{len(kept)} item(s) to file, {len(gone)} knocked out by the independent pass.",
    "",
]

if kept:
    lines += [
        "| id | kind | title | state | conf | size |",
        "| --- | --- | --- | --- | ---: | --- |",
    ]
    for i in kept:
        lines.append(
            f"| `{cell(i.get('id'))}` | {cell(i.get('kind'))} | {cell(i.get('title'))} "
            f"| {cell(i.get('state') or 'unassessed')} | {conf(i)} | {cell(i.get('size'))} |"
        )
    lines.append("")

    for i in kept:
        lines += [f"<details><summary><code>{cell(i.get('id'))}</code> — {cell(i.get('title'))}</summary>", ""]
        if i.get("parent"):
            lines.append(f"**Parent:** `{cell(i['parent'])}`")
        if i.get("depends_on"):
            lines.append("**Depends on:** " + ", ".join(f"`{cell(d)}`" for d in i["depends_on"]))
        if i.get("confidence_rationale"):
            lines.append(f"**Independent pass:** {i['confidence_rationale']}")
        body = (i.get("body") or "").strip()
        if body:
            lines += ["", body]
        if i.get("acceptance_criteria"):
            lines += ["", "Acceptance criteria:", ""]
            lines += [f"- {c}" for c in i["acceptance_criteria"]]
        if i.get("evidence"):
            lines += ["", "Evidence:", ""]
            lines += [
                f"- `{cell(e.get('source'))}` — {cell(e.get('detail'))}"
                for e in i["evidence"]
            ]
        lines += ["", "</details>", ""]

if gone:
    lines += ["### Not filed", "", "| id | title | state | conf | why |", "| --- | --- | --- | ---: | --- |"]
    for i in gone:
        why = "already built or unfounded" if i.get("state") in DEAD else "below the confidence bar"
        lines.append(
            f"| `{cell(i.get('id'))}` | {cell(i.get('title'))} | {cell(i.get('state'))} "
            f"| {conf(i)} | {why} |"
        )
    lines.append("")

if sources:
    lines += ["### Sources read", ""]
    lines += [f"- `{cell(s.get('ref'))}` — {cell(s.get('used'))}" for s in sources]
    lines.append("")

if notes:
    lines += ["### Notes", ""]
    lines += [f"- {n}" for n in notes]
    lines.append("")

footer = f"_Commit `{short}`"
if run_url:
    footer += f" · [run]({run_url})"
footer += " · advisory: `backlog-build` proposes, humans decide._"
lines.append(footer)

print("\n".join(lines))
PY
