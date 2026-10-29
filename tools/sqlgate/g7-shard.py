#!/usr/bin/env python3
"""Deterministic G7 shard assignment (ARCH-MNT wave 3, WAVE3-SQLGATE-SC-06). Standard library only.

Usage: g7-shard.py --in-scope-list FILE --root DIR --shards N --index I [--weights FILE]

Prints the in-scope files of shard I (0-based), one per line. Files are weighted by the measured valid-mutant count of a past
CI run (--weights, tools/sqlgate/g7-weights.json). A file missing from the weights falls back to its line count scaled by the
average mutants-per-line of the known files; unreadable or stale weights never fail the run (line count only). Files are
sorted by weight descending then path, and assigned greedily to the currently lightest shard (ties: lowest index). The result
depends only on the file list and the file contents, so every shard computes the same partition independently.
Exit 1 on an empty list, a bad shard argument or an empty shard.
"""
import argparse
import json
import os
import sys


def weight(root, rel):
    try:
        with open(os.path.join(root, rel), "rb") as handle:
            return max(1, sum(1 for _ in handle))
    except OSError:
        return 1


def load_weights(path):
    """{file: positive int}; empty on a missing or unreadable file (stale weights only cost balance, never correctness)."""
    if not path:
        return {}
    try:
        with open(path, encoding="utf-8") as handle:
            raw = json.load(handle)
        return {normalize(k): int(v) for k, v in raw.items() if isinstance(v, int) and v > 0}
    except (OSError, ValueError, AttributeError):
        print("g7-shard: weights file unusable, using line counts", file=sys.stderr)
        return {}


def normalize(path):
    return path.replace("\\", "/")


def build_weights(files, root, measured):
    lines = {f: weight(root, f) for f in files}
    known = [f for f in files if f in measured]
    ratio = sum(measured[f] for f in known) / sum(lines[f] for f in known) if known else 1.0
    return {f: measured[f] if f in measured else max(1, round(lines[f] * ratio)) for f in files}


def assign(files, weights, shards):
    """Returns a list of `shards` lists; a partition of `files`."""
    order = sorted(files, key=lambda f: (-weights[f], f))
    loads = [0] * shards
    result = [[] for _ in range(shards)]
    for f in order:
        target = min(range(shards), key=lambda i: (loads[i], i))
        result[target].append(f)
        loads[target] += weights[f]
    return [sorted(r) for r in result]


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--in-scope-list", required=True)
    parser.add_argument("--root", default=".")
    parser.add_argument("--shards", type=int, required=True)
    parser.add_argument("--index", type=int, required=True)
    parser.add_argument("--weights", default="")
    args = parser.parse_args(argv)

    if args.shards < 1 or not 0 <= args.index < args.shards:
        print("g7-shard: invalid shard %d of %d" % (args.index, args.shards), file=sys.stderr)
        return 1
    with open(args.in_scope_list, encoding="utf-8") as handle:
        files = sorted({x.strip().replace("\\", "/") for x in handle if x.strip()})
    if len(files) < args.shards:
        print("g7-shard: %d files cannot fill %d shards" % (len(files), args.shards), file=sys.stderr)
        return 1
    weights = build_weights(files, args.root, load_weights(args.weights))
    mine = assign(files, weights, args.shards)[args.index]
    if not mine:
        print("g7-shard: shard %d is empty" % args.index, file=sys.stderr)
        return 1
    print("\n".join(mine))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
