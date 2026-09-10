#!/usr/bin/env bash
# Render a merged backlog-groom result as the sticky report issue body.
# Merge only — no Claude.
# usage: format-backlog-groom.sh FINAL.json SHA [RUN_URL]
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 FINAL.json SHA [RUN_URL]" >&2
  exit 2
fi

python3 - "$1" "$2" "${3:-}" <<'PY'
import json
import sys
from pathlib import Path

STICKY = "<!-- ducknet-backlog-groom -->"
BAR = 0.5

SECTIONS = [
    ("refinement", "Needs refinement", "Not pickup-ready as written."),
    ("grouping", "Belongs under a common parent", "Two or more issues that are one user story."),
    ("duplicate", "Duplicates", "Same work, filed twice."),
    ("stale", "Stale — close candidates", "The premise no longer holds in the code. Verify before closing."),
    ("gap", "Gaps", "Work the code or documents imply that no issue covers."),
    ("ordering", "Ordering", "One issue has to land before another."),
]

data = json.loads(Path(sys.argv[1]).read_text())
sha = sys.argv[2]
run_url = sys.argv[3] if len(sys.argv) > 3 else ""
short = sha[:7] if sha else "unknown"

findings = list(data.get("findings") or [])
notes = list(data.get("notes") or [])


def cell(value):
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


def conf(item):
    c = item.get("confidence")
    return "—" if c is None else f"{float(c):.2f}"


def held(item):
    if item.get("verdict") == "does-not-hold":
        return False
    c = item.get("confidence")
    return c is None or float(c) >= BAR


def refs(numbers):
    return ", ".join(f"#{n}" for n in numbers or []) or "—"


kept = [f for f in findings if held(f)]
gone = [f for f in findings if not held(f)]
rank = {"blocker": 0, "major": 1, "minor": 2}
kept.sort(key=lambda f: (rank.get(f.get("severity"), 3),
                         -(1.0 if f.get("confidence") is None else float(f["confidence"]))))

lines = [
    STICKY,
    f"<!-- sha: {sha} -->",
    "# Backlog grooming report",
    "",
    (data.get("summary") or "No summary.").strip(),
    "",
    f"{data.get('backlog_size', 0)} issue(s) read · {len(kept)} finding(s) held · "
    f"{len(gone)} dismissed by the independent pass.",
    "",
    "Advisory. Nothing here has been applied to any issue.",
    "",
]

for kind, heading, blurb in SECTIONS:
    group = [f for f in kept if f.get("kind") == kind]
    if not group:
        continue
    lines += [f"## {heading}", "", blurb, "", "| issues | finding | severity | verdict | conf |",
              "| --- | --- | --- | --- | ---: |"]
    for f in group:
        lines.append(
            f"| {refs(f.get('issues'))} | {cell(f.get('title'))} | {cell(f.get('severity'))} "
            f"| {cell(f.get('verdict') or 'unassessed')} | {conf(f)} |"
        )
    lines.append("")
    for f in group:
        # A gap concerns no issue: "— — title" would be noise.
        label = cell(f.get("title"))
        if f.get("issues"):
            label = f"{refs(f['issues'])} — {label}"
        lines += [
            f"<details><summary>{label}</summary>",
            "",
            (f.get("detail") or "").strip(),
            "",
        ]
        if f.get("missing"):
            lines += ["**Missing:** " + ", ".join(f"`{m}`" for m in f["missing"]), ""]
        if f.get("keep"):
            lines += [f"**Keep:** #{f['keep']}", ""]
        if f.get("blocked_by"):
            lines += ["**Blocked by:** " + refs(f["blocked_by"]), ""]
        if f.get("confidence_rationale"):
            lines += [f"**Independent pass:** {f['confidence_rationale']}", ""]
        if f.get("evidence"):
            lines += ["Evidence:", ""]
            lines += [f"- `{cell(e.get('source'))}` — {cell(e.get('detail'))}" for e in f["evidence"]]
            lines.append("")
        if f.get("suggested_title"):
            lines += [f"Suggested title: **{cell(f['suggested_title'])}**", ""]
        if f.get("suggested_body"):
            body = f["suggested_body"]
            ticks = "````" if "```" in body else "```"
            lines += ["Suggested body:", "", f"{ticks}markdown", body.rstrip(), ticks, ""]
        lines += ["</details>", ""]

if gone:
    lines += ["## Dismissed", "",
              "The independent pass did not sustain these. Listed so a recurring dismissal is visible.",
              "", "| issues | finding | kind | verdict | conf | why |",
              "| --- | --- | --- | --- | ---: | --- |"]
    for f in gone:
        why = f.get("confidence_rationale") or "below the confidence bar"
        lines.append(
            f"| {refs(f.get('issues'))} | {cell(f.get('title'))} | {cell(f.get('kind'))} "
            f"| {cell(f.get('verdict'))} | {conf(f)} | {cell(why)} |"
        )
    lines.append("")

if notes:
    lines += ["## Notes", ""] + [f"- {n}" for n in notes] + [""]

footer = f"_Commit `{short}`"
if run_url:
    footer += f" · [run]({run_url})"
footer += " · rewritten in place each run by `backlog-groom.yml`; advisory, not a merge gate._"
lines.append(footer)

print("\n".join(lines))
PY
