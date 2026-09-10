#!/usr/bin/env python3
"""Run a DuckNet agent chain: ordered, isolated Claude sessions joined by schema.

Every stage is its own headless session with no memory of the others. The only
thing that crosses a stage boundary is a structured object that has been
validated against the stage's declared schema — never prose, never a transcript.
A stage that cannot produce a valid object either stops the chain or degrades to
a declared fallback, depending on what losing it would cost.

usage: run-chain.py CHAIN.json [OUTDIR] [--dry-run]
  OUTDIR defaults to /tmp/<chain name>.
  --dry-run builds every stage input it can and skips the model calls.

Writes, inside OUTDIR:
  <stage>-input.md    the exact prompt+context sent
  <stage>-raw.json    the CLI envelope
  <stage>.json        the validated structured output
  chain-meta.json     per-stage exit / cost / turns, and the chain verdict

Exit 0 chain completed, 1 a fail-stage produced nothing usable, 2 usage or
infrastructure error (missing token, unreadable manifest).
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / ".github" / "scripts"
CHAIN_SCHEMA = ROOT / ".github" / "schemas" / "chain.schema.json"


def die(message: str, code: int = 2) -> None:
    print(f"run-chain: {message}", file=sys.stderr)
    sys.exit(code)


def resolve(path: str) -> Path:
    p = Path(path)
    return p if p.is_absolute() else ROOT / p


def validate(schema: Path, data: Path) -> bool:
    result = subprocess.run(
        [sys.executable, str(SCRIPTS / "validate-json.py"), str(schema), str(data), "--quiet"],
        capture_output=True,
        text=True,
    )
    if result.returncode == 2:
        die(f"validator error: {result.stderr.strip()}")
    if result.returncode != 0:
        sys.stderr.write(result.stdout + result.stderr)
        return False
    return True


def jq(filter_text: str, path: Path) -> str:
    result = subprocess.run(["jq", filter_text, str(path)], capture_output=True, text=True)
    if result.returncode != 0:
        die(f"jq failed on {path}: {result.stderr.strip()}")
    return result.stdout


def jq_bool(filter_text: str, path: Path) -> bool:
    text = jq(filter_text, path).strip()
    return text == "true"


def fence(body: str, kind: str) -> str:
    if kind == "markdown" or kind == "text":
        ticks = "````" if "```" in body else "```"
        return f"{ticks}\n{body.rstrip()}\n{ticks}\n"
    ticks = "````" if "```" in body else "```"
    return f"{ticks}json\n{body.rstrip()}\n{ticks}\n"


def build_input(
    stage: dict, outdir: Path, stage_files: dict[str, Path], dry_run: bool = False
) -> Path:
    prompt_path = resolve(stage["prompt"])
    if not prompt_path.is_file():
        die(f"stage '{stage['id']}': prompt not found: {prompt_path}")
    parts = [prompt_path.read_text().rstrip(), "\n"]

    for block in stage.get("context") or []:
        source = block["from"]
        fmt = block.get("format") or ("json" if source == "stage" else "text")
        body = None

        if source == "text":
            body = block.get("text", "")
            fmt = block.get("format") or "text"
        elif source == "env":
            body = os.environ.get(block["env"])
            if body is None:
                if block.get("optional"):
                    continue
                die(f"stage '{stage['id']}': environment variable {block['env']} is not set")
        elif source == "file":
            raw = os.environ.get(block["path_env"], "") if block.get("path_env") else block.get("path", "")
            if not raw:
                if block.get("optional"):
                    continue
                die(f"stage '{stage['id']}': context file path is empty")
            path = resolve(raw)
            if not path.is_file():
                if block.get("optional"):
                    continue
                die(f"stage '{stage['id']}': context file not found: {path}")
            body = path.read_text()
        elif source == "stage":
            ref = block["stage"]
            if ref not in stage_files:
                if not dry_run:
                    die(f"stage '{stage['id']}': refers to '{ref}', which has not run")
                # A dry run still renders every stage input, so the jq filter and
                # the prompt can be reviewed without spending anything.
                body = json.dumps({"dry-run": f"output of stage '{ref}' not available"})
            else:
                body = jq(block.get("jq") or ".", stage_files[ref])

        if block.get("heading"):
            parts.append(f"\n## {block['heading']}\n\n")
        parts.append(fence(body or "", fmt))

    path = outdir / f"{stage['id']}-input.md"
    path.write_text("".join(parts))
    return path


def env_value(stage: dict, key: str, default):
    override = stage.get(f"{key}_env")
    if override and os.environ.get(override):
        return os.environ[override]
    return stage.get(key, default)


def read_meta(path: Path) -> dict:
    out: dict[str, object] = {}
    if not path.is_file():
        return out
    for line in path.read_text().splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            out[key.strip()] = value.strip()
    for key in ("claude_exit", "num_turns"):
        if out.get(key):
            try:
                out[key] = int(out[key])  # type: ignore[assignment]
            except ValueError:
                pass
    if out.get("cost_usd"):
        try:
            out["cost_usd"] = float(out["cost_usd"])  # type: ignore[assignment]
        except ValueError:
            pass
    return out


def write_json(path: Path, obj) -> None:
    path.write_text(json.dumps(obj, indent=2) + "\n")


def run_stage(stage: dict, outdir: Path, stage_files: dict[str, Path], dry_run: bool) -> dict:
    sid = stage["id"]
    schema_path = resolve(stage["schema"])
    if not schema_path.is_file():
        die(f"stage '{sid}': schema not found: {schema_path}")
    out_path = outdir / f"{sid}.json"
    record = {
        "id": sid,
        "model": str(env_value(stage, "model", "sonnet")),
        "budget_usd": str(env_value(stage, "budget", "0")),
        "status": "ok",
        "output": str(out_path.relative_to(outdir)),
    }

    skip = stage.get("skip_if")
    if skip:
        ref = skip["stage"]
        if ref in stage_files and jq_bool(skip["jq"], stage_files[ref]):
            fallback = stage.get("skip_output")
            if fallback is None:
                die(f"stage '{sid}': skip_if fired but no skip_output declared")
            write_json(out_path, fallback)
            if not validate(schema_path, out_path):
                die(f"stage '{sid}': skip_output does not satisfy its own schema")
            stage_files[sid] = out_path
            record["status"] = "skipped"
            record["reason"] = skip.get("reason") or f"skip_if matched on stage '{ref}'"
            print(f"[{sid}] skipped: {record['reason']}")
            return record

    input_path = build_input(stage, outdir, stage_files, dry_run)
    record["input"] = str(input_path.relative_to(outdir))

    if dry_run:
        record["status"] = "dry-run"
        print(f"[{sid}] dry run: input at {input_path}")
        return record

    raw_path = outdir / f"{sid}-raw.json"
    meta_path = outdir / f"{sid}-meta.txt"
    meta_path.write_text("")
    cmd = [
        "bash", str(SCRIPTS / "run-claude.sh"),
        "--schema", str(schema_path),
        "--model", str(env_value(stage, "model", "sonnet")),
        "--budget", str(env_value(stage, "budget", "0.50")),
        "--tools", str(stage.get("tools") or "none"),
        "--input", str(input_path),
        "--output", str(raw_path),
        "--meta", str(meta_path),
    ]
    turns = env_value(stage, "max_turns", None)
    if turns:
        cmd += ["--max-turns", str(turns)]

    print(f"[{sid}] {record['model']}, budget ${record['budget_usd']}, tools {stage.get('tools')}")
    result = subprocess.run(cmd)
    record.update(read_meta(meta_path))
    if result.returncode == 2:
        die(f"stage '{sid}': {CHAIN_TOKEN_HINT}", 2)

    usable = False
    if raw_path.is_file() and raw_path.stat().st_size > 0:
        extracted = subprocess.run(
            ["jq", "-e", ".structured_output"], stdin=raw_path.open(), capture_output=True, text=True
        )
        if extracted.returncode == 0:
            out_path.write_text(extracted.stdout)
            usable = validate(schema_path, out_path)
            if not usable:
                record["schema_error"] = True

    if usable:
        stage_files[sid] = out_path
        print(f"[{sid}] ok ({out_path})")
        return record

    if (stage.get("on_missing_output") or "fail") == "degrade":
        fallback = stage.get("degrade_output")
        if fallback is None:
            die(f"stage '{sid}': on_missing_output is degrade but no degrade_output declared")
        write_json(out_path, fallback)
        if not validate(schema_path, out_path):
            die(f"stage '{sid}': degrade_output does not satisfy its own schema")
        stage_files[sid] = out_path
        record["status"] = "degraded"
        record["reason"] = "no valid structured output; continued on the declared fallback"
        print(f"[{sid}] degraded — see {raw_path}", file=sys.stderr)
        return record

    record["status"] = "failed"
    record["reason"] = "no valid structured output"
    print(f"[{sid}] failed — see {raw_path}", file=sys.stderr)
    return record


CHAIN_TOKEN_HINT = "CLAUDE_CODE_OAUTH_TOKEN is not set (run: claude setup-token)"


def run_merge(merge: dict, outdir: Path, stage_files: dict[str, Path]) -> dict:
    args = []
    for arg in merge["args"]:
        text = arg.replace("{{outdir}}", str(outdir))
        for sid, path in stage_files.items():
            text = text.replace(f"{{{{stage:{sid}}}}}", str(path))
        if "{{stage:" in text:
            die(f"merge: unresolved placeholder in argument '{arg}'")
        args.append(text)

    script = resolve(merge["script"])
    runner = ["bash"] if script.suffix == ".sh" else [sys.executable]
    out_path = outdir / merge["output"]
    result = subprocess.run(runner + [str(script)] + args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.stderr.write(result.stderr)
        die(f"merge script failed: {script}", 1)
    out_path.write_text(result.stdout)

    record = {"script": merge["script"], "output": merge["output"], "status": "ok"}
    if merge.get("schema"):
        if not validate(resolve(merge["schema"]), out_path):
            record["status"] = "invalid"
            die(f"merged output does not satisfy {merge['schema']}", 1)
    print(f"merged -> {out_path}")
    return record


def main(argv: list[str]) -> int:
    args = [a for a in argv[1:] if not a.startswith("--")]
    dry_run = "--dry-run" in argv[1:]
    if not args:
        print(__doc__, file=sys.stderr)
        return 2

    manifest_path = resolve(args[0])
    if not manifest_path.is_file():
        die(f"chain manifest not found: {manifest_path}")
    if not validate(CHAIN_SCHEMA, manifest_path):
        die(f"chain manifest does not satisfy {CHAIN_SCHEMA.name}")
    chain = json.loads(manifest_path.read_text())

    outdir = Path(args[1]) if len(args) > 1 else Path("/tmp") / chain["name"]
    outdir.mkdir(parents=True, exist_ok=True)

    seen: set[str] = set()
    for stage in chain["stages"]:
        if stage["id"] in seen:
            die(f"duplicate stage id '{stage['id']}'")
        seen.add(stage["id"])

    print(f"chain '{chain['name']}': {len(chain['stages'])} stage(s) -> {outdir}")
    stage_files: dict[str, Path] = {}
    records = []
    failed = False
    for stage in chain["stages"]:
        record = run_stage(stage, outdir, stage_files, dry_run)
        records.append(record)
        if record["status"] == "failed":
            failed = True
            break

    meta = {
        "chain": chain["name"],
        "outdir": str(outdir),
        "stages": records,
        "status": "failed" if failed else ("dry-run" if dry_run else "ok"),
    }
    costs = [r["cost_usd"] for r in records if isinstance(r.get("cost_usd"), float)]
    if costs:
        meta["total_cost_usd"] = round(sum(costs), 4)

    if not failed and not dry_run and chain.get("merge"):
        meta["merge"] = run_merge(chain["merge"], outdir, stage_files)

    write_json(outdir / "chain-meta.json", meta)
    print(f"chain-meta -> {outdir / 'chain-meta.json'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
