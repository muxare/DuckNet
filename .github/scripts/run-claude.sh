#!/usr/bin/env bash
# Invoke Claude Code headless with a JSON schema. Never fails on verdict.
# Exit 0: ran (maybe no structured output). Exit 2: missing token (infra).
# Writes OUTPUT (raw envelope) and prints CLAUDE_EXIT / CLAUDE_SUBTYPE / COST_USD
# as GITHUB_OUTPUT-style lines on stdout (also copied to META file if set).
set -euo pipefail

schema=
model=
budget=
input=
output=
meta=
tools=none
max_turns=

while [[ $# -gt 0 ]]; do
  case "$1" in
    --schema) schema=$2; shift 2 ;;
    --model) model=$2; shift 2 ;;
    --budget) budget=$2; shift 2 ;;
    --input) input=$2; shift 2 ;;
    --output) output=$2; shift 2 ;;
    --meta) meta=$2; shift 2 ;;
    --tools) tools=$2; shift 2 ;;
    --max-turns) max_turns=$2; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$schema" || -z "$model" || -z "$budget" || -z "$input" || -z "$output" ]]; then
  echo "usage: $0 --schema FILE --model NAME --budget USD --input FILE --output FILE [--meta FILE] [--tools none|Read] [--max-turns N]" >&2
  exit 2
fi

# In CI a missing token is infrastructure: fail loudly rather than burn a job
# discovering the CLI is anonymous. Locally the CLI has its own login, which is
# what `/refactor-scan` and the backlog commands have always claimed to need —
# demanding the token there made the documented local flow impossible to run.
if [[ -z "${CLAUDE_CODE_OAUTH_TOKEN:-}" ]]; then
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    echo "::error::CLAUDE_CODE_OAUTH_TOKEN is not set on this repository."
    echo "claude_exit=2"
    if [[ -n "${meta:-}" ]]; then
      echo "claude_exit=2" >> "$meta"
    fi
    exit 2
  fi
  echo "run-claude: CLAUDE_CODE_OAUTH_TOKEN not set; using the local CLI login" >&2
fi

# The CLI validates --json-schema with a validator that has no meta-schemas
# registered and does not fetch them, so any "$schema" declaration is rejected
# outright: `no schema with key or ref "https://json-schema.org/draft-07/schema#"`.
# Keep the declaration in the files on disk -- editors and the jsonschema
# validation in run-chain.py read it -- and strip it only on the way to the CLI.
schema_json=$(jq -c 'del(."$schema")' "$schema")

args=(
  -p
  --output-format json
  --json-schema "$schema_json"
  --permission-mode dontAsk
  --model "$model"
  --max-budget-usd "$budget"
)

if [[ "$tools" == "none" ]]; then
  args+=(--tools "")
else
  args+=(--tools "$tools" --allowedTools "$tools")
fi
if [[ -n "$max_turns" ]]; then
  args+=(--max-turns "$max_turns")
fi

set +e
claude "${args[@]}" < "$input" > "$output"
claude_exit=$?
set -e

subtype=""
cost=""
turns=""
if [[ -s "$output" ]]; then
  jq '{is_error, subtype, total_cost_usd, num_turns}' "$output" >&2 || true
  subtype=$(jq -r '.subtype // empty' "$output" 2>/dev/null || true)
  cost=$(jq -r '.total_cost_usd // empty' "$output" 2>/dev/null || true)
  turns=$(jq -r '.num_turns // empty' "$output" 2>/dev/null || true)
fi

emit() {
  echo "claude_exit=${claude_exit}"
  echo "claude_subtype=${subtype}"
  echo "cost_usd=${cost}"
  echo "num_turns=${turns}"
}
emit
if [[ -n "${meta:-}" ]]; then
  emit >> "$meta"
fi
exit 0
