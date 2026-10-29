#!/usr/bin/env python3
"""G7 shard reduce and aggregate (ARCH-MNT wave 3, WAVE3-SQLGATE-SC-06 / T-09). Standard library only.

  g7-aggregate.py reduce --report-glob GLOB --in-scope-list FILE --shard I --shards N --out FILE
      Reduces one shard's Stryker mutation-report.json to raw status counts per in-scope file (plus non-Ignored mutants outside
      the shard's files). Exit 1 when the report is missing or unparseable.

  g7-aggregate.py merge --results-glob GLOB --in-scope-list FILE --shards N --floor N [--break-at 85] [--needs-result R] [--summary-file F]
      Merges the reduced shard results. Exit 1 when: a shard result is missing, duplicated or from a different shard count; the
      union of the shard file sets differs from the full in-scope list (or two shards cover one file); an in-scope file has no
      valid mutant; a non-Ignored mutant sits outside the files; the summed valid mutants are below the floor; the score computed
      from the SUMMED raw counts (never from shard scores) is below the break; or a shard job did not end in success.

Score = (Killed + Timeout) / (Killed + Timeout + Survived + NoCoverage), the same formula as g7-summary.py (single run).
"""
import argparse
import glob
import json
import os
import sys

VALID = ("Killed", "Survived", "NoCoverage", "Timeout")


def normalize(path):
    return path.replace("\\", "/")


def read_list(path):
    with open(path, encoding="utf-8") as handle:
        return sorted({normalize(x.strip()) for x in handle if x.strip()})


def reduce_report(report, in_scope):
    """report: parsed Stryker JSON. Returns {"files": {scope: {status: n}}, "outside": {file: n}}."""
    files = {s: {} for s in in_scope}
    outside = {}
    for key, entry in report["files"].items():
        norm = normalize(key)
        scope = next((s for s in in_scope if norm == s or norm.endswith("/" + s)), None)
        for mutant in entry.get("mutants", []):
            status = mutant.get("status", "")
            if scope is None:
                if status != "Ignored":
                    outside[norm] = outside.get(norm, 0) + 1
                continue
            files[scope][status] = files[scope].get(status, 0) + 1
    return {"files": files, "outside": outside}


