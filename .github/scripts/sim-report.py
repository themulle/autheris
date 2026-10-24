#!/usr/bin/env python3
"""Render the simulation corpus report (REQ-SIMTEST P1, SEC-SIM-14, SEC-SIM-19).

Usage:
    sim-report.py <results.jsonl> <baseline.json> <out.md>
    sim-report.py --self-test

Run with `python3 -I`. Stdlib only. The input is untrusted data: parsed with `json` only, bounded in size, validated per line.

Fail-closed (exit 1): missing, truncated, malformed, oversized (10 MB) or empty input; an unknown schema version, kind or disposition; a missing
or malformed baseline; no `Executed` line or fewer executed queries than the baseline requires for the sample; any security-class line
(Crash, Hang, ServerError, PolicyLeak); a WrongResult or UnexpectedRejection that is not classified; a KnownGap class that the baseline does not
list; more reference/recorded disagreements than the baseline allows. The report is written to <out.md> and appended to $GITHUB_STEP_SUMMARY
when set. Canaries are replaced by <canary> and cells are escaped before anything is rendered.
"""
import html
import json
import os
import pathlib
import re
import sys
import tempfile

MAX_BYTES = 10 * 1024 * 1024
MAX_LINES = 100_000
SCHEMA = 1
FINDING_KINDS = ("WrongResult", "UnexpectedRejection", "SchemaNonConformance", "Crash", "Hang", "ServerError", "PolicyLeak")
SECURITY_KINDS = ("Crash", "Hang", "ServerError", "PolicyLeak")
NOTE_KINDS = ("Executed", "Equal", "AcceptedDifference", "UnsupportedByDesign", "HarnessMismatch", "CircuitOpen")
DISPOSITIONS = ("KnownGap", "Finding", "AcceptedDifference", "UnsupportedByDesign", "PolicyLeak", "Crash", "Hang")
SUITE = re.compile(r"^[a-z][a-z0-9\-]{0,31}$")
CANARY = re.compile(r"SIMCANARY-[A-Za-z0-9\-]+")
CONTROL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
SAMPLE = re.compile(r"sample=([0-9]+|all)")
DRAFTS = "tests/Autheris.Tests.Simulation/KnownGaps/drafts"


class InvalidInput(Exception):
    pass


def clean(value) -> str:
    """Redact canaries, replace control characters (NUL included), escape HTML, drop pipe, backtick and line breaks."""
    text = CANARY.sub("<canary>", str(value))
    text = CONTROL.sub("?", text)
    text = html.escape(text, quote=True)
    return re.sub(r"[|`\r\n]", " ", text)


def parse_results(path: pathlib.Path) -> list[dict]:
    if not path.is_file():
        raise InvalidInput(f"results file not found: {path.name}")
    if path.stat().st_size > MAX_BYTES:
        raise InvalidInput("results file exceeds 10 MB")
    rows = []
    with path.open("r", encoding="utf-8") as handle:
        for number, line in enumerate(handle, start=1):
            if number > MAX_LINES:
                raise InvalidInput("results file exceeds 100000 lines")
            if not line.endswith("\n"):
                raise InvalidInput(f"line {number}: truncated (no trailing newline)")
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError as ex:
                raise InvalidInput(f"line {number}: not valid JSON ({ex.msg})") from None
            rows.append(validate(row, number))
    if not rows:
        raise InvalidInput("results file has no entries (an executed run must report at least one)")
    return rows


def validate(row, number: int) -> dict:
    if not isinstance(row, dict):
        raise InvalidInput(f"line {number}: not a JSON object")
    if row.get("v") != SCHEMA or isinstance(row.get("v"), bool):
        raise InvalidInput(f"line {number}: unknown schema version")
    if not isinstance(row.get("suite"), str) or not SUITE.match(row["suite"]):
        raise InvalidInput(f"line {number}: invalid suite")
    if row.get("kind") not in FINDING_KINDS + NOTE_KINDS:
        raise InvalidInput(f"line {number}: unknown kind")
    if not isinstance(row.get("errorClass"), str) or len(row["errorClass"]) > 400:
        raise InvalidInput(f"line {number}: invalid errorClass")
    count = row.get("count")
    if not isinstance(count, int) or isinstance(count, bool) or count < 0 or count > 1_000_000_000:
        raise InvalidInput(f"line {number}: invalid count")
    for key, limit in (("firstId", 64), ("repro", 1000)):
        if not isinstance(row.get(key), str) or len(row[key]) > limit:
            raise InvalidInput(f"line {number}: invalid {key}")
    disposition = row.get("disposition")
    if disposition is not None and disposition not in DISPOSITIONS:
        raise InvalidInput(f"line {number}: unknown disposition")
    return row


