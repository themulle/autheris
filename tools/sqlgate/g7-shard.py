#!/usr/bin/env python3
"""Deterministic G7 shard assignment (ARCH-MNT wave 3, WAVE3-SQLGATE-SC-06). Standard library only.

Usage: g7-shard.py --in-scope-list FILE --root DIR --shards N --index I

Prints the in-scope files of shard I (0-based), one per line. Files are weighted by line count (a proxy for the mutant count),
sorted by weight descending then path, and assigned greedily to the currently lightest shard (ties: lowest index). The result
depends only on the file list and the file contents, so every shard computes the same partition independently.
Exit 1 on an empty list, a bad shard argument or an empty shard.
"""
import argparse
import os
import sys


def weight(root, rel):
    try:
        with open(os.path.join(root, rel), "rb") as handle:
            return max(1, sum(1 for _ in handle))
    except OSError:
        return 1


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
    args = parser.parse_args(argv)

    if args.shards < 1 or not 0 <= args.index < args.shards:
        print("g7-shard: invalid shard %d of %d" % (args.index, args.shards), file=sys.stderr)
        return 1
    with open(args.in_scope_list, encoding="utf-8") as handle:
        files = sorted({x.strip().replace("\\", "/") for x in handle if x.strip()})
    if len(files) < args.shards:
        print("g7-shard: %d files cannot fill %d shards" % (len(files), args.shards), file=sys.stderr)
        return 1
    weights = {f: weight(args.root, f) for f in files}
    mine = assign(files, weights, args.shards)[args.index]
    if not mine:
        print("g7-shard: shard %d is empty" % args.index, file=sys.stderr)
        return 1
    print("\n".join(mine))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
