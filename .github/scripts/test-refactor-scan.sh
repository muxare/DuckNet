#!/usr/bin/env bash
# Fixture tests for refactor-scan merge + issue markdown. No Claude required.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
ex="$root/.github/examples"
scripts="$root/.github/scripts"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }

# --- merge: join confidence by id ---
bash "$scripts/merge-refactor-confidence.sh" \
  "$ex/findings-refactor.json" "$ex/verdicts-refactor.json" \
  > "$tmp/final.json"

jq -e '.findings | length == 2' "$tmp/final.json" >/dev/null \
  || fail "merge: finding count"
jq -e '.findings[] | select(.id=="alarmcenter-ratewindow-linq-allocation") | .confidence == 0.85' \
  "$tmp/final.json" >/dev/null || fail "merge: patch confidence"
jq -e '.findings[] | select(.id=="eventbus-hostile-wrappers-decorator") | .confidence == 0.6' \
  "$tmp/final.json" >/dev/null || fail "merge: plan confidence"
jq -e '.findings[] | select(.id=="alarmcenter-ratewindow-linq-allocation") | .confidence_rationale | test("verbatim")' \
  "$tmp/final.json" >/dev/null || fail "merge: rationale"
jq -e '.findings[] | select(.id=="alarmcenter-ratewindow-linq-allocation") | .detail' \
  "$tmp/final.json" >/dev/null || fail "merge: scanner detail kept"
pass "merge confidence join"

# --- merge: missing assessment → null + note ---
jq '{assessments: [.assessments[0]], notes: ["verify truncated"]}' \
  "$ex/verdicts-refactor.json" > "$tmp/partial-verdicts.json"

bash "$scripts/merge-refactor-confidence.sh" \
  "$ex/findings-refactor.json" "$tmp/partial-verdicts.json" \
  > "$tmp/partial.json"

jq -e '.findings[] | select(.id=="eventbus-hostile-wrappers-decorator") | .confidence == null' \
  "$tmp/partial.json" >/dev/null || fail "partial: null confidence"
jq -e '.notes | map(test("no independent confidence")) | any' \
  "$tmp/partial.json" >/dev/null || fail "partial: missing-assessment note"
jq -e '.notes | index("verify truncated")' "$tmp/partial.json" >/dev/null \
  || fail "partial: verifier notes kept"
pass "merge missing assessment"

# --- format: held findings + drafts ---
bash "$scripts/format-refactor-scan.sh" \
  "$tmp/final.json" abc1234deadbeef "https://example.test/run/1" \
  > "$tmp/issue.md"

grep -q 'Avoid re-materializing' "$tmp/issue.md" || fail "format: patch title"
grep -q 'Unify hostile-transport' "$tmp/issue.md" || fail "format: plan title"
grep -q '0.85' "$tmp/issue.md" || fail "format: confidence"
grep -q 'abc1234' "$tmp/issue.md" || fail "format: sha short"
grep -q 'https://example.test/run/1' "$tmp/issue.md" || fail "format: run url"
grep -q 'Advisory only' "$tmp/issue.md" || fail "format: advisory"
grep -q 'CI creates or updates' "$tmp/issue.md" || fail "format: issue sync"
grep -q 'Tasks (CI creates or updates issues)' "$tmp/issue.md" || fail "format: tasks heading"
grep -q 'Extract a delegating EventBusDecorator base' "$tmp/issue.md" \
  || fail "format: draft title"
grep -q 'independent pass' "$tmp/issue.md" || fail "format: independent pass"
grep -q '_events = _events.Where' "$tmp/issue.md" || fail "format: snippet"
! grep -q 'Draft issues (not created)' "$tmp/issue.md" || fail "format: old drafts copy"
pass "format held findings"

# --- format: low confidence goes to disagreed section ---
jq '.findings[0].confidence = 0.2' "$tmp/final.json" > "$tmp/low.json"
bash "$scripts/format-refactor-scan.sh" "$tmp/low.json" deadbeef > "$tmp/low.md"
grep -q 'Independent pass disagreed' "$tmp/low.md" || fail "low: section"
grep -q 'alarmcenter-ratewindow-linq-allocation' "$tmp/low.md" || fail "low: id"
pass "format low confidence"

# --- format: empty findings ---
printf '%s\n' '{"summary":"Tree is clean.","findings":[],"notes":["near miss"]}' \
  > "$tmp/empty.json"
bash "$scripts/format-refactor-scan.sh" "$tmp/empty.json" cafe123 > "$tmp/empty.md"
grep -q 'Tree is clean' "$tmp/empty.md" || fail "empty: summary"
grep -q 'No refactoring opportunities' "$tmp/empty.md" || fail "empty: empty copy"
grep -q 'near miss' "$tmp/empty.md" || fail "empty: notes"
grep -q 'cafe123' "$tmp/empty.md" || fail "empty: sha"
pass "format empty"

