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
grep -q 'Draft issues (not created)' "$tmp/issue.md" || fail "format: drafts heading"
grep -q 'Extract a delegating EventBusDecorator base' "$tmp/issue.md" \
  || fail "format: draft title"
grep -q 'independent pass' "$tmp/issue.md" || fail "format: independent pass"
grep -q '_events = _events.Where' "$tmp/issue.md" || fail "format: snippet"
! grep -qi 'created issue' "$tmp/issue.md" || fail "format: must not claim issues created"
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

echo "All refactor-scan fixture tests passed."
