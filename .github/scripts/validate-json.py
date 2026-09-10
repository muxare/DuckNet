#!/usr/bin/env python3
"""Validate a JSON document against a JSON Schema subset. No dependencies.

The repo has no npm/pip dependency beyond the Claude CLI, so this implements
the keywords the DuckNet schemas actually use rather than pulling in ajv or
jsonschema: type, properties, required, additionalProperties, items, enum,
const, anyOf, oneOf, pattern, min/maxLength, min/maxItems, minimum, maximum,
uniqueItems.

Unknown keywords are ignored, not silently accepted as failures: a schema that
grows a keyword this validator does not know still validates on the keywords it
does know, and `--strict-keywords` reports the gap.

usage: validate-json.py SCHEMA.json DATA.json [--strict-keywords] [--quiet]
exit 0 valid, 1 invalid, 2 usage/IO error.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

KNOWN = {
    "type", "properties", "required", "additionalProperties", "items", "enum",
    "const", "anyOf", "oneOf", "pattern", "minLength", "maxLength", "minItems",
    "maxItems", "minimum", "maximum", "uniqueItems",
    # annotations, no validation effect
    "$schema", "$id", "title", "description", "default", "examples",
}

TYPES = {
    "object": dict,
    "array": list,
    "string": str,
    "boolean": bool,
    "null": type(None),
}


def type_ok(value, name: str) -> bool:
    if name == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if name == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if name == "boolean":
        return isinstance(value, bool)
    expected = TYPES.get(name)
    if expected is None:
        return True  # unknown type name: do not invent a failure
    if expected in (dict, list, str) and isinstance(value, bool):
        return False
    return isinstance(value, expected)


def short(value) -> str:
    text = json.dumps(value, ensure_ascii=False)
    return text if len(text) <= 60 else text[:57] + "..."


class Validator:
    def __init__(self, strict_keywords: bool = False):
        self.errors: list[str] = []
        self.unknown: set[str] = set()
        self.strict_keywords = strict_keywords

    def fail(self, path: str, message: str) -> None:
        self.errors.append(f"{path or '/'}: {message}")

    def check(self, schema, data, path: str = "") -> None:
        if schema is True or schema == {}:
            return
        if schema is False:
            self.fail(path, "schema forbids any value here")
            return
        if not isinstance(schema, dict):
            self.fail(path, "schema fragment is not an object")
            return

        for keyword in schema:
            if keyword not in KNOWN:
                self.unknown.add(keyword)

        types = schema.get("type")
        if types is not None:
            names = types if isinstance(types, list) else [types]
            if not any(type_ok(data, n) for n in names):
                self.fail(path, f"expected type {'/'.join(names)}, got {short(data)}")
                return

        if "const" in schema and data != schema["const"]:
            self.fail(path, f"expected const {short(schema['const'])}, got {short(data)}")

        if "enum" in schema and data not in schema["enum"]:
            self.fail(path, f"{short(data)} is not one of {short(schema['enum'])}")

        for key in ("anyOf", "oneOf"):
            if key not in schema:
                continue
            matches = 0
            for branch in schema[key]:
                probe = Validator(self.strict_keywords)
                probe.check(branch, data, path)
                if not probe.errors:
                    matches += 1
            if matches == 0:
                self.fail(path, f"matches no {key} branch")
            elif key == "oneOf" and matches > 1:
                self.fail(path, f"matches {matches} oneOf branches, expected exactly 1")

        if isinstance(data, str):
            pattern = schema.get("pattern")
            if pattern and not re.search(pattern, data):
                self.fail(path, f"{short(data)} does not match /{pattern}/")
            if "minLength" in schema and len(data) < schema["minLength"]:
                self.fail(path, f"shorter than minLength {schema['minLength']}")
            if "maxLength" in schema and len(data) > schema["maxLength"]:
                self.fail(path, f"longer than maxLength {schema['maxLength']}")

        if isinstance(data, (int, float)) and not isinstance(data, bool):
            if "minimum" in schema and data < schema["minimum"]:
                self.fail(path, f"{data} is below minimum {schema['minimum']}")
            if "maximum" in schema and data > schema["maximum"]:
                self.fail(path, f"{data} is above maximum {schema['maximum']}")

        if isinstance(data, list):
            if "minItems" in schema and len(data) < schema["minItems"]:
                self.fail(path, f"has {len(data)} items, minItems {schema['minItems']}")
            if "maxItems" in schema and len(data) > schema["maxItems"]:
                self.fail(path, f"has {len(data)} items, maxItems {schema['maxItems']}")
            if schema.get("uniqueItems"):
                seen = [json.dumps(i, sort_keys=True) for i in data]
                if len(set(seen)) != len(seen):
                    self.fail(path, "items are not unique")
            if "items" in schema:
                for i, item in enumerate(data):
                    self.check(schema["items"], item, f"{path}/{i}")

        if isinstance(data, dict):
            props = schema.get("properties") or {}
            for name in schema.get("required") or []:
                if name not in data:
                    self.fail(path, f"missing required property '{name}'")
            extra = schema.get("additionalProperties")
            for name, value in data.items():
                if name in props:
                    self.check(props[name], value, f"{path}/{name}")
                elif extra is False:
                    self.fail(path, f"unexpected property '{name}'")
                elif isinstance(extra, dict):
                    self.check(extra, value, f"{path}/{name}")


def main(argv: list[str]) -> int:
    args = [a for a in argv[1:] if not a.startswith("--")]
    flags = {a for a in argv[1:] if a.startswith("--")}
    if len(args) != 2:
        print(__doc__.strip().splitlines()[-2], file=sys.stderr)
        return 2
    try:
        schema = json.loads(Path(args[0]).read_text())
        data = json.loads(Path(args[1]).read_text())
    except (OSError, json.JSONDecodeError) as err:
        print(f"validate-json: {err}", file=sys.stderr)
        return 2

    v = Validator("--strict-keywords" in flags)
    v.check(schema, data)

    if v.unknown and "--strict-keywords" in flags:
        for keyword in sorted(v.unknown):
            print(f"validate-json: unsupported keyword '{keyword}' ignored", file=sys.stderr)

    if v.errors:
        print(f"validate-json: {args[1]} fails {args[0]}", file=sys.stderr)
        for err in v.errors[:40]:
            print(f"  {err}", file=sys.stderr)
        if len(v.errors) > 40:
            print(f"  ... and {len(v.errors) - 40} more", file=sys.stderr)
        return 1

    if "--quiet" not in flags:
        print(f"validate-json: ok ({args[1]})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
