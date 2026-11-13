#!/usr/bin/env python3
"""Classify a change set for the CI `changes` job (WP-OI-CI-1, #518).

Writes `dotnet=true|false` to $GITHUB_OUTPUT. `false` only when every changed path matches the positive DOC_ONLY list and
no DENY rule. Fail-closed: an empty diff, a git error, an unknown event, a missing/zero/force-pushed base, an unnormalized
path or any exception always yields `dotnet=true`.

`dotnet=false` never skips a Security-trait suite wholesale: build-and-test, the integration shards and the Spark job
still run a narrowed filter (see ci.yml), because architecture and unit tests read docs/, deploy/, benchmarks/ and *.md.
"""
import fnmatch
import os
import posixpath
import subprocess
import sys

# Deny rules win over the doc globs (a .md under src/ is an embedded resource, i.e. code).
DENY = (
    "src/*", "tests/*", "deploy/helm/*", ".github/*", "*.csproj", "*.props", "*.targets", "*.sln", "*.slnx",
    "global.json", "Directory.*", "nuget.config", "NuGet.Config", "packages.lock.json", ".editorconfig",
)
# Positive list of doc-only paths.
DOC_ONLY = ("docs/*", "*.md", "benchmarks/*", "deploy/*", ".github/ISSUE_TEMPLATE/*", "LICENSE")

ZERO_SHA = "0" * 40


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
    # Directory-style globs ("docs/*") match the whole subtree; "*.md" and "Directory.*" match the file name at any depth.
    for pattern in patterns:
        if pattern.endswith("/*"):
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


def _git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout


def _diff(rev):
    # -z: NUL separated, never quoted; --no-renames: both sides of a rename are listed as separate paths.
    out = _git("diff", "-z", "--no-renames", "--name-only", rev)
    return [p for p in out.split("\0") if p]


def changed_paths(event, base, head):
    """Return the changed paths, or None when the diff cannot be trusted (fail-closed)."""
    try:
        if event == "pull_request":
            if not base or set(base) == {"0"} or not head:
                return None
            return _diff(f"{base}...{head}")
        if event == "push":
            if not base or set(base) == {"0"} or not head:
                return None
            # A force push (the previous tip is not an ancestor) or a missing object is not trusted.
            subprocess.run(["git", "merge-base", "--is-ancestor", base, head], check=True, capture_output=True)
            return _diff(f"{base}..{head}")
    except Exception:
        return None
    return None


def decide(event, base, head, diff=changed_paths):
    try:
        paths = diff(event, base, head)
        return True if paths is None else classify(paths)
    except Exception:
        return True


def main():
    event = os.environ.get("CI_EVENT", "")
    base = os.environ.get("CI_BASE", "")
    head = os.environ.get("CI_HEAD", "")
    dotnet = decide(event, base, head)
    line = f"dotnet={'true' if dotnet else 'false'}"
    print(line)
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