def parse_baseline(path: pathlib.Path) -> dict:
    if not path.is_file():
        raise InvalidInput(f"baseline file not found: {path.name}")
    if path.stat().st_size > MAX_BYTES:
        raise InvalidInput("baseline exceeds 10 MB")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as ex:
        raise InvalidInput(f"baseline is not valid JSON ({ex.msg})") from None
    if not isinstance(data, dict) or data.get("v") != SCHEMA:
        raise InvalidInput("baseline must be an object with v = 1")
    corpus = data.get("corpus")
    if not isinstance(corpus, dict):
        raise InvalidInput("baseline needs a 'corpus' object")
    unknown = set(corpus) - {"minExecuted", "maxHarnessMismatchPercent", "knownGapClasses"}
    if unknown:
        raise InvalidInput("baseline corpus has unknown keys")
    if not isinstance(corpus.get("minExecuted"), int) or isinstance(corpus["minExecuted"], bool) or corpus["minExecuted"] < 1:
        raise InvalidInput("baseline minExecuted must be a positive integer")
    pct = corpus.get("maxHarnessMismatchPercent")
    if not isinstance(pct, (int, float)) or isinstance(pct, bool) or not 0 <= pct <= 100:
        raise InvalidInput("baseline maxHarnessMismatchPercent must be 0..100")
    classes = corpus.get("knownGapClasses")
    if not isinstance(classes, list) or not all(isinstance(c, str) for c in classes):
        raise InvalidInput("baseline knownGapClasses must be a list of strings")
    return corpus


def evaluate(rows: list[dict], baseline: dict) -> tuple[str, list[str]]:
    problems: list[str] = []
    executed = sum(r["count"] for r in rows if r["kind"] == "Executed")
    executed_lines = [r for r in rows if r["kind"] == "Executed"]
    if not executed_lines:
        problems.append("no Executed line: the run did not report what it executed")
    sample_all = False
    sample_n = None
    for r in executed_lines:
        m = SAMPLE.search(r["repro"])
        if m and m.group(1) == "all":
            sample_all = True
        elif m:
            sample_n = int(m.group(1))
    required = baseline["minExecuted"]
    if not sample_all and sample_n is not None:
        required = min(required, sample_n)
    if executed_lines and executed < required:
        problems.append(f"executed {executed} queries, the baseline requires at least {required}")

    harness = sum(r["count"] for r in rows if r["kind"] == "HarnessMismatch")
    total = executed + harness
    allowed_pct = baseline["maxHarnessMismatchPercent"]
    if total and harness * 100 > allowed_pct * total:
        problems.append(f"{harness} reference/recorded disagreements exceed {allowed_pct}% of {total} queries")

    known = set(baseline["knownGapClasses"])
    gaps = []
    findings = []
    for r in rows:
        if r["kind"] in SECURITY_KINDS and r["count"] > 0:
            problems.append(f"{r['kind']}: {r['errorClass']} ({r['count']}x)")
            findings.append(r)
        elif r["kind"] in ("WrongResult", "UnexpectedRejection", "SchemaNonConformance"):
            disposition = r.get("disposition")
            if disposition == "KnownGap":
                gaps.append(r)
                if r["errorClass"] not in known:
                    problems.append(f"KnownGap class not in the baseline: {r['errorClass']}")
            elif disposition in ("AcceptedDifference", "UnsupportedByDesign"):
                findings.append(r)
            else:
                problems.append(f"unclassified {r['kind']}: {r['errorClass']} ({r['count']}x)")
                findings.append(r)

    lines = ["# Simulation corpus report", ""]
    lines += ["| executed | harness mismatches | known gap classes | red problems |", "|---|---|---|---|"]
    lines.append(f"| {executed} | {harness} | {len(gaps)} | {len(problems)} |")
    lines += ["", "## Run statistics", "", "| kind | class | count | detail |", "|---|---|---|---|"]
    for r in rows:
        if r["kind"] in NOTE_KINDS:
            lines.append(f"| {clean(r['kind'])} | {clean(r['errorClass'])} | {r['count']} | {clean(r['repro'])} |")
    lines += ["", "## Known gaps", ""]
    if gaps:
        lines += ["| class | count | first id | repro |", "|---|---|---|---|"]
        for r in gaps:
            lines.append(f"| {clean(r['errorClass'])} | {r['count']} | {clean(r['firstId'])} | {clean(r['repro'])} |")
        lines.append("")
        lines.append(f"Drafts: `{DRAFTS}/`")
    else:
        lines.append("No known gaps observed.")
    lines += ["", "## Findings and classified differences", ""]
    if findings:
        lines += ["| kind | disposition | class | count | first id |", "|---|---|---|---|---|"]
        for r in findings:
            lines.append(f"| {clean(r['kind'])} | {clean(r.get('disposition') or 'unclassified')} | {clean(r['errorClass'])} | {r['count']} | {clean(r['firstId'])} |")
    else:
        lines.append("None.")
    lines += ["", "## Red", ""]
    lines += [f"- {clean(p)}" for p in problems] if problems else ["None."]
    return "\n".join(lines) + "\n", problems


