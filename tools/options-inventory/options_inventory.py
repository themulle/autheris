#!/usr/bin/env python3
"""Options inventory for the configuration surface budget.

Scans core ``src/`` (module options live outside ``src/`` and are not counted) and lists every
``*Options`` class, its settable properties, how often each property name is referenced outside the
declaring file, ``bool *Enabled`` flags, security-named properties and undocumented properties.

Usage:
    python3 tools/options-inventory/options_inventory.py --summary
    python3 tools/options-inventory/options_inventory.py            # also writes artifacts/options-inventory*.csv

The CSV output goes to ``artifacts/`` (not committed). Counting rules mirror
``tests/Autheris.Tests.Architecture/OptionsBudgetTests.cs``.
"""
from __future__ import annotations

import argparse
import csv
import re
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}
CLASS_RE = re.compile(r"\bclass\s+(\w+Options)\b")
TYPE_RE = re.compile(r"\b(?:class|record|struct|interface)\s+(\w+)")
FLAG_RE = re.compile(r"public\s+bool\s+(\w*Enabled)\s*\{(.*)")
PROP_RE = re.compile(
    r"^\s*public\s+(?:required\s+)?(?:virtual\s+)?([\w<>\[\]?,.\s]+?)\s+(\w+)\s*\{\s*get;\s*(?:set|init);\s*\}(?:\s*=\s*([^;]+))?;?")
SECURITY_RE = re.compile(r"Enforce|Require|Allowed|Deny|Mask|Pii|Tls|Auth")
IDENT_RE = re.compile(r"\b\w+\b")


def is_comment(line: str) -> bool:
    t = line.lstrip()
    return t.startswith("//") or t.startswith("/*") or t.startswith("*")


def src_files() -> list[Path]:
    out = []
    for p in sorted((ROOT / "src").rglob("*.cs")):
        if any(part in SKIP_DIRS for part in p.parts):
            continue
        out.append(p)
    return out


def scan(files: list[Path]):
    classes: list[dict] = []
    props: list[dict] = []
    flags: list[dict] = []
    ident_total: Counter = Counter()
    ident_by_file: dict[Path, Counter] = {}
    for f in files:
        text = f.read_text(encoding="utf-8", errors="replace")
        lines = text.splitlines()
        c = Counter(IDENT_RE.findall("\n".join(l for l in lines if not is_comment(l))))
        ident_by_file[f] = c
        ident_total.update(c)
        stack: list[tuple[str, int]] = []  # (class name, brace depth at declaration)
        depth = 0
        pending: str | None = None
        for i, line in enumerate(lines):
            if not is_comment(line):
                m = CLASS_RE.search(line)
                if m:
                    classes.append({"class": m.group(1), "file": str(f.relative_to(ROOT)).replace("\\", "/")})
                tm = TYPE_RE.search(line)
                if tm and "=" not in line.split(tm.group(0))[0]:
                    pending = tm.group(1)
                fm = FLAG_RE.search(line)
                if fm:
                    flags.append({"class": (stack[-1][0] if stack else "?"), "name": fm.group(1),
                                  "tail": fm.group(2), "file": str(f.relative_to(ROOT)).replace("\\", "/")})
                if stack and stack[-1][0].endswith("Options"):
                    pm = PROP_RE.match(line)
                    if pm:
                        documented = False
                        j = i - 1
                        while j >= 0 and (lines[j].lstrip().startswith("[") or not lines[j].strip()):
                            j -= 1
                        k = j
                        while k >= 0 and lines[k].lstrip().startswith("///"):
                            if "<summary>" in lines[k] or "<inheritdoc" in lines[k]:
                                documented = True
                            k -= 1
                        props.append({
                            "class": stack[-1][0], "name": pm.group(2), "type": pm.group(1).strip(),
                            "default": (pm.group(3) or "").strip(), "file": str(f.relative_to(ROOT)).replace("\\", "/"),
                            "documented": documented,
                        })
                for ch in line:
                    if ch == "{":
                        depth += 1
                        if pending:
                            stack.append((pending, depth))
                            pending = None
                    elif ch == "}":
                        if stack and stack[-1][1] == depth:
                            stack.pop()
                        depth -= 1
    for p in props:
        f = ROOT / p["file"]
        outside = ident_total[p["name"]] - ident_by_file[f][p["name"]]
        p["consumers"] = max(outside, 0)
        p["security"] = bool(SECURITY_RE.search(p["name"]) or SECURITY_RE.search(p["class"]))
    for p in props:
        p["branch"] = ""
    for fl in flags:
        f = ROOT / fl["file"]
        fl["class_refs"] = max(ident_total[fl["class"]] - ident_by_file[f][fl["class"]], 0)
        dm = re.search(r"=\s*(true|false)\s*;", fl["tail"])
        fl["default"] = dm.group(1) if dm else ""
        fl["security"] = bool(SECURITY_RE.search(fl["name"]) or SECURITY_RE.search(fl["class"]))
        if fl["security"]:
            fl["branch"] = "keep-security"
        elif fl["default"] == "false":
            fl["branch"] = "keep-opt-in"
        elif fl["default"] == "true":
            fl["branch"] = "candidate-default-on"
        else:
            fl["branch"] = "review"
    return classes, props, flags



def summary(classes, props, flags) -> dict:
    enabled = flags
    return {
        "options_classes": len(classes),
        "settable_properties": len(props),
        "enabled_flags": len(enabled),
        "undocumented_properties": sum(1 for p in props if not p["documented"]),
        "unconsumed_properties": sum(1 for p in props if p["consumers"] == 0),
        "security_named_properties": sum(1 for p in props if p["security"]),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--summary", action="store_true", help="print summary numbers only, write no files")
    args = ap.parse_args()
    classes, props, flags = scan(src_files())
    s = summary(classes, props, flags)
    for k, v in s.items():
        print(f"{k}: {v}")
    if not args.summary:
        out = ROOT / "artifacts"
        out.mkdir(exist_ok=True)
        with (out / "options-inventory.csv").open("w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["class", "property", "type", "default", "consumers", "security", "documented", "file"])
            for p in props:
                w.writerow([p["class"], p["name"], p["type"], p["default"], p["consumers"], p["security"],
                            p["documented"], p["file"]])
        with (out / "options-enabled-flags.csv").open("w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["class", "property", "default", "security", "class_refs_outside_file", "branch", "file"])
            for fl in flags:
                w.writerow([fl["class"], fl["name"], fl["default"], fl["security"], fl["class_refs"], fl["branch"], fl["file"]])
        with (out / "options-classes.csv").open("w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["class", "file"])
            for c in classes:
                w.writerow([c["class"], c["file"]])
        print(f"wrote {out / 'options-inventory.csv'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
