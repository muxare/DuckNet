#!/usr/bin/env bash
# Fixture tests for ReviewFlow merge + aggregate. No Claude required.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
ex="$root/.github/examples"
scripts="$root/.github/scripts"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }

# --- merge: Claude overlay ---
bash "$scripts/merge-triage-state.sh" \
  "$ex/pr.json" "$ex/files-git.json" "$ex/triage-claude.json" \
  > "$tmp/state.json"

jq -e '.pullRequest.number == 7' "$tmp/state.json" >/dev/null \
  || fail "merge overlay: PR number"
jq -e '.requestedReviewers | index("architecture") and index("security")' "$tmp/state.json" >/dev/null \
  || fail "merge overlay: reviewers"
jq -e '.files | length == 3' "$tmp/state.json" >/dev/null \
  || fail "merge overlay: file count"
jq -e '.findings == []' "$tmp/state.json" >/dev/null \
  || fail "merge overlay: findings empty"
jq -e '.skipped == false' "$tmp/state.json" >/dev/null \
  || fail "merge overlay: not skipped"
pass "merge overlay"

# --- merge: fallback when Claude is missing ---
bash "$scripts/merge-triage-state.sh" \
  "$ex/pr.json" "$ex/files-git.json" \
  > "$tmp/fallback.json"

jq -e '.risk.reasons[0] | test("fallback")' "$tmp/fallback.json" >/dev/null \
  || fail "merge fallback: reason"
jq -e '.requestedReviewers | index("architecture")' "$tmp/fallback.json" >/dev/null \
  || fail "merge fallback: architecture from src/"
jq -e '[.files[] | select(.path=="README.md") | .areas[]] | index("other")' "$tmp/fallback.json" >/dev/null \
  || fail "merge fallback: README other"
pass "merge fallback"

# --- aggregate: both specialists, major finding → request_changes ---
bash "$scripts/aggregate-review.sh" \
  --state "$tmp/state.json" \
  --out-state "$tmp/agg-state.json" \
  --out-comment "$tmp/comment.md" \
  --sha abc1234deadbeef \
  --findings "$ex/findings-architecture.json" \
  --findings "$ex/findings-security.json"

jq -e '.findings | length == 1' "$tmp/agg-state.json" >/dev/null \
  || fail "aggregate: one finding"
jq -e '.findings[0].reviewer == "architecture"' "$tmp/agg-state.json" >/dev/null \
  || fail "aggregate: reviewer tag"
grep -q 'request changes' "$tmp/comment.md" || fail "aggregate: verdict line"
grep -q 'Advisory only' "$tmp/comment.md" || fail "aggregate: advisory footer"
grep -q 'architecture` ran' "$tmp/comment.md" || fail "aggregate: architecture ran"
grep -q 'security` ran' "$tmp/comment.md" || fail "aggregate: security ran"
grep -q 'mixed-version replay' "$tmp/comment.md" || fail "aggregate: notes"
pass "aggregate both specialists"

# --- aggregate: skipped triage → approve, no specialists ---
jq '.skipped = true | .requestedReviewers = [] | .risk.level = "low"' \
  "$tmp/state.json" > "$tmp/skipped.json"

bash "$scripts/aggregate-review.sh" \
  --state "$tmp/skipped.json" \
  --out-state "$tmp/skip-state.json" \
  --out-comment "$tmp/skip.md" \
  --sha abc1234

grep -q 'approve' "$tmp/skip.md" || fail "skipped: approve"
grep -q 'No specialist review' "$tmp/skip.md" || fail "skipped: copy"
pass "aggregate skipped"

# --- aggregate: degraded specialist ---
bash "$scripts/aggregate-review.sh" \
  --state "$tmp/state.json" \
  --out-state "$tmp/deg-state.json" \
  --out-comment "$tmp/deg.md" \
  --sha abc1234 \
  --findings "$ex/findings-architecture-degraded.json"

grep -q 'Degraded specialists' "$tmp/deg.md" || fail "degraded: heading"
grep -q 'error_max_budget_usd' "$tmp/deg.md" || fail "degraded: error"
grep -q 'approve' "$tmp/deg.md" || fail "degraded: no findings means approve"
pass "aggregate degraded"

# --- extract-findings: envelope vs degraded ---
jq -n '{structured_output:{reviewer:"security",findings:[],notes:["ok"]}}' \
  > "$tmp/envelope.json"
bash "$scripts/extract-findings.sh" security "$tmp/envelope.json" "$tmp/extracted.json"
jq -e '.reviewer == "security" and .notes[0] == "ok"' "$tmp/extracted.json" >/dev/null \
  || fail "extract: structured"
: > "$tmp/empty-raw.json"
bash "$scripts/extract-findings.sh" architecture "$tmp/empty-raw.json" "$tmp/empty.json"
jq -e '.error == "no_structured_output" and .findings == []' "$tmp/empty.json" >/dev/null \
  || fail "extract: degraded"
pass "extract-findings"

# --- numstat-to-json ---
printf '12\t4\tsrc/Foo.cs\n-\t-\tlogo.png\n' \
  | bash "$scripts/numstat-to-json.sh" > "$tmp/numstat.json"
jq -e '.[0].path == "src/Foo.cs" and .[0].added == 12' "$tmp/numstat.json" >/dev/null \
  || fail "numstat: text file"
jq -e '.[1].path == "logo.png" and .[1].added == 0' "$tmp/numstat.json" >/dev/null \
  || fail "numstat: binary"
pass "numstat-to-json"

echo "All ReviewFlow fixture tests passed."
