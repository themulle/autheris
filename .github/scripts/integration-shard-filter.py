#!/usr/bin/env python3
"""Print the `dotnet test --filter` expression for one CI shard of Autheris.Tests.Integration (WP-TS-02, WP-OI-CI-2 #519).

Usage: integration-shard-filter.py <shard-index> <shard-count>

Test classes are read from the test sources and assigned greedily (longest first) by the measured wall times in
tests/Autheris.Tests.Integration/ci-shard-weights.json; a class without a measurement weighs the mean measured weight of the
weights file (or FALLBACK_WEIGHT when the file is empty). When unweighted classes exceed 10% of the total weight, a
GitHub Actions `::warning::` is emitted to stderr.
Shards 0..n-2 select their classes explicitly. The last shard is the exact complement of the others, so every test runs in
exactly one shard, including tests in classes this script does not recognise.
"""
import json
import pathlib
import re
import sys

NAMESPACE = "Autheris.Tests.Integration."
FALLBACK_WEIGHT = 10.0
UNWEIGHTED_WARN_THRESHOLD = 0.10
BASE_FILTER = "Category!=Spark&Category!=Parity&((Category!=Oracle&Category!=Stress)|Category=Security)"

root = pathlib.Path(__file__).resolve().parents[2]
project = root / "tests" / "Autheris.Tests.Integration"


def classes(project_dir: pathlib.Path = project) -> list[str]:
    ns_pattern = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)", re.MULTILINE)
    cls_pattern = re.compile(r"^\s*public\s+(?:sealed\s+|partial\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)", re.MULTILINE)
    found = set()
    for path in project_dir.rglob("*.cs"):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        ns_match = ns_pattern.search(text)
        ns = ns_match.group(1) if ns_match else NAMESPACE.rstrip(".")
        prefix = f"{ns[len(NAMESPACE):]}." if ns.startswith(NAMESPACE) else ""
        for name in cls_pattern.findall(text):
            if name.endswith("Fixture"):
                continue
            found.add(f"{prefix}{name}")
    return sorted(found)


def default_weight(weights: dict[str, float]) -> float:
    if not weights:
        return FALLBACK_WEIGHT
    return sum(float(v) for v in weights.values()) / len(weights)


def unweighted_warning(names: list[str], weights: dict[str, float]) -> str | None:
    if not names:
        return None
    fallback = default_weight(weights)
    unweighted = [n for n in names if n not in weights]
    if not unweighted:
        return None
    unweighted_total = len(unweighted) * fallback
    total = sum(float(weights.get(n, fallback)) for n in names)
    if total > 0.0 and (unweighted_total / total) > UNWEIGHTED_WARN_THRESHOLD:
        share = unweighted_total / total
        return (
            f"::warning::Unweighted integration test classes ({len(unweighted)}/{len(names)}) "
            f"account for {share:.1%} of total shard weight (> 10%); "
            "run .github/scripts/update-shard-weights.py"
        )
    return None


def assign(
    names: list[str],
    weights: dict[str, float],
    count: int,
    warn_stream=None,
) -> list[list[str]]:
    if count < 1:
        raise ValueError(f"count must be >= 1, got {count}")
    fallback = default_weight(weights)
    if warn_stream is not None:
        warning = unweighted_warning(names, weights)
        if warning is not None:
            print(warning, file=warn_stream)

    shards: list[list[str]] = [[] for _ in range(count)]
    loads = [0.0] * count
    for name in sorted(names, key=lambda n: (-float(weights.get(n, fallback)), n)):
        target = loads.index(min(loads))
        shards[target].append(name)
        loads[target] += float(weights.get(name, fallback))
    return shards


