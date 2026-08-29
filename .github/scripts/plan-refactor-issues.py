#!/usr/bin/env python3
"""Turn merged refactor-scan findings into GitHub issue actions.

Merge only — no Claude. Held findings (confidence missing or >= 0.6) become
issues: one per patch finding, one per plan-tier proposed_issues item.

Match open issues by <!-- ducknet-refactor:KEY --> first, then case-insensitive
title. Closed issues (typically label refactor-scan) skip create. Updates
replace the generated block; text outside the markers is kept.

usage: plan-refactor-issues.py FINDINGS.json EXISTING.json SHA [RUN_URL]
EXISTING.json: [{number, title, body, state, labels?}, ...]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

CONFIDENCE_BAR = 0.6
MARKER_END = "<!-- /ducknet-refactor -->"
STICKY_MARKER = "<!-- ducknet-refactor-scan -->"


def marker_start(key: str) -> str:
    return f"<!-- ducknet-refactor:{key} -->"


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
        marker_start(key),
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
    lines.extend(["", footer, MARKER_END])
    return "\n".join(lines) + "\n"


def splice_generated(old_body: str, key: str, generated: str) -> str:
    start = marker_start(key)
    old = old_body or ""
    if start in old:
        before, rest = old.split(start, 1)
        if MARKER_END in rest:
            _, after = rest.split(MARKER_END, 1)
            return before + generated.rstrip("\n") + after
        return before + generated
    stripped = old.strip()
    if stripped:
        return generated + "\n---\n\n### Previous description\n\n" + stripped + "\n"
    return generated


def parse_keys(body: str) -> list:
    keys = []
    prefix = "<!-- ducknet-refactor:"
    text = body or ""
    start = 0
    while True:
        i = text.find(prefix, start)
        if i < 0:
            break
        j = text.find(" -->", i)
        if j < 0:
            break
        key = text[i + len(prefix) : j]
        start = j + 4
        if key.startswith("/"):
            continue
        if key not in keys:
            keys.append(key)
    return keys


def norm_title(title: str) -> str:
    return " ".join((title or "").casefold().split())


def index_issues(existing: list) -> tuple[dict, dict, dict, dict]:
    open_by_key, closed_by_key = {}, {}
    open_by_title, closed_by_title = {}, {}
    for issue in existing:
        if issue.get("pull_request"):
            continue
        if STICKY_MARKER in (issue.get("body") or ""):
            continue
        state = (issue.get("state") or "open").lower()
        body = issue.get("body") or ""
        title_key = norm_title(issue.get("title") or "")
        by_key = open_by_key if state == "open" else closed_by_key
        by_title = open_by_title if state == "open" else closed_by_title
        for key in parse_keys(body):
            by_key.setdefault(key, issue)
        if title_key:
            by_title.setdefault(title_key, issue)
    return open_by_key, open_by_title, closed_by_key, closed_by_title


def plan(findings_obj: dict, existing: list, sha: str, run_url: str) -> dict:
    items = work_items(list(findings_obj.get("findings") or []))
    open_by_key, open_by_title, closed_by_key, closed_by_title = index_issues(existing)
    actions = []
    for item in items:
        key = item["key"]
        title = item["title"]
        generated = generated_block(item, sha, run_url)
        matched = open_by_key.get(key)
        reason = "marker"
        if matched is None:
            matched = open_by_title.get(norm_title(title))
            reason = "title"
        if matched is not None:
            actions.append(
                {
                    "action": "update",
                    "key": key,
                    "number": matched["number"],
                    "title": title,
                    "body": splice_generated(matched.get("body") or "", key, generated),
                    "labels": item["labels"],
                    "reason": reason,
                }
            )
            continue
        closed = closed_by_key.get(key) or closed_by_title.get(norm_title(title))
        if closed is not None:
            actions.append(
                {
                    "action": "skip",
                    "key": key,
                    "number": closed["number"],
                    "title": title,
                    "reason": "closed",
                }
            )
            continue
        actions.append(
            {
                "action": "create",
                "key": key,
                "title": title,
                "body": generated,
                "labels": item["labels"],
            }
        )
    return {"actions": actions}


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
