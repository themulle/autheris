#!/usr/bin/env python3
"""Render the nightly parity report (REQ-PARITY-NIGHTLY D-6, SEC-PAR-06).

Usage:
    parity-report.py <results.jsonl> <baseline.json> <out.md> [--update-baseline]
    parity-report.py --self-test

Input is untrusted data: parsed with `json` only, bounded in size, validated per line. The report is written to <out.md> and
appended to $GITHUB_STEP_SUMMARY when set. Exit code 1: invalid input, or a cell that is `pass` in the baseline is now `fail`
or `gap` (green-to-red). Cells absent from the baseline never fail the report.
"""
import html
import json
import os
import pathlib
import re
import sys
import tempfile

MAX_BYTES = 50 * 1024 * 1024
MAX_LINES = 200_000
OUTCOMES = ("pass", "fail", "gap", "pending")
CELL_ID = re.compile(r"^[A-Za-z0-9_.\-]{1,200}$")
GAP_ID = re.compile(r"^PARITY-GAP-\d{3}$")
SHORT = re.compile(r"^[A-Za-z0-9_.\- ]{0,64}$")
PLAN = "docs/plans/2026-10-23-plan-nightly-parity-tests.md"


class InvalidInput(Exception):
    pass


def clean(value) -> str:
    """Escape for the markdown summary: HTML-escape, drop pipe, backtick and line breaks."""
    text = html.escape(str(value), quote=True)
    return re.sub(r"[|`\r\n]", " ", text)


def parse_results(path: pathlib.Path) -> list[dict]:
    if not path.is_file():
        raise InvalidInput(f"results file not found: {path.name}")
    if path.stat().st_size > MAX_BYTES:
        raise InvalidInput("results file exceeds 50 MB")
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for number, line in enumerate(handle, start=1):
            if number > MAX_LINES:
                raise InvalidInput("results file exceeds 200000 lines")
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as ex:
                raise InvalidInput(f"line {number}: not valid JSON ({ex.msg})") from None
            if not isinstance(row, dict):
                raise InvalidInput(f"line {number}: not a JSON object")
            if row.get("outcome") not in OUTCOMES:
                raise InvalidInput(f"line {number}: invalid outcome")
            cell = row.get("cellId")
            if not isinstance(cell, str) or not CELL_ID.match(cell):
                raise InvalidInput(f"line {number}: invalid cellId")
            gap = row.get("gapId")
            if gap is not None and (not isinstance(gap, str) or not GAP_ID.match(gap)):
                raise InvalidInput(f"line {number}: invalid gapId")
            for key in ("block", "channel", "capability", "dialect"):
                if not isinstance(row.get(key), str) or not SHORT.match(row[key]):
                    raise InvalidInput(f"line {number}: invalid {key}")
            rows.append(row)
    return rows


def parse_baseline(path: pathlib.Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    if path.stat().st_size > MAX_BYTES:
        raise InvalidInput("baseline file exceeds 50 MB")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as ex:
        raise InvalidInput(f"baseline is not valid JSON ({ex.msg})") from None
    cells = data.get("cells") if isinstance(data, dict) else None
    if not isinstance(cells, dict):
        raise InvalidInput("baseline must be an object with a 'cells' object")
    for cell, outcome in cells.items():
        if not CELL_ID.match(cell) or outcome not in OUTCOMES:
            raise InvalidInput("baseline contains an invalid entry")
    return dict(cells)


def render(rows: list[dict], baseline: dict[str, str]) -> tuple[str, list[str], list[str]]:
    latest: dict[str, dict] = {}
    for row in rows:
        latest[row["cellId"]] = row
    counts = {o: 0 for o in OUTCOMES}
    for row in latest.values():
        counts[row["outcome"]] += 1

    regressions = sorted(
        cell for cell, outcome in baseline.items()
        if outcome == "pass" and cell in latest and latest[cell]["outcome"] in ("fail", "gap")
    )
    missing = sorted(cell for cell, outcome in baseline.items() if outcome == "pass" and cell not in latest)

    gaps: dict[str, dict] = {}
    for row in latest.values():
        if row["outcome"] == "gap" and row.get("gapId"):
            entry = gaps.setdefault(row["gapId"], {"channels": set(), "capabilities": set(), "cells": 0})
            entry["channels"].add(row["channel"])
            entry["capabilities"].add(row["capability"])
            entry["cells"] += 1

    lines = ["# Parity nightly report", ""]
    lines.append("| pass | fail | gap | pending |")
    lines.append("|---|---|---|---|")
    lines.append("| " + " | ".join(str(counts[o]) for o in OUTCOMES) + " |")
    lines += ["", "## Known gaps", ""]
    if gaps:
        lines.append("| gap id | channel | capability | cells | draft |")
        lines.append("|---|---|---|---|---|")
        for gap in sorted(gaps):
            entry = gaps[gap]
            channels = ", ".join(sorted(entry["channels"]))
            capabilities = ", ".join(sorted(entry["capabilities"]))
            draft = f"{PLAN}#{gap.lower()}"
            lines.append(f"| {clean(gap)} | {clean(channels)} | {clean(capabilities)} | {entry['cells']} | {clean(draft)} |")
    else:
        lines.append("No known gaps observed.")
    tolerated = sorted((cell, row["observation"]) for cell, row in latest.items() if row.get("observation"))
    lines += ["", "## Tolerated 501 (unsupported, a 5xx: decide whether it should be a 4xx)", ""]
    if tolerated:
        lines += [f"- {clean(cell)}: {clean(code)}" for cell, code in tolerated]
    else:
        lines.append("None.")
    lines += ["", "## Green to red", ""]
    if regressions:
        lines += [f"- {clean(cell)} ({clean(latest[cell]['outcome'])})" for cell in regressions]
    else:
        lines.append("None.")
    if missing:
        lines += ["", "## Baseline cells without a result (warning only)", ""]
        lines += [f"- {clean(cell)}" for cell in missing]
    return "\n".join(lines) + "\n", regressions, missing


def update_baseline(rows: list[dict], path: pathlib.Path) -> None:
    latest = {row["cellId"]: row["outcome"] for row in rows}
    cells = {cell: "pass" for cell, outcome in sorted(latest.items()) if outcome == "pass"}
    path.write_text(json.dumps({"cells": cells}, indent=2) + "\n", encoding="utf-8")


def run(results: pathlib.Path, baseline_path: pathlib.Path, out: pathlib.Path, update: bool) -> int:
    try:
        rows = parse_results(results)
        baseline = parse_baseline(baseline_path)
    except InvalidInput as ex:
        print(f"parity-report: {ex}", file=sys.stderr)
        return 1
    if update:
        update_baseline(rows, baseline_path)
        print(f"parity-report: baseline updated ({sum(1 for r in rows if r['outcome'] == 'pass')} pass cells)")
        return 0
    text, regressions, _ = render(rows, baseline)
    out.write_text(text, encoding="utf-8")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text)
    if regressions:
        print(f"parity-report: {len(regressions)} cell(s) went green to red", file=sys.stderr)
        return 1
    return 0


