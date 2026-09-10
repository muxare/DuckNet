#!/usr/bin/env bash
# Fixture tests for the backlog chains: validator, schemas, merge, issue plan,
# markdown. No Claude required, no network, nothing written outside a tempdir.
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
ex="$root/.github/examples"
scripts="$root/.github/scripts"
schemas="$root/.github/schemas"
chains="$root/.github/chains"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }
validate() { python3 "$scripts/validate-json.py" "$1" "$2" --quiet; }

# --- validator: accepts what it should, rejects what it should ---
printf '{"type":"object","required":["a"],"properties":{"a":{"type":"integer","minimum":2}},"additionalProperties":false}\n' > "$tmp/s.json"
printf '{"a":3}\n' > "$tmp/good.json"
printf '{"a":1,"b":2}\n' > "$tmp/bad.json"
validate "$tmp/s.json" "$tmp/good.json" || fail "validator: rejected a valid document"
if validate "$tmp/s.json" "$tmp/bad.json" 2>/dev/null; then
  fail "validator: accepted a document violating minimum and additionalProperties"
fi
pass "validator accept/reject"

# --- every schema is parseable, every chain manifest is well-formed ---
for schema in "$schemas"/*.schema.json; do
  python3 -c "import json,sys; json.load(open(sys.argv[1]))" "$schema" \
    || fail "schema does not parse: $schema"
done
pass "schemas parse"

for chain in "$chains"/*.json; do
  validate "$schemas/chain.schema.json" "$chain" || fail "chain manifest invalid: $chain"
done
pass "chain manifests valid"

# --- fixtures satisfy the schemas the model is held to ---
validate "$schemas/backlog-items.schema.json" "$ex/backlog-items.json" || fail "items fixture"
validate "$schemas/backlog-verdicts.schema.json" "$ex/backlog-verdicts.json" || fail "verdicts fixture"
validate "$schemas/backlog-groom.schema.json" "$ex/backlog-groom.json" || fail "groom fixture"
validate "$schemas/backlog-groom-verdicts.schema.json" "$ex/backlog-groom-verdicts.json" \
  || fail "groom verdicts fixture"
pass "fixtures satisfy their schemas"

# --- run-claude.sh must not hand the CLI a "$schema" declaration ---
# The CLI's --json-schema validator has no meta-schemas registered and does not
# fetch them, so a schema carrying "$schema" is rejected before the model runs:
#   --json-schema is not a valid JSON Schema: no schema with key or ref "..."
# The files on disk keep the declaration; run-claude.sh strips it on the way out.
mkdir -p "$tmp/stub"
cat > "$tmp/stub/claude" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$STUB_ARGS"
STUB
chmod +x "$tmp/stub/claude"
echo 'prompt' > "$tmp/stub-input.txt"
STUB_ARGS="$tmp/stub-args.txt" PATH="$tmp/stub:$PATH" CLAUDE_CODE_OAUTH_TOKEN=stub \
  bash "$scripts/run-claude.sh" \
    --schema "$schemas/backlog-items.schema.json" \
    --model stub-model --budget 0.01 \
    --input "$tmp/stub-input.txt" --output "$tmp/stub-out.json" >/dev/null 2>&1 \
  || fail "run-claude.sh did not run against the stub CLI"
grep -q -- '--json-schema' "$tmp/stub-args.txt" || fail "run-claude.sh passed no --json-schema"
if grep -q '"\$schema"' "$tmp/stub-args.txt"; then
  fail 'run-claude.sh passed "$schema" to the CLI; it must be stripped'
fi
python3 - "$tmp/stub-args.txt" <<'CHECK' || fail "run-claude.sh passed a schema that does not parse"
import json, sys
args = open(sys.argv[1]).read().split("\n")
json.loads(args[args.index("--json-schema") + 1])
CHECK
pass "run-claude.sh strips \$schema before the CLI sees it"

# --- merge: join assessments into items by id ---
bash "$scripts/merge-confidence.sh" "$ex/backlog-items.json" "$ex/backlog-verdicts.json" items id \
  > "$tmp/final.json"
validate "$schemas/backlog-final.schema.json" "$tmp/final.json" || fail "merge: final schema"
jq -e '.items | length == 5' "$tmp/final.json" >/dev/null || fail "merge: item count"
jq -e '.items[] | select(.id=="azure-first-environment") | .confidence == 0.9' "$tmp/final.json" \
  >/dev/null || fail "merge: confidence joined"
jq -e '.items[] | select(.id=="dashboard-dlq-replay-ui") | .state == "already-done"' "$tmp/final.json" \
  >/dev/null || fail "merge: assessor state copied onto the item"
jq -e '.items[] | select(.id=="azure-first-environment") | .body' "$tmp/final.json" >/dev/null \
  || fail "merge: producer body kept"
pass "merge joins items"

# --- merge: an unassessed item is null, not silently endorsed ---
jq '{assessments: [.assessments[0]], notes: ["verify truncated"]}' "$ex/backlog-verdicts.json" \
  > "$tmp/partial-verdicts.json"
bash "$scripts/merge-confidence.sh" "$ex/backlog-items.json" "$tmp/partial-verdicts.json" items id \
  > "$tmp/partial.json"
jq -e '.items[] | select(.id=="contracts-squeaked-v3") | .confidence == null' "$tmp/partial.json" \
  >/dev/null || fail "partial: null confidence"
jq -e '.notes | map(test("no independent assessment")) | any' "$tmp/partial.json" >/dev/null \
  || fail "partial: missing-assessment note"
jq -e '.notes | index("verify truncated")' "$tmp/partial.json" >/dev/null \
  || fail "partial: assessor notes kept"
pass "merge flags unassessed items"

# --- merge: the same primitive serves the groom chain ---
bash "$scripts/merge-confidence.sh" "$ex/backlog-groom.json" "$ex/backlog-groom-verdicts.json" \
  findings id > "$tmp/groom-final.json"
validate "$schemas/backlog-groom-final.schema.json" "$tmp/groom-final.json" || fail "groom merge schema"
jq -e '.findings[] | select(.id=="stale-38-duck-progress") | .verdict == "does-not-hold"' \
  "$tmp/groom-final.json" >/dev/null || fail "groom merge: verdict joined"
pass "merge joins groom findings"

# --- plan: create, update, skip, drop ---
python3 "$scripts/plan-backlog-issues.py" \
  "$tmp/final.json" "$ex/backlog-existing-issues.json" abc1234deadbeef https://example.test/run/1 \
  > "$tmp/actions.json"
validate "$schemas/backlog-actions.schema.json" "$tmp/actions.json" || fail "plan: actions schema"
jq -e '.summary.create == 1 and .summary.update == 1 and .summary.skip == 1' "$tmp/actions.json" \
  >/dev/null || fail "plan: action counts"
jq -e '.dropped | length == 2' "$tmp/actions.json" >/dev/null || fail "plan: dropped count"
jq -e '.dropped[] | select(.id=="dashboard-dlq-replay-ui") | .reason | test("already-done")' \
  "$tmp/actions.json" >/dev/null || fail "plan: already-done is never filed"
jq -e '.dropped[] | select(.id=="contracts-squeaked-v3") | .reason | test("0.35")' \
  "$tmp/actions.json" >/dev/null || fail "plan: below-bar confidence is dropped"
pass "plan create/update/skip/drop"

# --- plan: a human's words outside the markers survive an update ---
jq -er '.actions[] | select(.action=="update") | .body' "$tmp/actions.json" > "$tmp/updated.md"
grep -q "Notes from standup" "$tmp/updated.md" || fail "plan: human text lost on update"
grep -q "Wait for the subscription owner" "$tmp/updated.md" || fail "plan: human text truncated"
! grep -q "Old generated text" "$tmp/updated.md" || fail "plan: stale generated block kept"
pass "plan preserves human text"

# --- plan: an epic lists its stories, a story points at its epic ---
jq -er '.actions[] | select(.key=="azure-first-environment") | .body' "$tmp/actions.json" \
  > "$tmp/epic.md"
grep -q '{{ref:azure-bootstrap-identity}}' "$tmp/epic.md" || fail "plan: epic missing child ref"
grep -q '### Stories' "$tmp/epic.md" || fail "plan: epic missing story list"
jq -er '.actions[] | select(.key=="azure-bootstrap-identity") | .body' "$tmp/actions.json" \
  | grep -q '{{ref:azure-first-environment}}' || fail "plan: story missing parent ref"
pass "plan links epic and stories"

# --- plan: an epic with no surviving story is dropped with them ---
jq '{summary, sources, notes, items: [.items[] | if .kind == "story" and .parent
      then .confidence = 0.1 else . end]}' "$tmp/final.json" > "$tmp/orphaned.json"
printf '[]\n' > "$tmp/no-issues.json"
python3 "$scripts/plan-backlog-issues.py" "$tmp/orphaned.json" "$tmp/no-issues.json" abc1234 \
  > "$tmp/orphan-actions.json"
jq -e '.dropped[] | select(.id=="azure-first-environment") | .reason | test("no surviving stories")' \
  "$tmp/orphan-actions.json" >/dev/null || fail "plan: empty epic still filed"
pass "plan drops an epic with no stories"

# --- markdown ---
bash "$scripts/format-backlog-build.sh" "$tmp/final.json" abc1234deadbeef https://example.test/run/1 \
  > "$tmp/build.md"
grep -q "Backlog build" "$tmp/build.md" || fail "build markdown: heading"
grep -q "Not filed" "$tmp/build.md" || fail "build markdown: dropped section"
grep -q "azure-first-environment" "$tmp/build.md" || fail "build markdown: item table"
pass "build markdown"

# --- reconcile: the approved plan is re-resolved against the backlog as it is now ---
# The plan pins issue numbers at build time; approval happens later. These cover
# what moves in that gap.
reconcile() {
  python3 "$scripts/reconcile-backlog-plan.py" \
    --approved "$tmp/actions.json" --final "$tmp/final.json" \
    --was "$ex/backlog-existing-issues.json" --now "$1" \
    --sha abc1234deadbeef --run-url https://example.test/run/1 "${@:2}"
}

# nothing changed: the fresh plan is the approved plan, and the replay proves it
reconcile "$ex/backlog-existing-issues.json" --drift-out "$tmp/drift-none.md" \
  > "$tmp/fresh-none.json" 2>/dev/null || fail "reconcile: rejected an unchanged backlog"
jq -es '.[0] == .[1]' "$tmp/actions.json" "$tmp/fresh-none.json" >/dev/null \
  || fail "reconcile: an unchanged backlog produced a different plan"
grep -q "Nothing moved" "$tmp/drift-none.md" || fail "reconcile: no-drift report"
pass "reconcile reproduces the approved plan when nothing moved"

# frozen input moved: refuse to file anything at all
jq '.items[0].title = "Something the approver never saw"' "$tmp/final.json" \
  > "$tmp/tampered-final.json"
if python3 "$scripts/reconcile-backlog-plan.py" \
     --approved "$tmp/actions.json" --final "$tmp/tampered-final.json" \
     --was "$ex/backlog-existing-issues.json" --now "$ex/backlog-existing-issues.json" \
     --sha abc1234deadbeef --run-url https://example.test/run/1 >/dev/null 2>&1; then
  fail "reconcile: filed a plan whose frozen model output had changed"
fi
pass "reconcile refuses when the approved content moved"

# the planner's own output moving counts as frozen input moving: a different
# sha reaches the issue bodies, so the replay must not reproduce the approval
if python3 "$scripts/reconcile-backlog-plan.py" \
     --approved "$tmp/actions.json" --final "$tmp/final.json" \
     --was "$ex/backlog-existing-issues.json" --now "$ex/backlog-existing-issues.json" \
     --sha 9999999deadbeef --run-url https://example.test/run/1 >/dev/null 2>&1; then
  fail "reconcile: filed a plan built from a different commit"
fi
pass "reconcile refuses when the commit behind the plan moved"

# an issue closed between plan and approval: update becomes skip, never a write
jq '[ .[] | if .number == 41 then .state = "closed" else . end ]' \
  "$ex/backlog-existing-issues.json" > "$tmp/now-closed.json"
reconcile "$tmp/now-closed.json" --drift-out "$tmp/drift-closed.md" \
  > "$tmp/fresh-closed.json" 2>/dev/null || fail "reconcile: closed-issue case"
jq -e '.actions[] | select(.key=="azure-bootstrap-identity") | .action == "skip"' \
  "$tmp/fresh-closed.json" >/dev/null \
  || fail "reconcile: still updates an issue closed since the plan was approved"
grep -q "closed since the plan was approved" "$tmp/drift-closed.md" \
  || fail "reconcile: closed-issue move not reported"
pass "reconcile turns an update into a skip when the issue was closed"

# a matching issue appeared between plan and approval: create becomes update
jq --arg t "Stand up the first live Azure environment" \
   '. + [{number: 77, title: $t, body: "Filed by a human on Tuesday.", state: "open", labels: []}]' \
   "$ex/backlog-existing-issues.json" > "$tmp/now-new.json"
reconcile "$tmp/now-new.json" --drift-out "$tmp/drift-new.md" \
  > "$tmp/fresh-new.json" 2>/dev/null || fail "reconcile: new-issue case"
jq -e '.actions[] | select(.key=="azure-first-environment")
       | .action == "update" and .number == 77' "$tmp/fresh-new.json" >/dev/null \
  || fail "reconcile: duplicated an issue that appeared since the plan was approved"
jq -er '.actions[] | select(.key=="azure-first-environment") | .body' "$tmp/fresh-new.json" \
  | grep -q "Filed by a human on Tuesday" \
  || fail "reconcile: clobbered the body of the issue it adopted"
grep -q "not duplicating it" "$tmp/drift-new.md" || fail "reconcile: new-issue move not reported"
pass "reconcile turns a create into an update rather than duplicating"

# a human edited a matched issue between plan and approval: their text survives
jq '[ .[] | if .number == 41 then .body = (.body + "\n\nAdded after the plan was built.\n")
      else . end ]' "$ex/backlog-existing-issues.json" > "$tmp/now-edited.json"
reconcile "$tmp/now-edited.json" --drift-out "$tmp/drift-edited.md" \
  > "$tmp/fresh-edited.json" 2>/dev/null || fail "reconcile: edited-issue case"
jq -er '.actions[] | select(.key=="azure-bootstrap-identity") | .body' "$tmp/fresh-edited.json" \
  | grep -q "Added after the plan was built" \
  || fail "reconcile: clobbered text a human added after the plan was approved"
jq -er '.actions[] | select(.key=="azure-bootstrap-identity") | .body' "$tmp/actions.json" \
  | grep -q "Added after the plan was built" \
  && fail "reconcile fixture: the approved plan already had the later edit"
grep -q "body re-merged" "$tmp/drift-edited.md" || fail "reconcile: body re-merge not reported"
pass "reconcile keeps text a human wrote after the plan was approved"

# --- action-plan markdown: what the approver reads before releasing apply ---
bash "$scripts/format-backlog-actions.sh" "$tmp/actions.json" https://example.test/run/1 \
  > "$tmp/plan.md"
grep -q "Backlog plan" "$tmp/plan.md" || fail "plan markdown: heading"
grep -q "### Create" "$tmp/plan.md" || fail "plan markdown: create section"
grep -q "### Update" "$tmp/plan.md" || fail "plan markdown: update section"
grep -q "### Dropped" "$tmp/plan.md" || fail "plan markdown: dropped section"
# Every title in the plan must be visible: an approval against a partial
# rendering is an approval against something the approver did not see.
while IFS= read -r title; do
  grep -qF "$title" "$tmp/plan.md" || fail "plan markdown: title not shown — $title"
done < <(jq -r '.actions[].title' "$tmp/actions.json")
grep -q "until the \`apply\` job is approved" "$tmp/plan.md" || fail "plan markdown: gate not stated"
pass "action-plan markdown"

# --- an empty plan renders rather than producing a blank approval page ---
printf '{"actions":[],"dropped":[],"summary":{"proposed":0,"kept":0,"dropped":0,"create":0,"update":0,"skip":0}}\n' \
  > "$tmp/empty-actions.json"
validate "$schemas/backlog-actions.schema.json" "$tmp/empty-actions.json" || fail "empty plan schema"
bash "$scripts/format-backlog-actions.sh" "$tmp/empty-actions.json" > "$tmp/empty-plan.md"
grep -q "Nothing to file" "$tmp/empty-plan.md" || fail "plan markdown: empty plan says nothing"
pass "action-plan markdown handles an empty plan"

bash "$scripts/format-backlog-groom.sh" "$tmp/groom-final.json" abc1234deadbeef \
  https://example.test/run/2 > "$tmp/groom.md"
grep -q '<!-- ducknet-backlog-groom -->' "$tmp/groom.md" \
  || fail "groom markdown: sticky marker missing — apply-groom-report.js refuses without it"
grep -q "Belongs under a common parent" "$tmp/groom.md" || fail "groom markdown: grouping section"
grep -q "## Dismissed" "$tmp/groom.md" || fail "groom markdown: dismissed section"
grep -q "Step 12c has no issue" "$tmp/groom.md" || fail "groom markdown: gap rendered"
grep -q -- "— — " "$tmp/groom.md" && fail "groom markdown: empty issue list rendered as a dash"
pass "groom markdown"

# --- readiness markdown ---
cat > "$tmp/readiness.json" <<'JSON'
{
  "issue": 12,
  "verdict": "needs-work",
  "summary": "Names the outcome but nothing checkable.",
  "missing": ["acceptance-criteria"],
  "strengths": ["The title says what changes"],
  "questions": ["Which Center owns the new table?"],
  "suggested_acceptance_criteria": ["dotnet test is green"]
}
JSON
validate "$schemas/backlog-readiness.schema.json" "$tmp/readiness.json" || fail "readiness fixture"
bash "$scripts/format-readiness.sh" "$tmp/readiness.json" https://example.test/run/3 \
  > "$tmp/readiness.md"
grep -q '<!-- ducknet-readiness -->' "$tmp/readiness.md" \
  || fail "readiness markdown: sticky marker missing"
grep -q "Which Center owns the new table" "$tmp/readiness.md" || fail "readiness markdown: questions"
pass "readiness markdown"

# --- slice markdown ---
cat > "$tmp/slice.json" <<'JSON'
{
  "issue": 36,
  "summary": "Cut into two slices so main keeps running between them.",
  "slices": [
    {
      "order": 1,
      "title": "Introduce EventConsumerBase alongside the existing consumers",
      "body": "Add the base type. No consumer moves yet.",
      "acceptance_criteria": ["dotnet test is green", "No Center references the new type yet"],
      "touches": ["src/DuckNet.Kernel/Consumer"],
      "size": "small",
      "demo": "dotnet run --project src/DuckNet.Kernel -- --seconds 5 still counts squeaks"
    }
  ],
  "notes": []
}
JSON
validate "$schemas/backlog-slices.schema.json" "$tmp/slice.json" || fail "slice fixture"
bash "$scripts/format-backlog-slice.sh" "$tmp/slice.json" > "$tmp/slice.md"
grep -q "Slices of #36" "$tmp/slice.md" || fail "slice markdown: heading"
grep -q "still runs afterwards" "$tmp/slice.md" || fail "slice markdown: demo line"
pass "slice markdown"

# --- chain runner: dry run assembles every stage input, spends nothing ---
BACKLOG_ISSUES_FILE="$ex/backlog-existing-issues.json" \
  python3 "$scripts/run-chain.py" "$chains/backlog-groom.json" "$tmp/chain" --dry-run >/dev/null
[[ -f "$tmp/chain/groom-input.md" ]] || fail "dry run: groom input not assembled"
[[ -f "$tmp/chain/verify-input.md" ]] || fail "dry run: verify input not assembled"
[[ -f "$tmp/chain/groom-raw.json" ]] && fail "dry run: called the model"
validate "$schemas/chain-meta.schema.json" "$tmp/chain/chain-meta.json" || fail "dry run: chain meta"
grep -q "Open backlog" "$tmp/chain/groom-input.md" || fail "dry run: context heading missing"
pass "chain dry run"

# --- chain runner: the verify stage never sees the groomer's argument ---
grep -q '"detail"' "$tmp/chain/verify-input.md" \
  && fail "verify input leaks the producer's detail — the assessment is anchored"
pass "verify stage is blind to the producer's argument"

echo
echo "all backlog fixture tests passed"