plan() {
  python3 "$scripts/plan-refactor-issues.py" "$@"
}

# --- plan: empty tracker → one patch + two proposed_issues ---
printf '%s\n' '[]' > "$tmp/none.json"
plan "$tmp/final.json" "$tmp/none.json" abc1234deadbeef "https://example.test/run/1" \
  > "$tmp/actions.json"

jq -e '.actions | length == 3' "$tmp/actions.json" >/dev/null || fail "plan: action count"
jq -e '[.actions[].action] | unique == ["create"]' "$tmp/actions.json" >/dev/null \
  || fail "plan: all creates"
jq -e '.actions[0].key == "alarmcenter-ratewindow-linq-allocation"' "$tmp/actions.json" \
  >/dev/null || fail "plan: patch key"
jq -e '.actions[1].key == "eventbus-hostile-wrappers-decorator:0"' "$tmp/actions.json" \
  >/dev/null || fail "plan: proposed 0"
jq -e '.actions[2].key == "eventbus-hostile-wrappers-decorator:1"' "$tmp/actions.json" \
  >/dev/null || fail "plan: proposed 1"
jq -e '.actions[2].body | test("\\{\\{ref:eventbus-hostile-wrappers-decorator:0\\}\\}")' \
  "$tmp/actions.json" >/dev/null || fail "plan: depends placeholder"
jq -e '.actions[0].body | test("ducknet-refactor:alarmcenter-ratewindow-linq-allocation")' \
  "$tmp/actions.json" >/dev/null || fail "plan: marker"
jq -e '.actions[0].labels | index("refactor-scan") and index("refactoring")' \
  "$tmp/actions.json" >/dev/null || fail "plan: labels"
pass "plan creates from held findings"

# --- plan: marker match updates; title match preserves prior body ---
cat > "$tmp/open.json" <<'JSON'
[
  {
    "number": 11,
    "title": "old patch title",
    "body": "<!-- ducknet-refactor:alarmcenter-ratewindow-linq-allocation -->\nold generated\n<!-- /ducknet-refactor -->\n\nKeep my note.\n",
    "state": "open",
    "labels": ["refactoring"]
  },
  {
    "number": 12,
    "title": "Extract a delegating EventBusDecorator base",
    "body": "I already filed this by hand.\n",
    "state": "open",
    "labels": []
  }
]
JSON
plan "$tmp/final.json" "$tmp/open.json" abc1234deadbeef > "$tmp/matched.json"

jq -e '.actions[0] | .action=="update" and .number==11 and .reason=="marker"' \
  "$tmp/matched.json" >/dev/null || fail "plan: marker update"
jq -e '.actions[0].body | test("Keep my note")' "$tmp/matched.json" >/dev/null \
  || fail "plan: suffix preserved"
jq -e '.actions[1] | .action=="update" and .number==12 and .reason=="title"' \
  "$tmp/matched.json" >/dev/null || fail "plan: title update"
jq -e '.actions[1].body | test("Previous description") and test("already filed")' \
  "$tmp/matched.json" >/dev/null || fail "plan: prior body kept"
jq -e '.actions[2].action == "create"' "$tmp/matched.json" >/dev/null \
  || fail "plan: unmatched proposed still created"
pass "plan update by marker and title"

# --- plan: closed skip; low confidence omitted ---
cat > "$tmp/closed.json" <<'JSON'
[
  {
    "number": 9,
    "title": "Avoid re-materializing the rate window list on every squeak",
    "body": "<!-- ducknet-refactor:alarmcenter-ratewindow-linq-allocation -->\ndone\n<!-- /ducknet-refactor -->\n",
    "state": "closed",
    "labels": ["refactor-scan"]
  }
]
JSON
plan "$tmp/final.json" "$tmp/closed.json" deadbeef > "$tmp/skip.json"
jq -e '.actions[0] | .action=="skip" and .number==9 and .reason=="closed"' \
  "$tmp/skip.json" >/dev/null || fail "plan: closed skip"
jq -e '[.actions[] | select(.action=="create")] | length == 2' "$tmp/skip.json" \
  >/dev/null || fail "plan: remaining creates"

jq '.findings[0].confidence = 0.2 | .findings[1].confidence = 0.2' \
  "$tmp/final.json" > "$tmp/weak.json"
plan "$tmp/weak.json" "$tmp/none.json" deadbeef > "$tmp/weak-actions.json"
jq -e '.actions | length == 0' "$tmp/weak-actions.json" >/dev/null \
  || fail "plan: weak findings create nothing"
pass "plan skip closed and weak"

echo "All refactor-scan fixture tests passed."
