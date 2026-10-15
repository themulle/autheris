#!/usr/bin/env python3
"""Print the `dotnet test --filter` expression for one CI shard of Autheris.Tests.Integration (WP-TS-02).

Usage: integration-shard-filter.py <shard-index> <shard-count>

Test classes are read from the test sources and assigned greedily (longest first) by the measured wall times in
tests/Autheris.Tests.Integration/ci-shard-weights.json; a class without a measurement weighs DEFAULT_WEIGHT seconds.
Shards 0..n-2 select their classes explicitly. The last shard is the exact complement of the others, so every test runs in
exactly one shard, including tests in classes this script does not recognise.
"""
import json
import pathlib
import re
import sys

NAMESPACE = "Autheris.Tests.Integration."
DEFAULT_WEIGHT = 10.0
BASE_FILTER = "Category!=Spark"

root = pathlib.Path(__file__).resolve().parents[2]
project = root / "tests" / "Autheris.Tests.Integration"


def classes() -> list[str]:
    pattern = re.compile(r"^\s*public\s+(?:sealed\s+|partial\s+|abstract\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)", re.MULTILINE)
    found = set()
    for path in project.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        found.update(pattern.findall(path.read_text(encoding="utf-8-sig")))
    return sorted(found)


def assign(names: list[str], weights: dict[str, float], count: int) -> list[list[str]]:
    shards: list[list[str]] = [[] for _ in range(count)]
    loads = [0.0] * count
    for name in sorted(names, key=lambda n: (-weights.get(n, DEFAULT_WEIGHT), n)):
        target = loads.index(min(loads))
        shards[target].append(name)
        loads[target] += weights.get(name, DEFAULT_WEIGHT)
    return shards


def main() -> None:
    index, count = int(sys.argv[1]), int(sys.argv[2])
    if count < 1 or not 0 <= index < count:
        sys.exit(f"invalid shard {index}/{count}")

    weights = json.loads((project / "ci-shard-weights.json").read_text(encoding="utf-8"))
    shards = assign(classes(), weights, count)

    if count == 1:
        print(BASE_FILTER)
    elif index < count - 1:
        selected = "|".join(f"FullyQualifiedName~{NAMESPACE}{name}." for name in sorted(shards[index]))
        print(f"{BASE_FILTER}&({selected})")
    else:
        others = sorted(name for shard in shards[:-1] for name in shard)
        excluded = "&".join(f"FullyQualifiedName!~{NAMESPACE}{name}." for name in others)
        print(f"{BASE_FILTER}&{excluded}")


if __name__ == "__main__":
    main()
