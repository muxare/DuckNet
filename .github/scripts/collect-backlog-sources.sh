#!/usr/bin/env bash
# Assemble the vision / description documents for a backlog build into one file.
# Merge only — no Claude.
#
# usage: collect-backlog-sources.sh OUTFILE [PATH ...]
#   With no PATH arguments, discovers the repo's standing description documents.
#   A PATH may be a file or a directory (*.md beneath it, one level).
#   Missing paths are reported and skipped: the chain must still run when only
#   the code is available.
#
# Exit 0 whether or not any document was found — "code only" is a valid build.
# The file always exists afterwards, so the chain's context block is stable.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 OUTFILE [PATH ...]" >&2
  exit 2
fi

out=$1
shift
root=$(cd -- "$(dirname -- "$0")/../.." && pwd)

if [[ $# -eq 0 ]]; then
  set -- README.md ImplementationPlan.md CentersBuildPlan.md \
    DuckNetArchitectureSteps.html docs/industry-mappings.md
fi

python3 - "$root" "$out" "$@" <<'PY'
import re
import sys
from pathlib import Path

root = Path(sys.argv[1])
out = Path(sys.argv[2])
requested = sys.argv[3:]

MAX_CHARS = 60000  # per document; enough for ImplementationPlan.md, bounded


def expand(spec: str):
    path = Path(spec)
    if not path.is_absolute():
        path = root / spec
    if path.is_dir():
        return sorted(p for p in path.glob("*.md"))
    return [path]


def strip_html(text: str) -> str:
    text = re.sub(r"(?is)<(script|style)\b.*?</\1>", "", text)
    text = re.sub(r"(?s)<!--.*?-->", "", text)
    text = re.sub(r"(?i)<(br|/p|/div|/h[1-6]|/li|/tr)\s*/?>", "\n", text)
    text = re.sub(r"(?s)<[^>]+>", " ", text)
    text = (
        text.replace("&nbsp;", " ").replace("&amp;", "&")
        .replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"')
    )
    text = re.sub(r"[ \t]+", " ", text)
    return re.sub(r"\n{3,}", "\n\n", text).strip()


chunks, found, missing = [], [], []
for spec in requested:
    for path in expand(spec):
        try:
            body = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            missing.append(spec)
            continue
        rel = path.relative_to(root) if path.is_relative_to(root) else path
        if path.suffix.lower() in (".html", ".htm"):
            body = strip_html(body)
        if len(body) > MAX_CHARS:
            body = body[:MAX_CHARS] + f"\n\n[truncated at {MAX_CHARS} characters]"
        found.append(str(rel))
        chunks.append(f"### {rel}\n\n{body.strip()}\n")

if not found:
    out.write_text(
        "No description or vision document was found or readable.\n"
        "Build the backlog from the code alone.\n"
    )
    print(f"collect-backlog-sources: no documents found; wrote code-only marker to {out}", file=sys.stderr)
else:
    header = "Documents read for this build:\n" + "\n".join(f"- {f}" for f in found) + "\n\n"
    out.write_text(header + "\n".join(chunks))
    print(f"collect-backlog-sources: {len(found)} document(s) -> {out}", file=sys.stderr)

for spec in missing:
    print(f"collect-backlog-sources: skipped unreadable path {spec}", file=sys.stderr)
PY
