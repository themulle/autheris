#!/usr/bin/env python3
"""G7 summary and fail-closed verdict (ARCH-MNT wave 3, WAVE3-SQLGATE-SC-03/SC-05). Standard library only.

Usage: g7-summary.py --report-glob GLOB --in-scope-list FILE --floor N [--log-glob GLOB] [--summary-file FILE]

The in-scope list comes from `g7-scope.sh --list` (the globs of stryker-config.json are the single source).
Exit 1 when: the report is missing or unparseable (timeout or cancel), an in-scope file has no valid mutant, the total of valid
in-scope mutants is below the floor, or a non-Ignored mutant sits outside the in-scope files. The score gate itself (break 85) is
enforced by Stryker.
"""
import argparse
import glob
import json
import os
import sys

VALID = ("Killed", "Survived", "NoCoverage", "Timeout")


def normalize(path):
    return path.replace("\\", "/")


def tail(log_glob, lines=20):
    out = []
    for name in sorted(glob.glob(log_glob, recursive=True)):
        try:
            with open(name, encoding="utf-8", errors="replace") as handle:
                out.extend(handle.read().splitlines()[-lines:])
        except OSError:
            pass
    return out[-lines:]


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-glob", required=True)
    parser.add_argument("--in-scope-list", required=True)
    parser.add_argument("--floor", type=int, required=True)
    parser.add_argument("--log-glob", default="")
    parser.add_argument("--summary-file", default=os.environ.get("GITHUB_STEP_SUMMARY", ""))
    args = parser.parse_args(argv)

    lines = ["### G7 mutation testing (Stryker.NET)", ""]
    errors = []

    def finish():
        if errors:
            lines.append("")
            lines.extend("- FAIL: " + e for e in errors)
        text = "\n".join(lines) + "\n"
        print(text)
        if args.summary_file:
            with open(args.summary_file, "a", encoding="utf-8") as handle:
                handle.write(text)
        return 1 if errors else 0

    try:
        with open(args.in_scope_list, encoding="utf-8") as handle:
            in_scope = [normalize(x.strip()) for x in handle if x.strip()]
    except OSError as ex:
        errors.append("cannot read the in-scope file list: %s" % ex)
        return finish()
    if not in_scope:
        errors.append("the in-scope file list is empty")
        return finish()

    reports = sorted(glob.glob(args.report_glob, recursive=True))
    data = None
    if len(reports) == 1:
        try:
            with open(reports[0], encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            data = None
    if data is None or not isinstance(data.get("files"), dict):
        errors.append("G7 did not complete: timeout or cancel (no usable mutation-report.json; found %d)" % len(reports))
        last = tail(args.log_glob) if args.log_glob else []
        if last:
            lines.append("Last log lines:")
            lines.append("```")
            lines.extend(last)
            lines.append("```")
        return finish()

    per_file = {}
    totals = {}
    outside = []
    for key, entry in data["files"].items():
        norm = normalize(key)
        scope = next((s for s in in_scope if norm == s or norm.endswith("/" + s)), None)
        for mutant in entry.get("mutants", []):
            status = mutant.get("status", "")
            if scope is None:
                if status != "Ignored":
                    outside.append((norm, status))
                continue
            totals[status] = totals.get(status, 0) + 1
            if status in VALID:
                per_file[scope] = per_file.get(scope, 0) + 1

    valid_total = sum(per_file.values())
    killed = totals.get("Killed", 0) + totals.get("Timeout", 0)
    score = 100.0 * killed / valid_total if valid_total else 0.0
    lines.append("Score: %.2f%% (killed+timeout %d of %d valid in-scope mutants). Statuses: %s" % (
        score, killed, valid_total, ", ".join("%s=%d" % kv for kv in sorted(totals.items()))))
    lines.append("")
    lines.append("| In-scope file | Valid mutants |")
    lines.append("|---|---|")
    for scope in sorted(in_scope):
        lines.append("| %s | %d |" % (scope, per_file.get(scope, 0)))

    for scope in sorted(in_scope):
        if per_file.get(scope, 0) == 0:
            errors.append("in-scope file %s has zero valid mutants" % scope)
    if valid_total < args.floor:
        errors.append("only %d valid in-scope mutants, below the floor %d" % (valid_total, args.floor))
    if outside:
        per_out = {}
        for name, _ in outside:
            per_out[name] = per_out.get(name, 0) + 1
        for name in sorted(per_out):
            errors.append("%d non-Ignored mutants outside the globs in %s" % (per_out[name], name))
    return finish()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
