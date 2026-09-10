#!/usr/bin/env bash
# Render a readiness verdict as the sticky issue comment. Merge only — no Claude.
# usage: format-readiness.sh VERDICT.json [RUN_URL]
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  echo "usage: $0 VERDICT.json [RUN_URL]" >&2
  exit 2
fi

python3 - "$1" "${2:-}" <<'PY'
import json
import sys
from pathlib import Path

STICKY = "<!-- ducknet-readiness -->"
BADGE = {
    "ready": "**Ready to pick up**",
    "needs-work": "**Needs a round of clarification**",
    "blocked": "**Blocked as written**",
}

data = json.loads(Path(sys.argv[1]).read_text())
run_url = sys.argv[2] if len(sys.argv) > 2 else ""
verdict = data.get("verdict") or "needs-work"

lines = [
    STICKY,
    f"### Readiness check — {BADGE.get(verdict, verdict)}",
    "",
    (data.get("summary") or "").strip(),
    "",
]

if data.get("rule_conflict"):
    lines += [f"> **Rule conflict:** {data['rule_conflict']}", ""]

if data.get("missing"):
    lines += ["**Missing:** " + ", ".join(f"`{m}`" for m in data["missing"]), ""]

if data.get("questions"):
    lines += ["**Questions someone would have to ask first:**", ""]
    lines += [f"{i}. {q}" for i, q in enumerate(data["questions"], 1)]
    lines.append("")

if data.get("suggested_acceptance_criteria"):
    lines += ["<details><summary>Suggested acceptance criteria</summary>", ""]
    lines += [f"- [ ] {c}" for c in data["suggested_acceptance_criteria"]]
    lines += ["", "</details>", ""]

if data.get("strengths"):
    lines += ["<details><summary>What this issue already does well</summary>", ""]
    lines += [f"- {s}" for s in data["strengths"]]
    lines += ["", "</details>", ""]

footer = "_Advisory readiness check"
if run_url:
    footer += f" · [run]({run_url})"
footer += ". It judges the issue text only — it has not read the code, and it does not gate anything._"
lines.append(footer)

print("\n".join(lines))
PY
