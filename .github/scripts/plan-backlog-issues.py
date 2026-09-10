#!/usr/bin/env python3
"""Turn a merged backlog-build result into GitHub issue actions.

Merge only — no Claude. An item is filed when the independent pass did not
knock it down:

  state `already-done` / `unfounded`  -> dropped, never filed
  confidence < 0.6                    -> dropped
  confidence missing                  -> filed (an unassessed item is not a
                                         disproved one; the body says so)

Epics are planned before their stories so `{{ref:KEY}}` in a story body can
resolve to the epic's number; the apply script does a second pass for the
references that only exist after everything is created. An epic whose stories
were all dropped is dropped with them.

usage: plan-backlog-issues.py FINAL.json EXISTING.json SHA [RUN_URL]
EXISTING.json: [{number, title, body, state, labels?}, ...]
Prints the action plan on stdout.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from issue_plan import IssuePlanner  # noqa: E402

CONFIDENCE_BAR = 0.6
NAMESPACE = "ducknet-backlog"
BASE_LABELS = ["backlog", "backlog-build"]
DEAD_STATES = {"already-done", "unfounded"}


def labels_for(item: dict) -> list[str]:
    out = list(BASE_LABELS)
    if item.get("kind") == "epic":
        out.append("epic")
    for label in item.get("labels") or []:
        if label and label not in out:
            out.append(label)
    return out


def drop_reason(item: dict) -> str | None:
    state = item.get("state")
    if state in DEAD_STATES:
        return f"independent pass found it {state}"
    confidence = item.get("confidence")
    if confidence is not None and float(confidence) < CONFIDENCE_BAR:
        return f"confidence {float(confidence):.2f} below {CONFIDENCE_BAR}"
    return None


def partition(items: list[dict]) -> tuple[list[dict], list[dict]]:
    kept, dropped = [], []
    for item in items:
        reason = drop_reason(item)
        if reason:
            dropped.append({"id": item.get("id"), "title": item.get("title"), "reason": reason})
        else:
            kept.append(item)

    # An epic is only a container. With no surviving story it is noise.
    kept_ids = {i["id"] for i in kept}
    parents_with_children = {i.get("parent") for i in kept if i.get("kind") != "epic"}
    survivors = []
    for item in kept:
        if item.get("kind") == "epic" and item["id"] not in parents_with_children:
            dropped.append(
                {
                    "id": item.get("id"),
                    "title": item.get("title"),
                    "reason": "no surviving stories",
                }
            )
            continue
        survivors.append(item)
    kept_ids = {i["id"] for i in survivors}

    for item in survivors:
        if item.get("parent") and item["parent"] not in kept_ids:
            item["parent"] = None
        item["depends_on"] = [d for d in (item.get("depends_on") or []) if d in kept_ids]

    # Epics first: their numbers must exist before a story body references them.
    survivors.sort(key=lambda i: (0 if i.get("kind") == "epic" else 1, i["id"]))
    return survivors, dropped


def conf_text(item: dict) -> str:
    confidence = item.get("confidence")
    if confidence is None:
        return "not independently assessed"
    return f"confidence {float(confidence):.2f}"


def render(item: dict, children: dict, sha: str, run_url: str, planner: IssuePlanner) -> str:
    key = item["id"]
    short = sha[:7] if sha else "unknown"
    lines = [
        planner.marker_start(key),
        f"<!-- sha: {sha} -->",
        f"## {item.get('title')}",
        "",
        (
            f"Backlog item (`{item.get('kind')}`, state `{item.get('state') or 'unassessed'}`, "
            f"{conf_text(item)}). Build id `{key}`."
        ),
        "",
    ]
    # Metadata as a list: consecutive bold lines would otherwise render as one
    # run-on paragraph.
    meta = [f"- **Size:** {item.get('size') or 'unsized'}"]
    if item.get("parent"):
        meta.append("- **Parent:** {{ref:%s}}" % item["parent"])
    deps = item.get("depends_on") or []
    if deps:
        meta.append("- **Depends on:** " + ", ".join("{{ref:%s}}" % d for d in deps))
    if item.get("state") == "partially-implemented":
        meta.append(
            "- **Note:** the independent pass found part of this already built — "
            "confirm the scope before starting."
        )
    if item.get("confidence_rationale"):
        meta.append(f"- **Independent pass:** {item['confidence_rationale']}")
    lines.extend(meta)

    body = (item.get("body") or "").strip()
    if body:
        lines.extend(["", body])

    criteria = item.get("acceptance_criteria") or []
    if criteria:
        lines.extend(["", "### Acceptance criteria", ""])
        lines.extend(f"- [ ] {c}" for c in criteria)

    kids = children.get(key) or []
    if kids:
        lines.extend(["", "### Stories", ""])
        lines.extend("- [ ] {{ref:%s}} — %s" % (k["id"], k.get("title")) for k in kids)

    evidence = item.get("evidence") or []
    if evidence:
        lines.extend(["", "### Evidence", ""])
        lines.extend(
            f"- `{e.get('source')}` — {e.get('detail')}" for e in evidence if e.get("source")
        )

    footer = f"_Commit `{short}`"
    if run_url:
        footer += f" · [run]({run_url})"
    footer += " · proposed by `backlog-build`; advisory, not a merge gate._"
    lines.extend(["", footer, planner.marker_end])
    return "\n".join(lines) + "\n"


def build_plan(final: dict, existing: list[dict], sha: str, run_url: str = "") -> dict:
    """The whole planner as one pure function of its inputs.

    Deterministic and model-free, which is what lets `reconcile-backlog-plan.py`
    re-derive a plan at apply time: replay it against the issue dump taken at
    build time and you must get the approved plan back, byte for byte.
    """
    kept, dropped = partition(list(final.get("items") or []))
    children: dict[str, list] = {}
    for item in kept:
        if item.get("parent"):
            children.setdefault(item["parent"], []).append(item)

    planner = IssuePlanner(NAMESPACE)
    work = [
        {"key": i["id"], "title": i.get("title") or i["id"], "labels": labels_for(i), "item": i}
        for i in kept
    ]
    plan = planner.plan(
        work, existing, lambda w: render(w["item"], children, sha, run_url, planner)
    )
    plan["dropped"] = dropped
    plan["summary"] = {
        "proposed": len(final.get("items") or []),
        "kept": len(kept),
        "dropped": len(dropped),
        "create": sum(1 for a in plan["actions"] if a["action"] == "create"),
        "update": sum(1 for a in plan["actions"] if a["action"] == "update"),
        "skip": sum(1 for a in plan["actions"] if a["action"] == "skip"),
    }
    return plan


def main(argv: list[str]) -> int:
    if len(argv) < 4 or len(argv) > 5:
        print("usage: plan-backlog-issues.py FINAL.json EXISTING.json SHA [RUN_URL]", file=sys.stderr)
        return 2

    final = json.loads(Path(argv[1]).read_text())
    existing = json.loads(Path(argv[2]).read_text())
    sha = argv[3]
    run_url = argv[4] if len(argv) > 4 else ""

    print(json.dumps(build_plan(final, existing, sha, run_url), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
