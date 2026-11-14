#!/usr/bin/env python3
"""Update tests/Autheris.Tests.Integration/ci-shard-weights.json from TRX test results (WP-OI-CI-2, #519).

Usage: update-shard-weights.py [--output <path>] <trx-file-or-dir> [<trx-file-or-dir> ...]

For each TRX file, computes the per-class wall-clock span from the earliest test startTime to the latest test endTime
(capturing class fixture lifetime), averages each class's span across the supplied runs, rounds to 0.1s (emitting integers
for whole seconds), and writes ci-shard-weights.json deterministically with sorted keys.
"""
import argparse
from collections import defaultdict
from datetime import datetime
import json
import pathlib
import sys
from typing import Iterable
import xml.etree.ElementTree as ET

NAMESPACE = "Autheris.Tests.Integration."
root = pathlib.Path(__file__).resolve().parents[2]
default_output = root / "tests" / "Autheris.Tests.Integration" / "ci-shard-weights.json"


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1] if "}" in tag else tag


def _normalize_class_name(raw_class: str) -> str:
    cls = raw_class.strip()
    if cls.startswith(NAMESPACE):
        cls = cls[len(NAMESPACE) :]
    if "+" in cls:
        cls = cls.split("+", 1)[0]
    return cls


def _class_from_test_name(test_name: str) -> str | None:
    head = test_name.split("(", 1)[0].strip()
    if "." not in head:
        return None
    raw_class = head.rsplit(".", 1)[0]
    return _normalize_class_name(raw_class)


def _parse_iso(ts: str) -> datetime:
    return datetime.fromisoformat(ts.strip())


def parse_trx_file(trx_path: pathlib.Path) -> dict[str, float]:
    """Return {class_name: wall_clock_span_seconds} for one TRX file."""
    tree = ET.parse(trx_path)
    root_el = tree.getroot()

    test_id_to_class: dict[str, str] = {}
    for elem in root_el.iter():
        if _local_name(elem.tag) == "UnitTest":
            test_id = elem.attrib.get("id")
            if not test_id:
                continue
            for child in elem:
                if _local_name(child.tag) == "TestMethod":
                    cls_attr = child.attrib.get("className")
                    if cls_attr:
                        test_id_to_class[test_id] = _normalize_class_name(cls_attr)

    starts: dict[str, list[datetime]] = defaultdict(list)
    ends: dict[str, list[datetime]] = defaultdict(list)

    for elem in root_el.iter():
        if _local_name(elem.tag) != "UnitTestResult":
            continue
        test_id = elem.attrib.get("testId", "")
        cls_name = test_id_to_class.get(test_id)
        if not cls_name:
            test_name = elem.attrib.get("testName", "")
            cls_name = _class_from_test_name(test_name)
        if not cls_name:
            continue

        start_str = elem.attrib.get("startTime")
        end_str = elem.attrib.get("endTime")
        if start_str and end_str:
            start_dt = _parse_iso(start_str)
            end_dt = _parse_iso(end_str)
            starts[cls_name].append(start_dt)
            ends[cls_name].append(end_dt)

    spans: dict[str, float] = {}
    for cls_name in starts:
        first_start = min(starts[cls_name])
        last_end = max(ends[cls_name])
        spans[cls_name] = max(0.0, (last_end - first_start).total_seconds())
    return spans


def collect_trx_files(paths: Iterable[pathlib.Path]) -> list[pathlib.Path]:
    found: list[pathlib.Path] = []
    for p in paths:
        if p.is_file() and p.suffix.lower() == ".trx":
            found.append(p)
        elif p.is_dir():
            found.extend(sorted(p.rglob("*.trx")))
    return sorted(set(found))


def _round_weight(val: float) -> int | float:
    r = round(val, 1)
    return int(r) if r.is_integer() else r


def compute_weights(trx_paths: Iterable[pathlib.Path]) -> dict[str, int | float]:
    files = collect_trx_files(trx_paths)
    if not files:
        raise ValueError("No .trx files found in the provided paths.")

    per_class_runs: dict[str, list[float]] = defaultdict(list)
    for trx in files:
        for cls_name, span in parse_trx_file(trx).items():
            per_class_runs[cls_name].append(span)

    weights: dict[str, int | float] = {}
    for cls_name in sorted(per_class_runs):
        samples = per_class_runs[cls_name]
        mean_span = sum(samples) / len(samples)
        weights[cls_name] = _round_weight(mean_span)
    return weights


def format_weights_json(weights: dict[str, int | float]) -> str:
    ordered = {k: weights[k] for k in sorted(weights)}
    return json.dumps(ordered, indent=2) + "\n"


def update_weights(
    trx_paths: Iterable[pathlib.Path],
    output_path: pathlib.Path = default_output,
) -> dict[str, int | float]:
    weights = compute_weights(trx_paths)
    output_path.write_text(format_weights_json(weights), encoding="utf-8")
    return weights


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Update ci-shard-weights.json from TRX files.")
    parser.add_argument(
        "-o",
        "--output",
        type=pathlib.Path,
        default=default_output,
        help="Path to ci-shard-weights.json",
    )
    parser.add_argument(
        "inputs",
        nargs="+",
        type=pathlib.Path,
        help="TRX files or directories containing TRX files",
    )
    args = parser.parse_args(argv)
    update_weights(args.inputs, args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
