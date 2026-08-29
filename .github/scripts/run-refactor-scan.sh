#!/usr/bin/env bash
# Two-stage refactoring scan: scan -> independent confidence pass -> jq merge.
# Stage 1 (refactor-scan.md) finds opportunities; its schema has no confidence
# field, so it cannot score itself. Stage 2 (refactor-verify.md) is a separate
# session that re-checks each finding against the code and scores confidence,
# blind to the scanner's own argument. The merge is jq, not a model.
#
# usage: run-refactor-scan.sh [OUTDIR]      (default /tmp/refactor-scan)
# Final result: OUTDIR/findings-final.json
# Issue markdown: format-refactor-scan.sh FINDINGS.json SHA [RUN_URL]
# Issue actions:  plan-refactor-issues.py FINDINGS.json EXISTING.json SHA [RUN_URL]
# env overrides: SCAN_MODEL / VERIFY_MODEL (sonnet), SCAN_BUDGET_USD (1.00),
#   VERIFY_BUDGET_USD (0.50), SCAN_MAX_TURNS (40), VERIFY_MAX_TURNS (30)
# Nested scripts are invoked with bash so GitHub Actions does not depend on +x.
set -euo pipefail

root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
scripts="$root/.github/scripts"
outdir=${1:-/tmp/refactor-scan}
mkdir -p "$outdir"

bash "$scripts/run-claude.sh" \
  --schema "$root/.github/schemas/refactor-findings.schema.json" \
  --model "${SCAN_MODEL:-sonnet}" \
  --budget "${SCAN_BUDGET_USD:-1.00}" \
  --tools "Read,Grep,Glob" \
  --max-turns "${SCAN_MAX_TURNS:-40}" \
  --input "$root/.github/prompts/refactor-scan.md" \
  --output "$outdir/scan-raw.json"

if ! jq -e '.structured_output | type == "object"' "$outdir/scan-raw.json" >/dev/null 2>&1; then
  echo "scan produced no structured output — see $outdir/scan-raw.json" >&2
  exit 1
fi
jq '.structured_output' "$outdir/scan-raw.json" > "$outdir/findings.json"

if [[ "$(jq '.findings | length' "$outdir/findings.json")" -eq 0 ]]; then
  cp "$outdir/findings.json" "$outdir/findings-final.json"
  echo "no findings; wrote $outdir/findings-final.json"
  exit 0
fi

# Strip the scanner's argument (detail, effort, risk) so the confidence pass
# judges each claim against the code, not the scanner's persuasion.
{
  cat "$root/.github/prompts/refactor-verify.md"
  printf '\n## Findings to assess\n\n```json\n'
  jq '{findings: [.findings[] | del(.detail, .effort, .risk)]}' "$outdir/findings.json"
  printf '```\n'
} > "$outdir/verify-input.md"

bash "$scripts/run-claude.sh" \
  --schema "$root/.github/schemas/refactor-verdicts.schema.json" \
  --model "${VERIFY_MODEL:-sonnet}" \
  --budget "${VERIFY_BUDGET_USD:-0.50}" \
  --tools "Read,Grep,Glob" \
  --max-turns "${VERIFY_MAX_TURNS:-30}" \
  --input "$outdir/verify-input.md" \
  --output "$outdir/verify-raw.json"

if jq -e '.structured_output | type == "object"' "$outdir/verify-raw.json" >/dev/null 2>&1; then
  jq '.structured_output' "$outdir/verify-raw.json" > "$outdir/verdicts.json"
else
  echo "confidence pass produced no structured output — merging without it" >&2
  printf '{"assessments": [], "notes": ["confidence pass failed; confidence not assessed"]}\n' \
    > "$outdir/verdicts.json"
fi

bash "$scripts/merge-refactor-confidence.sh" \
  "$outdir/findings.json" "$outdir/verdicts.json" > "$outdir/findings-final.json"

echo "wrote $outdir/findings-final.json"
jq '{summary, findings: [.findings[] | {id, tier, category, confidence}]}' \
  "$outdir/findings-final.json"
