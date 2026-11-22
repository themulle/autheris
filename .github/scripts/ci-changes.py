#!/usr/bin/env python3
"""Classify a change set for the CI `changes` job (WP-OI-CI-1, #518).

Writes `dotnet=true|false` to $GITHUB_OUTPUT. `false` only when every changed path matches the positive DOC_ONLY list and
no DENY rule. Only pull_request events are ever narrowed; push and every other event yield dotnet=true. Fail-closed: an empty diff, a git error, an unknown event, a missing/zero/force-pushed base, an unnormalized
path or any exception always yields `dotnet=true`.

`dotnet=false` never skips a Security-trait suite wholesale: build-and-test, the integration shards and the Spark job
still run a narrowed filter (see ci.yml), because architecture and unit tests read docs/, deploy/, benchmarks/ and *.md.
"""
import fnmatch
import os
import posixpath
import re
import subprocess
import sys

# Deny rules win over the doc globs (a .md under src/ is an embedded resource, i.e. code).
DENY = (
    "src/*", "tests/*", "deploy/helm/*", ".github/*", "*.csproj", "*.props", "*.targets", "*.sln", "*.slnx",
    "global.json", "Directory.*", "nuget.config", "NuGet.Config", "packages.lock.json", ".editorconfig",
)
# Positive list of doc-only paths.
DOC_ONLY = ("docs/*", "*.md", "benchmarks/*", ".github/ISSUE_TEMPLATE/*", "LICENSE")
# Positive path triggers for PR smoke suites (WP-OI-CI-5, #522). Unrecognized/empty/error states fail closed to True.
ORACLE_PATTERNS = (
    "**/*Oracle*",
    "src/TrinoSqlEngine/Ast/Emit/*",
    "src/TrinoSqlEngine/Ast/Generators/OracleDialectGenerator.cs",
    "src/Autheris.Application/Sql/OracleBindByName.cs",
    "src/Autheris.Infrastructure/Persistence/BindByNameOracleConnection.cs",
    "src/Autheris.Infrastructure/Persistence/OracleConnectionSupport.cs",
    "tests/Autheris.Tests.Integration/OracleTestFixtures.cs",
    "tests/TrinoSqlEngine.Tests/Conformance/Fixtures/Oracle.json",
)
SPARK_PATTERNS = (
    "**/*Spark*",
    "**/*Databricks*",
    "src/TrinoSqlEngine/Ast/Emit/*",
    "src/TrinoSqlEngine/Ast/Generators/DatabricksDialectGenerator.cs",
    "src/Autheris.Infrastructure/Databricks/*",
    "src/Autheris.Domain/Options/DatabricksOptions.cs",
    "tests/Autheris.Tests.Integration/Spark/*",
    "tests/Autheris.Tests.Integration/Databricks/*",
    "tests/TrinoSqlEngine.Tests/Conformance/Fixtures/Databricks.json",
)
SHARED_CI_PATTERNS = (
    ".github/workflows/ci.yml",
    ".github/scripts/ci-changes.py",
    ".github/scripts/integration-shard-filter.py",
    "tests/test-images.txt",
    "tests/Autheris.Tests.Common/TestImages.cs",
)

ZERO_SHA = "0" * 40
SHA = re.compile(r"^[0-9a-f]{40}$")


def _normalize(path):
    """Return the normalized relative path, or None when it is suspicious."""
    if not path or "\\" in path or path.startswith("/") or "\x00" in path:
        return None
    if ".." in path.split("/") or posixpath.normpath(path) != path:
        return None
    return path


def is_doc_only(path):
    path = _normalize(path)
    if path is None:
        return False
    issue_template = path.startswith(".github/ISSUE_TEMPLATE/")
    if _match_path(path, tuple(d for d in DENY if not (issue_template and d == ".github/*"))):
        return False
    return _match_path(path, DOC_ONLY)