def run(results: pathlib.Path, baseline_path: pathlib.Path, out: pathlib.Path) -> int:
    try:
        rows = parse_results(results)
        baseline = parse_baseline(baseline_path)
    except (InvalidInput, UnicodeDecodeError) as ex:
        print(f"sim-report: {ex}", file=sys.stderr)
        return 1
    text, problems = evaluate(rows, baseline)
    out.write_text(text, encoding="utf-8")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text)
    if problems:
        print(f"sim-report: {len(problems)} problem(s), the job is red", file=sys.stderr)
        return 1
    return 0


def self_test() -> int:
    def line(kind, cls="x", count=1, repro="r", disposition=None, first="abc"):
        d = {"v": 1, "suite": "corpus", "kind": kind, "errorClass": cls, "count": count, "firstId": first, "repro": repro}
        if disposition:
            d["disposition"] = disposition
        return json.dumps(d) + "\n"

    base = {"v": 1, "corpus": {"minExecuted": 5000, "maxHarnessMismatchPercent": 1, "knownGapClasses": ["WrongResult:ResultMismatch:values differ (scenario A)"]}}
    ok = [line("Executed", "scenario:A", 5000, "sample=5000;seed=1;sampled=5000"), line("Equal", "all", 4990)]

    with tempfile.TemporaryDirectory() as tmp:
        d = pathlib.Path(tmp)
        res, bl, out = d / "r.jsonl", d / "b.json", d / "o.md"
        bl.write_text(json.dumps(base), encoding="utf-8")

        def check(lines, expected, raw=None, baseline=None):
            if baseline is not None:
                bl.write_text(baseline, encoding="utf-8")
            if raw is not None:
                res.write_bytes(raw)
            else:
                res.write_text("".join(lines), encoding="utf-8")
            code = run(res, bl, out)
            bl.write_text(json.dumps(base), encoding="utf-8")
            assert code == expected, f"expected {expected}, got {code}"

        check(ok, 0)
        check(ok + [line("WrongResult", "WrongResult:ResultMismatch:values differ (scenario A)", 3, disposition="KnownGap")], 0)
        check(ok + [line("WrongResult", "WrongResult:Other", 3, disposition="KnownGap")], 1)  # not in baseline
        check(ok + [line("WrongResult", "WrongResult:Other", 3)], 1)  # unclassified
        check(ok + [line("PolicyLeak", "PolicyLeak:RowFilter", 1, disposition="PolicyLeak")], 1)
        check(ok + [line("Crash", "Crash:x", 1)], 1)
        check([], 1)  # empty = failure, not green
        check([line("Equal")], 1)  # no Executed line
        check([line("Executed", "scenario:A", 10, "sample=5000;seed=1")], 1)  # too few executed
        check([line("Executed", "scenario:A", 10, "sample=10;seed=1")], 0)  # sample smaller than the baseline minimum
        check(ok + [line("HarnessMismatch", "x", 500)], 1)  # over 1 percent
        check([], 1, raw=b'{"v":1,"suite":"corpus"')  # truncated
        check([], 1, raw=b"not json\n")
        check([], 1, raw=(line("Equal").replace('"v": 1', '"v": 2')).encode())
        check([], 1, raw=(line("Nope")).encode())
        check([], 1, raw=b"x" * (MAX_BYTES + 1))
        check(ok, 1, baseline="{broken")
        check(ok, 1, baseline=json.dumps({"v": 1}))
        check(ok, 1, baseline=json.dumps({"v": 1, "corpus": {**base["corpus"], "minExecuted": True}}))
        assert run(d / "missing.jsonl", bl, out) == 1, "missing input must exit 1"

        # escaping and canary redaction
        res.write_text("".join(ok + [line("WrongResult", "c|`<b>SIMCANARY-ABC-1\x07", 1, repro="SIMCANARY-X\nnext", disposition="KnownGap")]), encoding="utf-8")
        bl.write_text(json.dumps(base), encoding="utf-8")
        run(res, bl, out)
        text = out.read_text(encoding="utf-8")
        assert "SIMCANARY" not in text, "canary leaked"
        assert "<b>" not in text and "&lt;b&gt;" in text, "html not escaped"
        assert "\x07" not in text, "control character not replaced"
    print("sim-report: self-test passed")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    if len(argv) != 3:
        print("usage: sim-report.py <results.jsonl> <baseline.json> <out.md> | --self-test", file=sys.stderr)
        return 1
    return run(pathlib.Path(argv[0]), pathlib.Path(argv[1]), pathlib.Path(argv[2]))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
