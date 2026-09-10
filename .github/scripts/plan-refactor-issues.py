#!/usr/bin/env python3
"""Turn merged refactor-scan findings into GitHub issue actions.

Merge only — no Claude. Held findings (confidence missing or >= 0.6) become
issues: one per patch finding, one per plan-tier proposed_issues item.

The matching rules live in `issue_plan.IssuePlanner`, shared with the backlog
chains: match open issues by `<!-- ducknet-refactor:KEY -->` first, then by
normalised title; a closed match skips create; an update replaces the generated
block and keeps whatever a human wrote outside the markers.

usage: plan-refactor-issues.py FINDINGS.json EXISTING.json SHA [RUN_URL]
EXISTING.json: [{number, title, body, state, labels?}, ...]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from issue_plan import IssuePlanner  # noqa: E402

CONFIDENCE_BAR = 0.6
NAMESPACE = "ducknet-refactor"
STICKY_MARKER = "<!-- ducknet-refactor-scan -->"

PLANNER = IssuePlanner(NAMESPACE, sticky_marker=STICKY_MARKER)


def held(item: dict) -> bool:
    c = item.get("confidence")
    if c is None:
        return True
    return float(c) >= CONFIDENCE_BAR


def unique_labels(*groups):
    out = []
    for group in groups:
        for label in group or []:
            if label and label not in out:
                out.append(label)
    if "refactoring" not in out:
        out.append("refactoring")
    if "refactor-scan" not in out:
        out.append("refactor-scan")
    return out


def work_items(findings: list) -> list:
    items = []
    for finding in findings:
        if not held(finding):
            continue
        drafts = finding.get("proposed_issues") or []
        if finding.get("tier") == "plan" and drafts:
            fid = finding["id"]
            for i, draft in enumerate(drafts):
                deps = [f"{fid}:{j}" for j in (draft.get("depends_on") or [])]
                items.append(
                    {
                        "key": f"{fid}:{i}",
                        "title": draft.get("title") or finding.get("title") or fid,
                        "labels": unique_labels(draft.get("labels")),
                        "depends_on_keys": deps,
                        "finding": finding,
                        "draft": draft,
                    }
                )
            continue
        ident = finding.get("id") or "finding"
        items.append(
            {
                "key": ident,
                "title": finding.get("title") or ident,
                "labels": unique_labels(),
                "depends_on_keys": [],
                "finding": finding,
                "draft": None,
            }
        )
    return items


def fence(code: str) -> str:
    text = code or ""
    ticks = "````" if "```" in text else "```"
    return f"{ticks}\n{text}\n{ticks}"


def conf_cell(finding: dict) -> str:
    c = finding.get("confidence")
    if c is None:
        return "—"
    return f"{float(c):.2f}"


def generated_block(item: dict, sha: str, run_url: str) -> str:
    finding = item["finding"]
    draft = item.get("draft")
    key = item["key"]
    short = sha[:7] if sha else "unknown"
    title = item["title"]
    files = finding.get("files") or []
    lines = [
        PLANNER.marker_start(key),
        f"<!-- sha: {sha} -->",
        f"## {title}",
        "",
        (
            f"Advisory refactor-scan {'task' if draft else 'finding'} "
            f"(`{finding.get('tier')}`, confidence {conf_cell(finding)}). "
            f"Scan id `{key}`."
        ),
    ]
    if files:
        lines.extend(["", "**Files:** " + ", ".join(f"`{p}`" for p in files)])
    lines.append(
        f"**Effort / risk:** {finding.get('effort')} / {finding.get('risk')}"
    )
    rationale = finding.get("confidence_rationale")
    if rationale:
        lines.append(f"**Independent pass:** {rationale}")
    deps = item.get("depends_on_keys") or []
    if deps:
        refs = ", ".join("{{ref:%s}}" % d for d in deps)
        lines.append(f"**Depends on:** {refs}")

    if draft:
        body = (draft.get("body") or "").strip()
        if body:
            lines.extend(["", body])
    else:
        if finding.get("detail"):
            lines.extend(["", finding["detail"]])
        if finding.get("tier") == "patch":
            if finding.get("snippet"):
                lines.extend(["", "Current:", "", fence(finding["snippet"])])
            if finding.get("suggestion"):
                lines.extend(["", "Suggestion:", "", fence(finding["suggestion"])])

    footer = f"_Commit `{short}`"
    if run_url:
        footer += f" · [run]({run_url})"
    footer += " · not a merge gate._"
    lines.extend(["", footer, PLANNER.marker_end])
    return "\n".join(lines) + "\n"


def plan(findings_obj: dict, existing: list, sha: str, run_url: str) -> dict:
    items = work_items(list(findings_obj.get("findings") or []))
    return PLANNER.plan(items, existing, lambda item: generated_block(item, sha, run_url))


def main(argv: list[str]) -> int:
    if len(argv) < 4 or len(argv) > 5:
        print(
            "usage: plan-refactor-issues.py FINDINGS.json EXISTING.json SHA [RUN_URL]",
            file=sys.stderr,
        )
        return 2
    findings = json.loads(Path(argv[1]).read_text())
    existing = json.loads(Path(argv[2]).read_text())
    sha = argv[3]
    run_url = argv[4] if len(argv) > 4 else ""
    json.dump(plan(findings, existing, sha, run_url), sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
