#!/usr/bin/env python3
"""Re-resolve an approved backlog plan against the backlog as it is *now*.

Merge only — no Claude.

A plan is built at one moment and applied at another, with a human approval in
between. Issue numbers, open/closed state, and issue bodies all move in that
gap, so the plan's `number` fields and its spliced bodies are stale by the time
anyone approves them. Applying the stale plan writes to issues that closed,
duplicates issues that appeared, and clobbers text a human added since.

What the approval actually fixes is the *content*: which items get filed, with
what titles and what generated bodies. That lives in `backlog-final.json`, the
frozen model output. Where each item lands is bookkeeping, and bookkeeping
should be done against current facts. So this re-runs the planner:

    approved  = plan(final, issues as they were at build time)   # replayed
    fresh     = plan(final, issues as they are now)              # applied

Step one is the safety check. `plan-backlog-issues.py` is a pure function, so
replaying it against the build-time issue dump *must* reproduce the approved
plan byte for byte. If it does not, something that was supposed to be frozen
moved — the model output, the planner's own code, the commit sha in the footer,
or the artifact itself — and this refuses to file anything. That one assertion
covers every drift vector at once, which is why there is no need to pin the
script version or re-checkout the build's commit.

Step two is applied. Its item set and titles are identical to the approved plan
by construction (same `final`, same pure function); only action kind, number,
and body may move, and every move is reported. A move is always toward the
safer or more correct outcome: an update to a since-closed issue becomes a skip,
a create that would now duplicate becomes an update, a body is re-spliced around
text a human wrote after the plan was made.

usage: reconcile-backlog-plan.py --approved P --final F --was E1 --now E2 --sha S
                                 [--run-url U] [--drift-out FILE]
Prints the fresh plan on stdout.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load_planner():
    """Import plan-backlog-issues.py — the hyphen keeps it off the import path."""
    spec = importlib.util.spec_from_file_location(
        "plan_backlog_issues", HERE / "plan-backlog-issues.py"
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def by_key(plan: dict) -> dict[str, dict]:
    return {a["key"]: a for a in plan.get("actions") or []}


def describe(old: dict, new: dict) -> str | None:
    """Why this item moved, in the words of what actually happened to it."""
    o, n = old.get("action"), new.get("action")
    on, nn = old.get("number"), new.get("number")

    if o == n:
        if o == "update" and on != nn:
            return f"was matched to #{on}, now matches #{nn}"
        if old.get("body") != new.get("body"):
            return "body re-merged around text edited since the plan was approved"
        return None
    if o == "update" and n == "skip":
        return f"#{on} was closed since the plan was approved — leaving it alone"
    if o == "update" and n == "create":
        return f"#{on} is gone; filing a fresh issue instead"
    if o == "create" and n == "update":
        return f"#{nn} appeared since the plan was approved — updating it, not duplicating it"
    if o == "create" and n == "skip":
        return f"closed issue #{nn} now matches — leaving it alone"
    if o == "skip" and n == "update":
        return f"#{on} was reopened since the plan was approved"
    if o == "skip" and n == "create":
        return f"closed issue #{on} is gone; filing a fresh issue instead"
    return f"{o} -> {n}"


def action_text(a: dict) -> str:
    kind = a.get("action")
    return f"{kind} #{a['number']}" if a.get("number") else kind


def render_drift(moves: list[tuple[str, dict, dict, str]], planned_at: str) -> str:
    lines = ["### Reconciled against the backlog as it is now", ""]
    when = f" (issue state read at {planned_at})" if planned_at else ""
    if not moves:
        lines += [
            f"Nothing moved. The backlog is as it was when this plan was built{when}, "
            "so the approved plan is applied exactly as shown.",
            "",
        ]
        return "\n".join(lines)

    lines += [
        f"The approved plan resolved issue numbers when it was built{when}. "
        "The backlog has moved since, so it was re-resolved just now. Same items, "
        "same titles, same generated text — only where they land has changed:",
        "",
        "| key | approved | applied | what happened |",
        "| --- | --- | --- | --- |",
    ]
    for key, old, new, why in moves:
        lines.append(
            f"| `{key}` | {action_text(old)} | {action_text(new)} "
            f"| {why.replace('|', chr(92) + '|')} |"
        )
    lines += [
        "",
        f"_{len(moves)} item(s) re-resolved. Every move is toward the safer outcome: "
        "issues that closed are left alone, issues that appeared are updated rather "
        "than duplicated, and text a human wrote after the plan was built is kept._",
        "",
    ]
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--approved", required=True, help="the plan a human approved")
    ap.add_argument("--final", required=True, help="frozen merged chain output")
    ap.add_argument("--was", required=True, help="issue dump taken at build time")
    ap.add_argument("--now", required=True, help="issue dump taken just now")
    ap.add_argument("--sha", required=True)
    ap.add_argument("--run-url", default="")
    ap.add_argument("--drift-out", help="write the drift report here as markdown")
    ap.add_argument("--planned-at", default="", help="when the build dumped issues")
    args = ap.parse_args(argv[1:])

    planner = load_planner()
    approved = json.loads(Path(args.approved).read_text())
    final = json.loads(Path(args.final).read_text())
    was = json.loads(Path(args.was).read_text())
    now = json.loads(Path(args.now).read_text())

    # 1. The frozen inputs must still produce the approved plan.
    replay = planner.build_plan(final, was, args.sha, args.run_url)
    if replay != approved:
        print(
            "reconcile: replaying the planner against the build-time issue dump did "
            "not reproduce the approved plan. Something that should have been frozen "
            "moved — the chain output, the planner's code, the commit sha, or the "
            "artifact. Refusing to file anything.",
            file=sys.stderr,
        )
        a_keys, r_keys = sorted(by_key(approved)), sorted(by_key(replay))
        if a_keys != r_keys:
            print(f"  approved keys: {a_keys}", file=sys.stderr)
            print(f"  replayed keys: {r_keys}", file=sys.stderr)
        else:
            for key in a_keys:
                if by_key(approved)[key] != by_key(replay)[key]:
                    print(f"  first key that differs: {key}", file=sys.stderr)
                    break
            if approved.get("dropped") != replay.get("dropped"):
                print("  the dropped list differs", file=sys.stderr)
        return 1

    # 2. Re-resolve against the backlog as it is now.
    fresh = planner.build_plan(final, now, args.sha, args.run_url)

    old_by_key, new_by_key = by_key(approved), by_key(fresh)
    if sorted(old_by_key) != sorted(new_by_key):
        # Cannot happen from a pure function on frozen input; if it does, the
        # assumption this whole script rests on is wrong. Do not file.
        print(
            "reconcile: the re-resolved plan covers different items than the "
            "approved one. Refusing to file anything.",
            file=sys.stderr,
        )
        return 1
    for key in old_by_key:
        if old_by_key[key].get("title") != new_by_key[key].get("title"):
            print(f"reconcile: title moved for {key}. Refusing to file anything.", file=sys.stderr)
            return 1

    moves = []
    for key in sorted(old_by_key):
        why = describe(old_by_key[key], new_by_key[key])
        if why:
            moves.append((key, old_by_key[key], new_by_key[key], why))

    if args.drift_out:
        Path(args.drift_out).write_text(render_drift(moves, args.planned_at))
    for key, old, new, why in moves:
        print(f"reconcile: {key}: {action_text(old)} -> {action_text(new)} ({why})", file=sys.stderr)
    print(f"reconcile: {len(moves)} of {len(old_by_key)} item(s) re-resolved", file=sys.stderr)

    print(json.dumps(fresh, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