def cmd_reduce(args):
    in_scope = read_list(args.in_scope_list)
    reports = sorted(glob.glob(args.report_glob, recursive=True))
    data = None
    if len(reports) == 1:
        try:
            with open(reports[0], encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            data = None
    if not in_scope or data is None or not isinstance(data.get("files"), dict):
        print("g7 shard %d/%d: no usable report (found %d) or empty file list" % (args.shard, args.shards, len(reports)), file=sys.stderr)
        return 1
    result = reduce_report(data, in_scope)
    result.update({"version": 1, "shard": args.shard, "shards": args.shards})
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(result, handle, sort_keys=True)
    print("g7 shard %d/%d reduced: %d files" % (args.shard, args.shards, len(in_scope)))
    return 0


def merge(results, full, shards, floor, break_at, needs_result=""):
    """results: list of reduced shard dicts. Returns (errors, summary_lines)."""
    errors = []
    lines = ["### G7 mutation testing (Stryker.NET, %d shards)" % shards, ""]
    if needs_result and needs_result != "success":
        errors.append("the shard jobs ended as '%s', not success" % needs_result)

    seen = {}
    for r in results:
        if r.get("version") != 1 or r.get("shards") != shards or not isinstance(r.get("shard"), int):
            errors.append("a shard result has a wrong version or shard count (expected %d shards)" % shards)
            continue
        seen.setdefault(r["shard"], []).append(r)
    for i in range(shards):
        if i not in seen:
            errors.append("shard %d has no report (timeout, cancel or failure)" % i)
        elif len(seen[i]) > 1:
            errors.append("shard %d reported %d times" % (i, len(seen[i])))
    for i in seen:
        if not 0 <= i < shards:
            errors.append("unexpected shard index %d" % i)

    owner = {}
    totals = {}
    per_file = {}
    outside = {}
    for i in sorted(seen):
        if not 0 <= i < shards:
            continue
        r = seen[i][0]
        for name, counts in r.get("files", {}).items():
            if name in owner:
                errors.append("file %s is covered by shards %d and %d" % (name, owner[name], i))
                continue
            owner[name] = i
            for status, n in counts.items():
                totals[status] = totals.get(status, 0) + n
                if status in VALID:
                    per_file[name] = per_file.get(name, 0) + n
        for name, n in r.get("outside", {}).items():
            outside[name] = outside.get(name, 0) + n

    missing = sorted(set(full) - set(owner))
    extra = sorted(set(owner) - set(full))
    if len(seen) == shards:  # with a missing shard the file-set error would only repeat it
        for name in missing:
            errors.append("in-scope file %s is covered by no shard" % name)
    for name in extra:
        errors.append("file %s is not in the full in-scope list" % name)

    valid_total = sum(per_file.values())
    killed = totals.get("Killed", 0) + totals.get("Timeout", 0)
    score = 100.0 * killed / valid_total if valid_total else 0.0
    lines.append("Score: %.2f%% (killed+timeout %d of %d valid in-scope mutants; break %d, floor %d). Statuses: %s" % (
        score, killed, valid_total, break_at, floor, ", ".join("%s=%d" % kv for kv in sorted(totals.items()))))
    lines.append("")
    lines.append("| In-scope file | Shard | Valid mutants |")
    lines.append("|---|---|---|")
    for name in full:
        lines.append("| %s | %s | %d |" % (name, owner.get(name, "-"), per_file.get(name, 0)))

    for name in full:
        if name in owner and per_file.get(name, 0) == 0:
            errors.append("in-scope file %s has zero valid mutants" % name)
    for name in sorted(outside):
        errors.append("%d non-Ignored mutants outside the globs in %s" % (outside[name], name))
    if valid_total < floor:
        errors.append("only %d valid in-scope mutants, below the floor %d" % (valid_total, floor))
    if score < break_at:
        errors.append("mutation score %.2f%% is below the break %d%%" % (score, break_at))
    return errors, lines


def cmd_merge(args):
    errors = []
    try:
        full = read_list(args.in_scope_list)
    except OSError as ex:
        full = []
        errors.append("cannot read the in-scope file list: %s" % ex)
    if not full:
        errors.append("the in-scope file list is empty")
    results = []
    for name in sorted(glob.glob(args.results_glob, recursive=True)):
        try:
            with open(name, encoding="utf-8") as handle:
                results.append(json.load(handle))
        except (OSError, ValueError):
            errors.append("unreadable shard result %s" % os.path.basename(name))
    more, lines = merge(results, full, args.shards, args.floor, args.break_at, args.needs_result)
    errors.extend(more)
    if errors:
        lines.append("")
        lines.extend("- FAIL: " + e for e in errors)
    text = "\n".join(lines) + "\n"
    print(text)
    if args.summary_file:
        with open(args.summary_file, "a", encoding="utf-8") as handle:
            handle.write(text)
    return 1 if errors else 0


def main(argv):
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    red = sub.add_parser("reduce")
    red.add_argument("--report-glob", required=True)
    red.add_argument("--in-scope-list", required=True)
    red.add_argument("--shard", type=int, required=True)
    red.add_argument("--shards", type=int, required=True)
    red.add_argument("--out", required=True)
    mrg = sub.add_parser("merge")
    mrg.add_argument("--results-glob", required=True)
    mrg.add_argument("--in-scope-list", required=True)
    mrg.add_argument("--shards", type=int, required=True)
    mrg.add_argument("--floor", type=int, required=True)
    mrg.add_argument("--break-at", type=int, default=85)
    mrg.add_argument("--needs-result", default="")
    mrg.add_argument("--summary-file", default=os.environ.get("GITHUB_STEP_SUMMARY", ""))
    args = parser.parse_args(argv)
    return cmd_reduce(args) if args.command == "reduce" else cmd_merge(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
