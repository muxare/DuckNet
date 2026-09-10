#!/usr/bin/env bash
# Render vertical slices as markdown a human can paste into issues.
# Merge only — no Claude.
# usage: format-backlog-slice.sh SLICE.json
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 SLICE.json" >&2
  exit 2
fi

python3 - "$1" <<'PY'
import json
import sys
from pathlib import Path

data = json.loads(Path(sys.argv[1]).read_text())
slices = sorted(data.get("slices") or [], key=lambda s: s.get("order", 0))

lines = [f"## Slices of #{data.get('issue')}", "", (data.get("summary") or "").strip(), ""]

if data.get("not_sliced_because"):
    lines += [f"**Left whole:** {data['not_sliced_because']}", ""]

for s in slices:
    lines += [
        f"### {s.get('order')}. {s.get('title')}",
        "",
        f"`{s.get('size')}` · still runs afterwards: {s.get('demo')}",
        "",
        (s.get("body") or "").strip(),
        "",
        "Acceptance criteria:",
        "",
    ]
    lines += [f"- [ ] {c}" for c in s.get("acceptance_criteria") or []]
    if s.get("touches"):
        lines += ["", "Touches: " + ", ".join(f"`{p}`" for p in s["touches"])]
    lines.append("")

if data.get("notes"):
    lines += ["### Notes", ""] + [f"- {n}" for n in data["notes"]] + [""]

print("\n".join(lines))
PY
