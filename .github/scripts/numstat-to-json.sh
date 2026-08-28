#!/usr/bin/env bash
# Convert `git diff --numstat` on stdin to a JSON array of {path, added, deleted}.
set -euo pipefail
jq -R -s '
  split("\n")
  | map(select(length > 0))
  | map(split("\t"))
  | map(select(length >= 3) | {
      path: (.[2:] | join("\t")),
      added: (.[0] | if . == "-" then 0 else tonumber end),
      deleted: (.[1] | if . == "-" then 0 else tonumber end)
    })
'
