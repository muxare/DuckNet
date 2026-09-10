#!/usr/bin/env python3
"""Marker-keyed GitHub issue planning, shared by every chain that files issues.

Merge only — no Claude. The rules are the ones `plan-refactor-issues.py`
established and this repo already trusts:

- an issue this machinery wrote carries `<!-- <namespace>:KEY -->` ... `<!-- /<namespace> -->`
- a re-run matches an open issue by that marker first, then by normalised title
- text outside the markers is a human's, and survives the rewrite
- a *closed* match means a human decided; skip it rather than reopening the argument

Callers supply the namespace and a `render(item) -> markdown` function, and get
back the create/update/skip plan: `plan-refactor-issues.py` (`ducknet-refactor`)
and `plan-backlog-issues.py` (`ducknet-backlog`). `test-refactor-scan.sh` and
`test-backlog.sh` between them cover every branch here.
"""
from __future__ import annotations


def norm_title(title: str) -> str:
    return " ".join((title or "").casefold().split())


class IssuePlanner:
    """Plans create/update/skip actions for one marker namespace."""

    def __init__(self, namespace: str, sticky_marker: str | None = None):
        self.namespace = namespace
        self.prefix = f"<!-- {namespace}:"
        self.marker_end = f"<!-- /{namespace} -->"
        self.sticky_marker = sticky_marker

    def marker_start(self, key: str) -> str:
        return f"<!-- {self.namespace}:{key} -->"

    def parse_keys(self, body: str) -> list[str]:
        keys: list[str] = []
        text = body or ""
        start = 0
        while True:
            i = text.find(self.prefix, start)
            if i < 0:
                break
            j = text.find(" -->", i)
            if j < 0:
                break
            key = text[i + len(self.prefix) : j]
            start = j + 4
            if key.startswith("/"):
                continue
            if key not in keys:
                keys.append(key)
        return keys

    def splice(self, old_body: str, key: str, generated: str) -> str:
        """Replace this key's generated block, keeping everything a human wrote."""
        start = self.marker_start(key)
        old = old_body or ""
        if start in old:
            before, rest = old.split(start, 1)
            if self.marker_end in rest:
                _, after = rest.split(self.marker_end, 1)
                return before + generated.rstrip("\n") + after
            return before + generated
        stripped = old.strip()
        if stripped:
            return generated + "\n---\n\n### Previous description\n\n" + stripped + "\n"
        return generated

    def index(self, existing: list[dict]):
        open_by_key: dict[str, dict] = {}
        closed_by_key: dict[str, dict] = {}
        open_by_title: dict[str, dict] = {}
        closed_by_title: dict[str, dict] = {}
        for issue in existing:
            if issue.get("pull_request"):
                continue
            body = issue.get("body") or ""
            if self.sticky_marker and self.sticky_marker in body:
                continue
            state = (issue.get("state") or "open").lower()
            by_key = open_by_key if state == "open" else closed_by_key
            by_title = open_by_title if state == "open" else closed_by_title
            for key in self.parse_keys(body):
                by_key.setdefault(key, issue)
            title_key = norm_title(issue.get("title") or "")
            if title_key:
                by_title.setdefault(title_key, issue)
        return open_by_key, open_by_title, closed_by_key, closed_by_title

    def plan(self, items: list[dict], existing: list[dict], render) -> dict:
        """items: [{key, title, labels, ...}]; render(item) -> generated markdown."""
        open_by_key, open_by_title, closed_by_key, closed_by_title = self.index(existing)
        actions = []
        for item in items:
            key = item["key"]
            title = item["title"]
            generated = render(item)

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
                        "body": self.splice(matched.get("body") or "", key, generated),
                        "labels": item.get("labels") or [],
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
                    "labels": item.get("labels") or [],
                }
            )
        return {"actions": actions}