def shard_filter(shards: list[list[str]], index: int, base_filter: str = BASE_FILTER) -> str:
    count = len(shards)
    if count < 1 or not 0 <= index < count:
        raise ValueError(f"invalid shard {index}/{count}")
    if count == 1:
        return base_filter
    if index < count - 1:
        selected = "|".join(f"FullyQualifiedName~{NAMESPACE}{name}." for name in sorted(shards[index]))
        return f"{base_filter}&({selected})" if selected else base_filter
    others = sorted(name for shard in shards[:-1] for name in shard)
    excluded = "&".join(f"FullyQualifiedName!~{NAMESPACE}{name}." for name in others)
    return f"{base_filter}&{excluded}" if excluded else base_filter


def _tokenize_filter(expr: str) -> list[str]:
    return [t for t in re.findall(r"\(|\)|&|\||[^()&|\s]+", expr) if t]


def _eval_tokens(tokens: list[str], pos: int, fqn: str, cats: set[str]) -> tuple[bool, int]:
    """Recursive-descent evaluator for `dotnet test --filter` expressions (`|`, `&`, `(...)`)."""
    lhs, pos = _eval_and(tokens, pos, fqn, cats)
    while pos < len(tokens) and tokens[pos] == "|":
        rhs, pos = _eval_and(tokens, pos + 1, fqn, cats)
        lhs = lhs or rhs
    return lhs, pos


def _eval_and(tokens: list[str], pos: int, fqn: str, cats: set[str]) -> tuple[bool, int]:
    lhs, pos = _eval_atom(tokens, pos, fqn, cats)
    while pos < len(tokens) and tokens[pos] == "&":
        rhs, pos = _eval_atom(tokens, pos + 1, fqn, cats)
        lhs = lhs and rhs
    return lhs, pos


def _eval_atom(tokens: list[str], pos: int, fqn: str, cats: set[str]) -> tuple[bool, int]:
    if pos >= len(tokens):
        raise ValueError("unexpected end of filter expression")
    tok = tokens[pos]
    if tok == "(":
        val, pos = _eval_tokens(tokens, pos + 1, fqn, cats)
        if pos >= len(tokens) or tokens[pos] != ")":
            raise ValueError("unclosed '(' in filter expression")
        return val, pos + 1
    if tok.startswith("Category!="):
        return (tok[len("Category!="):] not in cats), pos + 1
    if tok.startswith("Category="):
        return (tok[len("Category="):] in cats), pos + 1
    if tok.startswith("FullyQualifiedName!~"):
        needle = tok[len("FullyQualifiedName!~"):]
        return ((needle not in fqn) if fqn else True), pos + 1
    if tok.startswith("FullyQualifiedName~"):
        needle = tok[len("FullyQualifiedName~"):]
        return ((needle in fqn) if fqn else True), pos + 1
    raise ValueError(f"unsupported filter predicate: {tok}")


def matches_category_filter(filter_expr: str, categories=()) -> bool:
    """Evaluate a filter expression against a set of Category traits."""
    tokens = _tokenize_filter(filter_expr)
    val, pos = _eval_tokens(tokens, 0, "", set(categories))
    if pos != len(tokens):
        raise ValueError(f"trailing tokens at {pos}: {tokens[pos:]}")
    return val


def matches_shard_filter(filter_expr: str, fqn: str, categories=()) -> bool:
    """Evaluate a shard filter expression against a test method FullyQualifiedName and Category traits."""
    tokens = _tokenize_filter(filter_expr)
    val, pos = _eval_tokens(tokens, 0, fqn, set(categories))
    if pos != len(tokens):
        raise ValueError(f"trailing tokens at {pos}: {tokens[pos:]}")
    return val


def main() -> None:
    index, count = int(sys.argv[1]), int(sys.argv[2])
    if count < 1 or not 0 <= index < count:
        sys.exit(f"invalid shard {index}/{count}")

    weights = json.loads((project / "ci-shard-weights.json").read_text(encoding="utf-8"))
    shards = assign(classes(), weights, count, warn_stream=sys.stderr)
    print(shard_filter(shards, index))


if __name__ == "__main__":
    main()