def self_test() -> int:
    def row(cell, outcome, gap=None, channel="WebSql", capability="count"):
        return {"cellId": cell, "block": "b", "channel": channel, "capability": capability, "dialect": "sqlite",
                "outcome": outcome, "gapId": gap, "seed": "1"}

    def write(path, rows, raw=None):
        path.write_text(raw if raw is not None else "".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")

    with tempfile.TemporaryDirectory() as tmp:
        d = pathlib.Path(tmp)
        results, baseline, out = d / "r.jsonl", d / "b.json", d / "o.md"
        baseline.write_text(json.dumps({"cells": {"a.x.y.1": "pass", "a.x.y.2": "pass"}}), encoding="utf-8")

        # regression: pass -> fail exits 1
        write(results, [row("a.x.y.1", "fail"), row("a.x.y.2", "pass")])
        assert run(results, baseline, out, False) == 1, "regression must exit 1"
        # regression: pass -> gap exits 1
        write(results, [row("a.x.y.1", "gap", "PARITY-GAP-001"), row("a.x.y.2", "pass")])
        assert run(results, baseline, out, False) == 1, "pass->gap must exit 1"
        # new cell failing is ignored; gap table rendered; baseline pass stays pass
        write(results, [row("a.x.y.1", "pass"), row("a.x.y.2", "pass"), row("new.cell.1", "fail"),
                        row("g.1", "gap", "PARITY-GAP-002", "GraphQl", "sum"), row("g.2", "gap", "PARITY-GAP-002", "GraphQl", "avg")])
        assert run(results, baseline, out, False) == 0, "new failing cell must be ignored"
        text = out.read_text(encoding="utf-8")
        assert "| PARITY-GAP-002 | GraphQl | avg, sum | 2 |" in text, "gap table row missing"
        # tolerated 501 observations are listed
        tolerated = row("t.1", "gap", "PARITY-GAP-019")
        tolerated["observation"] = "SQL_FUNCTION_UNSUPPORTED_DIALECT"
        write(results, [row("a.x.y.1", "pass"), row("a.x.y.2", "pass"), tolerated])
        assert run(results, baseline, out, False) == 0
        assert "- t.1: SQL_FUNCTION_UNSUPPORTED_DIALECT" in out.read_text(encoding="utf-8"), "tolerated 501 not listed"
        # pending is not a regression
        write(results, [row("a.x.y.1", "pending"), row("a.x.y.2", "pass")])
        assert run(results, baseline, out, False) == 0, "pending is not a regression"
        # malformed / oversized / invalid input => exit 1
        for raw in ["not json\n", "[1,2]\n", json.dumps(row("a.x.y.1", "weird")) + "\n",
                    json.dumps(row("bad id|x", "pass")) + "\n", json.dumps(row("a.x.y.1", "gap", "GAP-1")) + "\n"]:
            write(results, [], raw)
            assert run(results, baseline, out, False) == 1, f"malformed input must exit 1: {raw!r}"
        write(results, [], "\n" * (MAX_LINES + 1))
        assert run(results, baseline, out, False) == 1, "too many lines must exit 1"
        assert run(d / "missing.jsonl", baseline, out, False) == 1, "missing results must exit 1"
        # markdown escaping
        assert "|" not in clean("a|b`c\nd<script>") and "<" not in clean("<b>"), "escape failed"
        rows = [row("g.9", "gap", "PARITY-GAP-003", "Chan nel", "cap")]
        rows[0]["channel"] = "Chan nel"
        text, _, _ = render(rows, {})
        assert "PARITY-GAP-003" in text
        # update-baseline
        write(results, [row("a.x.y.1", "pass"), row("a.x.y.2", "fail")])
        assert run(results, baseline, out, True) == 0
        assert json.loads(baseline.read_text(encoding="utf-8")) == {"cells": {"a.x.y.1": "pass"}}
    print("parity-report self-test OK")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    update = "--update-baseline" in argv
    args = [a for a in argv if a != "--update-baseline"]
    if len(args) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    return run(pathlib.Path(args[0]), pathlib.Path(args[1]), pathlib.Path(args[2]), update)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
