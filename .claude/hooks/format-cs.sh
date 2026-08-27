#!/usr/bin/env bash
# Format a C# file after an agent edit. Fail-open: never block the agent.
# Stdin: Claude Code PostToolUse JSON or Cursor afterFileEdit JSON.

set -u

ROOT="${CLAUDE_PROJECT_DIR:-}"
if [[ -z "$ROOT" ]]; then
  ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
fi

input=$(cat)
file=$(printf '%s' "$input" | python3 -c '
import json, sys
try:
    data = json.load(sys.stdin)
except Exception:
    sys.exit(0)
tool_input = data.get("tool_input") or {}
path = (
    tool_input.get("file_path")
    or tool_input.get("path")
    or data.get("file_path")
    or data.get("filePath")
    or data.get("path")
    or ""
)
print(path)
') || true

[[ -z "${file:-}" ]] && exit 0

if [[ "$file" != /* ]]; then
  file="$ROOT/$file"
fi

case "$file" in
  *.cs) ;;
  *) exit 0 ;;
esac

case "$file" in
  */bin/*|*/obj/*) exit 0 ;;
esac

[[ -f "$file" ]] || exit 0

rel="${file#"$ROOT"/}"
[[ "$rel" == "$file" ]] && exit 0

command -v dotnet >/dev/null 2>&1 || exit 0

cd "$ROOT" || exit 0
python3 - "$rel" <<'PY' || true
import subprocess, sys
rel = sys.argv[1]
try:
    subprocess.run(
        ["dotnet", "format", "DuckNet.slnx", "--include", rel, "--verbosity", "quiet", "--no-restore"],
        timeout=25,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
except Exception:
    pass
PY
exit 0
