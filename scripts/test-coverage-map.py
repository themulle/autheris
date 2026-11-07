#!/usr/bin/env python3
"""
WP-SIMPL-23: Test coverage map and security test inventory scanner.
Scans C# test files under tests/ (and optionally a git revision) to extract
test method identifiers (Namespace.ClassName.MethodName) and security traits.
"""

from __future__ import annotations

import argparse
import csv
import pathlib
import re
import subprocess
import sys
from typing import Iterable

NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", re.MULTILINE)
TYPE_DECL_RE = re.compile(
    r"^\s*(?:public|internal|private|protected)\s+(?:(?:sealed|abstract|static|partial)\s+)*(?:class|record)\s+([A-Za-z0-9_]+)",
    re.MULTILINE,
)
FACT_ATTR_RE = re.compile(
    r"\[\s*(?:Fact|Theory|ParityFact|ParityTheory|SparkFact|SparkTheory|RequiresContainerRuntimeFact|RequiresContainerRuntimeTheory|Property)(?:Attribute)?\b"
)
SECURITY_TRAIT_RE = re.compile(
    r'\[\s*Trait(?:Attribute)?\s*\(\s*"Category"\s*,\s*"Security"\s*\)\s*\]'
)
METHOD_DECL_RE = re.compile(
    r"^\s*(?:public|internal|private|protected)\s+(?:(?:static|async|virtual|override|sealed)\s+)*[A-Za-z0-9_<>,.?\[\]\s]+\s+([A-Za-z0-9_]+)\s*\(",
    re.MULTILINE,
)


def extract_tests_from_source(source: str) -> tuple[set[str], set[str]]:
    """Return (all_test_ids, security_test_ids) from a C# test source string."""
    all_tests: set[str] = set()
    security_tests: set[str] = set()

    ns_match = NAMESPACE_RE.search(source)
    namespace = ns_match.group(1) if ns_match else "Global"

    type_matches = list(TYPE_DECL_RE.finditer(source))
    if not type_matches:
        return all_tests, security_tests

    for idx, t_match in enumerate(type_matches):
        class_name = t_match.group(1)
        class_start = t_match.start()
        body_end = type_matches[idx + 1].start() if idx + 1 < len(type_matches) else len(source)

        # Look immediately before the class declaration for class-level attributes.
        prefix_start = type_matches[idx - 1].end() if idx > 0 else 0
        class_attr_window = source[max(prefix_start, class_start - 600) : class_start]
        # Only count attributes after the last '}' or ';' before the class declaration.
        last_sep = max(class_attr_window.rfind("}"), class_attr_window.rfind(";"))
        if last_sep != -1:
            class_attr_window = class_attr_window[last_sep + 1 :]
        class_is_security = bool(SECURITY_TRAIT_RE.search(class_attr_window))

        class_body = source[t_match.end() : body_end]
        for m_match in METHOD_DECL_RE.finditer(class_body):
            method_name = m_match.group(1)
            if method_name == class_name:
                continue
            attr_window = class_body[max(0, m_match.start() - 600) : m_match.start()]
            last_method_sep = max(attr_window.rfind("}"), attr_window.rfind(";"), attr_window.rfind("{"))
            if last_method_sep != -1:
                attr_window = attr_window[last_method_sep + 1 :]

            if not FACT_ATTR_RE.search(attr_window):
                continue

            fqn = f"{namespace}.{class_name}.{method_name}"
            all_tests.add(fqn)
            if class_is_security or SECURITY_TRAIT_RE.search(attr_window):
                security_tests.add(fqn)

    return all_tests, security_tests


def scan_worktree(root: pathlib.Path) -> tuple[set[str], set[str]]:
    all_tests: set[str] = set()
    security_tests: set[str] = set()
    for folder_name in ("tests", "modules"):
        scan_dir = root / folder_name
        if not scan_dir.exists():
            continue
        for path in sorted(scan_dir.rglob("*.cs")):
            rel = path.relative_to(root).as_posix()
            if "/bin/" in rel or "/obj/" in rel:
                continue
            source = path.read_text(encoding="utf-8", errors="replace")
            file_all, file_sec = extract_tests_from_source(source)
            all_tests.update(file_all)
            security_tests.update(file_sec)
    return all_tests, security_tests


def scan_git_rev(root: pathlib.Path, rev: str) -> tuple[set[str], set[str]]:
    all_tests: set[str] = set()
    security_tests: set[str] = set()
    ls_out = subprocess.check_output(
        ["git", "-C", str(root), "ls-tree", "-r", "--name-only", rev, "tests/"],
        text=True,
    )
    for rel in ls_out.splitlines():
        if not rel.endswith(".cs") or "/bin/" in rel or "/obj/" in rel:
            continue
        source = subprocess.check_output(
            ["git", "-C", str(root), "show", f"{rev}:{rel}"],
            text=True,
            errors="replace",
        )
        file_all, file_sec = extract_tests_from_source(source)
        all_tests.update(file_all)
        security_tests.update(file_sec)
    return all_tests, security_tests


def load_coverage_map(csv_path: pathlib.Path) -> dict[str, tuple[str, str]]:
    mapping: dict[str, tuple[str, str]] = {}
    if not csv_path.exists():
        return mapping
    with csv_path.open("r", encoding="utf-8", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            old_id = (row.get("old_id") or "").strip()
            new_id = (row.get("new_id_or_reason") or "").strip()
            cat = (row.get("category") or "").strip()
            if old_id:
                mapping[old_id] = (new_id, cat)
    return mapping


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify or generate WP-SIMPL-23 security test coverage map.")
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    parser.add_argument("--baseline-file", type=pathlib.Path, default=None)
    parser.add_argument("--map-file", type=pathlib.Path, default=None)
    parser.add_argument("--verify", action="store_true", help="Verify baseline against current tests and coverage map.")
    args = parser.parse_args(list(argv) if argv is not None else None)

    root = args.root.resolve()
    baseline_file = args.baseline_file or (
        root / "tests" / "Autheris.Tests.Architecture" / "Baselines" / "security-tests-base.txt"
    )
    map_file = args.map_file or (root / "docs" / "plans" / "2026-11-05-test-coverage-map.csv")

    current_all, current_sec = scan_worktree(root)
    mapping = load_coverage_map(map_file)

    if args.verify:
        if not baseline_file.exists():
            print(f"ERROR: Missing baseline file {baseline_file}", file=sys.stderr)
            return 1
        baseline_sec = {
            line.strip()
            for line in baseline_file.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.strip().startswith("#")
        }
        missing = sorted(t for t in baseline_sec if t not in current_sec and t not in mapping)
        if missing:
            print(f"ERROR: {len(missing)} security test(s) missing without coverage map entry:", file=sys.stderr)
            for m in missing[:25]:
                print(f"  - {m}", file=sys.stderr)
            return 1
        print(f"OK: {len(baseline_sec)} baseline security tests accounted for ({len(current_sec)} active, {len(mapping)} mapped).")
        return 0

    print(f"Scanned worktree: {len(current_all)} total tests, {len(current_sec)} security tests.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