def _match_path(path, patterns):
    # Directory-style globs ("docs/*") match the whole subtree; "**/*X*" matches any path segment or basename;
    # "*.md" and "Directory.*" match the file name at any depth.
    for pattern in patterns:
        if pattern.startswith("**/"):
            sub = pattern[3:]
            if (
                fnmatch.fnmatchcase(path, sub)
                or fnmatch.fnmatchcase(posixpath.basename(path), sub)
                or any(fnmatch.fnmatchcase(part, sub) for part in path.split("/"))
            ):
                return True
        elif pattern.endswith("/*"):
            if path.startswith(pattern[:-1]):
                return True
        elif pattern.startswith("*.") or pattern.startswith("Directory."):
            if fnmatch.fnmatchcase(posixpath.basename(path), pattern):
                return True
        elif path == pattern or posixpath.basename(path) == pattern:
            if "/" not in pattern or path == pattern:
                return True
    return False


def classify(paths):
    """Return True when the .NET jobs must run."""
    paths = [p for p in paths if p is not None]
    if not paths or any(not p.strip() for p in paths):
        return True
    return not all(is_doc_only(p) for p in paths)


def _classify_suite(paths, suite_patterns):
    """Return True when the path-triggered smoke suite must run (fail-closed on empty or suspicious paths)."""
    if paths is None:
        return True
    paths = [p for p in paths if p is not None]
    if not paths or any(not p.strip() for p in paths):
        return True
    normalized = [_normalize(p) for p in paths]
    if any(n is None for n in normalized):
        return True
    combined = suite_patterns + SHARED_CI_PATTERNS
    return any(_match_path(n, combined) for n in normalized)


def classify_oracle(paths):
    """Return True when the Oracle smoke job must run."""
    return _classify_suite(paths, ORACLE_PATTERNS)


def classify_spark(paths):
    """Return True when the Spark integration job must run."""
    return _classify_suite(paths, SPARK_PATTERNS)


def _git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def _diff(rev):
    # -z: NUL separated, never quoted; --no-renames: both sides of a rename are listed as separate paths.
    out = _git("diff", "-z", "--no-renames", "--name-only", rev, "--")
    return [p for p in out.split("\0") if p]


def changed_paths(event, base, head):
    """Return the changed paths, or None when the diff cannot be trusted (fail-closed)."""
    try:
        if event == "pull_request":
            if not SHA.match(base or "") or base == ZERO_SHA or not SHA.match(head or ""):
                return None
            return _diff(f"{base}...{head}")
        # push (main) is never narrowed: with coalescing a docs-only push can supersede a pending code push.
    except Exception:
        return None
    return None


def decide(event, base, head, diff=changed_paths):
    try:
        paths = diff(event, base, head)
        return True if paths is None else classify(paths)
    except Exception:
        return True


def decide_oracle(event, base, head, diff=changed_paths):
    try:
        paths = diff(event, base, head)
        return True if paths is None else classify_oracle(paths)
    except Exception:
        return True


def decide_spark(event, base, head, diff=changed_paths):
    try:
        paths = diff(event, base, head)
        return True if paths is None else classify_spark(paths)
    except Exception:
        return True


def decide_all(event, base, head, diff=changed_paths):
    try:
        paths = diff(event, base, head)
        if paths is None:
            return {"dotnet": True, "oracle": True, "spark": True}
        return {
            "dotnet": classify(paths),
            "oracle": classify_oracle(paths),
            "spark": classify_spark(paths),
        }
    except Exception:
        return {"dotnet": True, "oracle": True, "spark": True}


def main():
    event = os.environ.get("CI_EVENT", "")
    base = os.environ.get("CI_BASE", "")
    head = os.environ.get("CI_HEAD", "")
    decisions = decide_all(event, base, head)
    lines = [f"{key}={'true' if val else 'false'}" for key, val in decisions.items()]
    for line in lines:
        print(line)
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            for line in lines:
                f.write(line + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
