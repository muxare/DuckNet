#!/usr/bin/env bash
# Render merged refactor-scan findings as issue markdown. Merge only — no Claude.
# usage: format-refactor-scan.sh FINDINGS.json SHA [RUN_URL]
set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 FINDINGS.json SHA [RUN_URL]" >&2
  exit 2
fi

findings=$1
sha=$2
run_url=${3:-}

python3 - "$findings" "$sha" "$run_url" <<'PY'
import json
import sys
from pathlib import Path

data = json.loads(Path(sys.argv[1]).read_text())
sha = sys.argv[2]
run_url = sys.argv[3] if len(sys.argv) > 3 else ""
short = sha[:7] if sha else "unknown"

findings = list(data.get("findings") or [])
notes = list(data.get("notes") or [])
summary = (data.get("summary") or "").strip() or "No summary."


def cell(value):
    return str(value if value is not None else "").replace("|", "\\|").replace("\n", " ")


def fence(code):
    text = code or ""
    ticks = "````" if "```" in text else "```"
    return f"{ticks}\n{text}\n{ticks}"


def conf_key(item):
    c = item.get("confidence")
    return (-1.0 if c is None else -float(c), 0 if item.get("tier") == "patch" else 1)


def conf_cell(item):
    c = item.get("confidence")
    if c is None:
        return "—"
    return f"{float(c):.2f}"


held, weak = [], []
for item in sorted(findings, key=conf_key):
    c = item.get("confidence")
    if c is not None and float(c) < 0.6:
        weak.append(item)
    else:
        held.append(item)

lines = [
    "## Refactor scan",
    "",
    summary,
    "",
    f"{len(findings)} finding{'s' if len(findings) != 1 else ''} · commit `{short}`",
]
if run_url:
    lines[-1] += f" · [run]({run_url})"

lines.extend(
    [
        "",
        "Advisory only — not a merge gate. CI creates or updates one GitHub issue per held patch finding and per plan-tier `proposed_issues` item.",
    ]
)

if held:
    lines.extend(["", "### Findings", "", "| Id | Tier | Category | Confidence | Effort | Risk | Title |", "|---|---|---|---|---|---|---|"])
    for item in held:
        lines.append(
            "| `{id}` | {tier} | {cat} | {conf} | {effort} | {risk} | {title} |".format(
                id=cell(item.get("id")),
                tier=cell(item.get("tier")),
                cat=cell(item.get("category")),
                conf=conf_cell(item),
                effort=cell(item.get("effort")),
                risk=cell(item.get("risk")),
                title=cell(item.get("title")),
            )
        )

    for item in held:
        ident = item.get("id") or "finding"
        title = item.get("title") or ident
        lines.extend(["", f"#### {title}", "", f"- **id:** `{ident}` · **tier:** {item.get('tier')} · **category:** {item.get('category')}"])
        lines.append(
            f"- **effort:** {item.get('effort')} · **risk:** {item.get('risk')} · **confidence:** {conf_cell(item)}"
        )
        files = item.get("files") or []
        if files:
            lines.append("- **files:** " + ", ".join(f"`{p}`" for p in files))
        rationale = item.get("confidence_rationale")
        if rationale:
            lines.append(f"- **independent pass:** {rationale}")
        if item.get("detail"):
            lines.extend(["", item["detail"]])
        if item.get("tier") == "patch":
            if item.get("snippet"):
                lines.extend(["", "Current:", "", fence(item["snippet"])])
            if item.get("suggestion"):
                lines.extend(["", "Suggestion:", "", fence(item["suggestion"])])
        drafts = item.get("proposed_issues") or []
        if drafts:
            lines.extend(["", "**Tasks (CI creates or updates issues):**", ""])
            for i, draft in enumerate(drafts):
                deps = [d + 1 for d in (draft.get("depends_on") or [])]
                dep = f" (depends on {deps})" if deps else ""
                labels = ", ".join(f"`{x}`" for x in (draft.get("labels") or []))
                lines.append(f"{i + 1}. **{draft.get('title') or 'untitled'}**{dep}")
                if labels:
                    lines.append(f"   Labels: {labels}")
                body = (draft.get("body") or "").strip()
                if body:
                    for line in body.splitlines():
                        lines.append(f"   > {line}" if line else "   >")
                    lines.append("")

if weak:
    lines.extend(
        [
            "",
            "### Independent pass disagreed",
            "",
            "These did not clear the 0.6 confidence bar. They stay here so a human can overrule.",
            "",
            "| Id | Confidence | Title | Rationale |",
            "|---|---|---|---|",
        ]
    )
    for item in weak:
        lines.append(
            "| `{id}` | {conf} | {title} | {why} |".format(
                id=cell(item.get("id")),
                conf=conf_cell(item),
                title=cell(item.get("title")),
                why=cell(item.get("confidence_rationale") or item.get("detail")),
            )
        )

if not findings:
    lines.extend(["", "No refactoring opportunities cleared the bar."])

if notes:
    lines.extend(["", "### Notes"])
    for note in notes:
        lines.append(f"- {note}")

print("\n".join(lines) + "\n")
PY
