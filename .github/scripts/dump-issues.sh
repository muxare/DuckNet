#!/usr/bin/env bash
# Dump the repository's issue backlog as JSON for a chain to read.
# Merge only — no Claude. Requires `gh` authenticated for this repo.
#
# usage: dump-issues.sh OUTFILE [STATE] [LIMIT]
#   STATE  open (default) | closed | all
#   LIMIT  default 200
#
# Pull requests are excluded — gh's issue list already does that. Bodies are
# truncated so one essay-length issue cannot crowd out the rest of the backlog.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 OUTFILE [STATE] [LIMIT]" >&2
  exit 2
fi

out=$1
state=${2:-open}
limit=${3:-200}

if ! command -v gh >/dev/null 2>&1; then
  echo "dump-issues: gh is not installed" >&2
  exit 2
fi

gh issue list --state "$state" --limit "$limit" \
  --json number,title,body,state,labels,assignees,createdAt,updatedAt,comments,url \
  | jq '[ .[] | {
      number,
      title,
      state,
      url,
      labels: [ .labels[].name ],
      assignees: [ .assignees[].login ],
      createdAt,
      updatedAt,
      comment_count: (.comments | length),
      body: ((.body // "") | if length > 4000 then .[0:4000] + "\n\n[truncated]" else . end)
    } ]' > "$out"

echo "dump-issues: $(jq 'length' "$out") $state issue(s) -> $out" >&2
