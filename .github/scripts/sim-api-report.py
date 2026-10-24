#!/usr/bin/env python3
"""Check the Schemathesis output of the simulation API fuzz job (REQ-SIMTEST P2, SEC-SIM-14/18/19).

Usage:
    sim-api-report.py --har <file>... [--junit <file>...] --canaries <file> --gaps <file> --out <dir>
    sim-api-report.py --self-test

Run with `python3 -I`. Stdlib only. The input is untrusted data: parsed with `json` and `xml.etree` only, bounded in size (50 MB per file),
DOCTYPE/ENTITY declarations in XML are refused.

Hard failures (no KnownGap path): any response with status >= 500, any canary (the --canaries file or the reserved SIMCANARY- prefix) in a
response body or header, any stack-trace pattern in a response, a request or redirect that leaves the loopback host, a JUnit failure that is
neither a server error nor a schema-conformance failure. Schema-conformance failures (SchemaNonConformance) are red unless a KnownGap lists
their class. 4xx responses are grouped by (status, JSON code or error.code) and listed without failing.

Fail-closed (non-zero exit): missing, truncated, malformed, oversized or empty input, an unknown HAR version, a missing or empty --canaries file,
a gaps file with unknown keys, unknown kinds or a security class, a gap without a draft, a stale gap (strict xfail).
Output (scrubbed: no headers, no request bodies, canaries replaced by <canary>, excerpts capped at 200 characters): api-report.md and
api-results.jsonl in --out; the report is appended to $GITHUB_STEP_SUMMARY when set.
"""
import base64
import collections
import datetime
import html
import json
import os
import pathlib
import re
import sys
import tempfile
import urllib.parse
import xml.etree.ElementTree as ET

MAX_BYTES = 50 * 1024 * 1024
MAX_ENTRIES = 2_000_000
SCHEMA = 1
EXCERPT = 200
CANARY_PREFIX = re.compile(r"SIMCANARY-[A-Za-z0-9\-]+")
CONTROL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
STACK = (
    re.compile(r"--- End of stack trace"),
    re.compile(r"\bat [A-Z][\w`<>+]*(?:\.[\w`<>+]+)+\("),
    re.compile(r"System\.\w+Exception"),
)
GAP_ID = re.compile(r"^SIM-GAP-\d{3}$")
SEED = re.compile(r"^[0-9]{1,10}$")
LOOPBACK = ("127.0.0.1", "localhost")
GAP_KINDS = ("SchemaNonConformance", "Documented4xx")
SECURITY_KINDS = ("Crash", "Hang", "ServerError", "PolicyLeak")
GAP_KEYS = {"id", "kind", "class", "draft"}
SCHEMA_TITLES = (
    "response violates schema",
    "undocumented http status code",
    "undocumented content-type",
    "missing content-type",
    "response header",
    "missing header",
    "undefined status code",
)
FIXTURES = pathlib.Path(__file__).resolve().parent.parent.parent / "tests" / "Autheris.Tests.Simulation" / "ApiFuzz" / "fixtures"
SELF_CANARIES = ("SIMCANARY-MASK-7f3a91", "SIMCANARY-TENANT-9b1c42")


class InvalidInput(Exception):
    pass


def scrub(value, limit: int = EXCERPT) -> str:
    """Redact canaries, replace control characters (NUL included) and cap the length."""
    return CONTROL.sub("?", CANARY_PREFIX.sub("<canary>", str(value)))[:limit]


def clean(value, limit: int = EXCERPT) -> str:
    """Markdown cell: scrub, escape HTML, drop pipe, backtick and line breaks."""
    text = html.escape(scrub(value, limit), quote=True)
    return re.sub(r"[|`\r\n]", " ", text)


def read_bounded(path: pathlib.Path, what: str) -> bytes:
    if not path.is_file():
        raise InvalidInput(f"{what} file not found: {path.name}")
    if path.stat().st_size > MAX_BYTES:
        raise InvalidInput(f"{what} file exceeds 50 MB: {path.name}")
    return path.read_bytes()


def normalize(text: str) -> str:
    text = re.sub(r"(?<![A-Za-z\d])\d+", "#", text)
    text = re.sub(r"\"[^\"]*\"|'[^']*'", "'?'", text)
    return text.strip()[:120]


def load_canaries(path: pathlib.Path, extra: tuple = ()) -> list[str]:
    raw = read_bounded(path, "canaries").decode("utf-8", errors="strict")
    canaries = [line.strip() for line in raw.splitlines() if line.strip()]
    if not canaries:
        raise InvalidInput("canaries file is empty (the run must plant at least one canary)")
    return canaries + list(extra)


class Entry:
    __slots__ = ("method", "path", "url_host", "status", "headers", "body", "location", "code", "gql")


def parse_har(path: pathlib.Path) -> list[Entry]:
    raw = read_bounded(path, "HAR")
    try:
        doc = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise InvalidInput(f"HAR is not valid JSON (truncated or malformed): {path.name}") from None
    log = doc.get("log") if isinstance(doc, dict) else None
    if not isinstance(log, dict) or log.get("version") != "1.2" or not isinstance(log.get("entries"), list):
        raise InvalidInput(f"HAR has no log.version 1.2 with entries: {path.name}")
    if len(log["entries"]) > MAX_ENTRIES:
        raise InvalidInput("HAR has too many entries")
    if not log["entries"]:
        raise InvalidInput(f"HAR contains no requests (a run must record at least one): {path.name}")
    out = []
    for index, raw_entry in enumerate(log["entries"]):
        try:
            request, response = raw_entry["request"], raw_entry["response"]
            status = response["status"]
            if not isinstance(status, int) or isinstance(status, bool) or not 100 <= status <= 599:
                raise ValueError("status")
            url = urllib.parse.urlsplit(request["url"])
            entry = Entry()
            entry.method = str(request["method"]).upper()[:10]
            entry.path = url.path or "/"
            entry.url_host = url.hostname or ""
            entry.status = status
            headers = response.get("headers") or []
            entry.headers = [(str(h["name"]), str(h["value"])) for h in headers]
            content = response.get("content") or {}
            text = content.get("text") or ""
            if content.get("encoding") == "base64":
                text = base64.b64decode(text, validate=False).decode("utf-8", errors="replace")
            entry.body = str(text)
            entry.location = next((v for n, v in entry.headers if n.lower() == "location"), "")
            entry.code = extract_code(entry.body)
            entry.gql = graphql_codes(entry.body)
        except (KeyError, TypeError, ValueError, AttributeError):
            raise InvalidInput(f"HAR entry {index} is malformed: {path.name}") from None
        out.append(entry)
    return out


def extract_code(body: str) -> str:
    if not body.lstrip().startswith(("{", "[")):
        return "(none)"
    try:
        data = json.loads(body)
    except (json.JSONDecodeError, RecursionError):
        return "(none)"
    if isinstance(data, dict):
        code = data.get("code")
        error = data.get("error")
        if code is None and isinstance(error, dict):
            code = error.get("code")
        if isinstance(code, (str, int)) and not isinstance(code, bool):
            return normalize(str(code))[:60]
    return "(none)"


def graphql_codes(body: str) -> list[str]:
    """extensions.code of every entry of a GraphQL `errors` list ("(none)" when an entry carries no code); empty for any other body."""
    if not body.lstrip().startswith("{") or '"errors"' not in body:
        return []
    try:
        data = json.loads(body)
    except (json.JSONDecodeError, RecursionError):
        return []
    errors = data.get("errors") if isinstance(data, dict) else None
    if not isinstance(errors, list):
        return []
    codes = []
    for error in errors:
        ext = error.get("extensions") if isinstance(error, dict) else None
        code = ext.get("code") if isinstance(ext, dict) else None
        codes.append(normalize(str(code))[:60] if isinstance(code, (str, int)) and not isinstance(code, bool) else "(none)")
    return sorted(set(codes))


def parse_junit(path: pathlib.Path) -> list[tuple[str, str, str, str]]:
    """Returns (kind, errorClass, operation, method) for each failing or erroring test case."""
    raw = read_bounded(path, "JUnit")
    if re.search(rb"<!(DOCTYPE|ENTITY)", raw, re.IGNORECASE):
        raise InvalidInput(f"JUnit XML contains a DOCTYPE or ENTITY declaration: {path.name}")
    try:
        root = ET.fromstring(raw)
    except ET.ParseError:
        raise InvalidInput(f"JUnit XML is malformed: {path.name}") from None
    cases = list(root.iter("testcase"))
    if not cases:
        raise InvalidInput(f"JUnit has no test cases: {path.name}")
    out = []
    for case in cases:
        for node in list(case.findall("failure")) + list(case.findall("error")):
            text = (node.text or "")
            title = ""
            for line in text.splitlines():
                m = re.match(r"^- (ST\d+ .+)$", line.strip()) or re.match(r"^(ST\d+ .+)$", line.strip())
                if m:
                    title = m.group(1)
                    break
            title = title or (node.get("message") or "unknown")
            detail = ""
            lines = [ln.strip() for ln in text.splitlines()]
            for idx, ln in enumerate(lines):
                if ln in (title, "- " + title):
                    detail = next((x for x in lines[idx + 1:] if x), "")
                    break
            lowered = title.lower()
            if lowered.startswith("st113") or "graphql server error" in lowered:
                # Schemathesis flags every GraphQL `errors` entry; the HAR decides by extensions.code (INTERNAL_SERVER_ERROR is a hard failure).
                kind = "GraphQLError"
            elif "server error" in lowered:
                kind = "ServerError"
            elif any(t in lowered for t in SCHEMA_TITLES):
                kind = "SchemaNonConformance"
            else:
                kind = "HarnessError"
            name = (case.get("name") or "").split(" ", 1)
            method, operation = (name[0], name[1]) if len(name) == 2 else ("GET", "/")
            if kind == "GraphQLError":
                title, detail = "ST113 GraphQL error reported by the not_a_server_error check (classified by extensions.code in the HAR)", ""
            out.append((kind, normalize(f"{title} | {detail}" if detail else title), operation[:200], method[:10]))
    return out


def load_gaps(path: pathlib.Path) -> list[dict]:
    raw = read_bounded(path, "gaps")
    try:
        data = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError):
        raise InvalidInput("gaps file is not valid JSON") from None
    if not isinstance(data, dict) or set(data) != {"v", "gaps"} or data["v"] != SCHEMA or isinstance(data["v"], bool):
        raise InvalidInput("gaps file must be an object with exactly the keys v (1) and gaps")
    if not isinstance(data["gaps"], list):
        raise InvalidInput("gaps must be a list")
    drafts = path.resolve().parent.parent / "KnownGaps" / "drafts"
    seen = set()
    for gap in data["gaps"]:
        if not isinstance(gap, dict) or set(gap) != GAP_KEYS:
            raise InvalidInput("gap entries need exactly the keys id, kind, class, draft (unknown keys are refused)")
        if not all(isinstance(gap[k], str) for k in GAP_KEYS):
            raise InvalidInput("gap entry values must be strings")
        if gap["kind"] in SECURITY_KINDS or re.match(r"^(ServerError|PolicyLeak|Crash|Hang)\b", gap["class"]):
            raise InvalidInput(f"gap {gap['id']}: security classes can never be a KnownGap (SEC-SIM-18)")
        if gap["kind"] not in GAP_KINDS:
            raise InvalidInput(f"gap {gap['id']}: unknown kind (allowed: {', '.join(GAP_KINDS)})")
        if not GAP_ID.match(gap["id"]) or gap["id"] in seen:
            raise InvalidInput(f"gap id must be unique and match SIM-GAP-###: {gap['id'][:20]}")
        seen.add(gap["id"])
        if not gap["class"] or len(gap["class"]) > 400:
            raise InvalidInput(f"gap {gap['id']}: invalid class")
        if gap["kind"] == "Documented4xx":
            m = re.match(r"^(\d{3}):", gap["class"])
            if not m or not 400 <= int(m.group(1)) <= 499:
                raise InvalidInput(f"gap {gap['id']}: a Documented4xx class must start with a 4xx status and a colon")
        if gap["draft"] != gap["id"] + ".md" or not (drafts / gap["draft"]).is_file():
            raise InvalidInput(f"gap {gap['id']}: no issue draft {gap['id']}.md under KnownGaps/drafts")
    return data["gaps"]


class Group:
    def __init__(self, kind: str, error_class: str, first_id: str, repro: str, disposition: str | None = None):
        self.kind, self.error_class, self.first_id, self.repro, self.disposition = kind, error_class, first_id, repro, disposition
        self.count = 0


def repro_line(method: str, path: str, seed: str) -> str:
    # HAR URLs carry the server base path; --include-path takes the schema path template, so drop the OData service root.
    if path.startswith("/odata/v4/"):
        path = path[len("/odata/v4"):]
    safe_path = CANARY_PREFIX.sub("<canary>", path)[:200]
    return f"st run <schema> --seed {seed} --include-path {safe_path} --include-method {method}"


def evaluate(entries: list[Entry], junit: list, canaries: list[str], gaps: list[dict], seed: str):
    problems: list[str] = []
    groups: dict[tuple, Group] = {}
    statuses: collections.Counter = collections.Counter()

    def add(key, kind, error_class, first_id, repro, disposition=None):
        group = groups.get(key)
        if group is None:
            group = groups[key] = Group(kind, error_class, first_id, repro, disposition)
        group.count += 1
        return group

    if not entries:
        problems.append("the HAR files contain no requests (an executed run must record at least one)")
    for number, e in enumerate(entries, start=1):
        statuses[f"{e.status // 100}xx"] += 1
        tag = f"entry {number}"
        repro = repro_line(e.method, e.path, seed)
        if e.url_host not in LOOPBACK:
            add(("offhost",), "PolicyLeak", "PolicyLeak:request left the loopback host", tag, repro)
        if 300 <= e.status < 400 and e.location:
            host = urllib.parse.urlsplit(e.location).hostname
            if host is not None and host not in LOOPBACK:
                add(("redirect",), "PolicyLeak", "PolicyLeak:redirect to a non-loopback host", tag, repro)
        header_text = "\n".join(f"{n}: {v}" for n, v in e.headers)
        for place, text in (("body", e.body), ("header", header_text)):
            if any(c in text for c in canaries) or CANARY_PREFIX.search(text):
                add(("canary", place), "PolicyLeak", f"PolicyLeak:canary in response {place}", tag, repro)
            if any(p.search(text) for p in STACK):
                add(("stack", place), "PolicyLeak", f"PolicyLeak:stack trace in response {place}", tag, repro)
        for code in e.gql:
            if code in ("INTERNAL_SERVER_ERROR", "(none)"):
                # an unhandled exception behind a masked GraphQL error, or an error without a code: never a KnownGap
                add(("gql5xx", e.status, code), "ServerError", f"ServerError:GraphQL:{e.status}:{code}", tag, repro)
            else:
                add(("gql4xx", e.status, code), "Documented4xx", f"{e.status}:GraphQL:{code}", tag, repro)
        if e.status >= 500:
            add(("5xx", e.status, e.code), "ServerError", f"ServerError:{e.status}:{e.code}", tag, repro)
        elif 400 <= e.status < 500:
            add(("4xx", e.status, e.code), "Documented4xx", f"{e.status}:{e.code}", tag, repro)

    for kind, error_class, operation, method in junit:
        repro = repro_line(method, operation, seed)
        if kind == "GraphQLError":
            add(("gqljunit",), "Documented4xx", error_class, f"{method} {operation}", repro)
        elif kind == "SchemaNonConformance":
            add(("schema", error_class), kind, error_class, f"{method} {operation}", repro)
        elif kind == "ServerError":
            add(("junit5xx", error_class), "ServerError", f"ServerError:{error_class}", f"{method} {operation}", repro)
        else:
            add(("harness", error_class), "HarnessError", f"HarnessError:{error_class}", f"{method} {operation}", repro)

    gap_by_class = {(g["kind"], g["class"]): g for g in gaps}
    matched = set()
    for group in groups.values():
        if group.kind in SECURITY_KINDS or group.kind == "HarnessError":
            problems.append(f"{group.kind}: {group.error_class} ({group.count}x, first {group.first_id})")
        elif group.kind == "SchemaNonConformance":
            gap = gap_by_class.get((group.kind, group.error_class))
            if gap:
                group.disposition = "KnownGap"
                matched.add(gap["id"])
            else:
                problems.append(f"unclassified SchemaNonConformance: {group.error_class} ({group.count}x)")
        elif group.kind == "Documented4xx":
            gap = gap_by_class.get((group.kind, group.error_class))
            if gap:
                matched.add(gap["id"])
    for gap in gaps:
        if gap["kind"] == "SchemaNonConformance" and gap["id"] not in matched:
            problems.append(f"remove KnownGap {gap['id']}: it no longer reproduces (strict xfail)")
    return groups, statuses, problems


def render(groups, statuses, problems, seed: str, total: int) -> tuple[str, str]:
    ordered = sorted(groups.values(), key=lambda g: (g.kind, g.error_class))
    lines = ["# Simulation API fuzz report", "", f"Seed: {clean(seed)}", ""]
    lines += ["| requests | " + " | ".join(sorted(statuses)) + " | red problems |", "|---|" + "---|" * len(statuses) + "---|"]
    lines.append(f"| {total} | " + " | ".join(str(statuses[k]) for k in sorted(statuses)) + f" | {len(problems)} |")
    lines += ["", "## Groups (status and error code or class)", ""]
    if ordered:
        lines += ["| kind | class | count | first | disposition | repro |", "|---|---|---|---|---|---|"]
        for g in ordered:
            lines.append(f"| {clean(g.kind)} | {clean(g.error_class)} | {g.count} | {clean(g.first_id)} | {clean(g.disposition or '')} | {clean(g.repro, 300)} |")
    else:
        lines.append("None.")
    lines += ["", "Repro lines omit credentials: add the test-auth headers of the sim host with `-H` and pass the schema file of the failing phase.", ""]
    lines += ["## Red", ""]
    lines += [f"- {clean(p, 300)}" for p in problems] if problems else ["None."]
    results = []
    for g in ordered:
        row = {"v": SCHEMA, "suite": "api", "kind": g.kind if g.kind != "Documented4xx" else "UnexpectedRejection",
               "errorClass": scrub(g.error_class, 400), "count": g.count, "firstId": scrub(g.first_id, 64), "repro": scrub(g.repro, 1000)}
        if g.kind == "Documented4xx":
            row["kind"] = "UnexpectedRejection"
            row["disposition"] = "AcceptedDifference"
        elif g.disposition:
            row["disposition"] = g.disposition
        results.append(json.dumps(row))
    return "\n".join(lines) + "\n", "\n".join(results) + "\n"


def run(hars, junits, canaries_path, gaps_path, out_dir) -> int:
    try:
        if not hars:
            raise InvalidInput("no --har file given")
        seed = os.environ.get("AUTHERIS_SIM_SEED") or datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d")
        if not SEED.match(seed):
            raise InvalidInput("AUTHERIS_SIM_SEED must be 1 to 10 digits")
        canaries = load_canaries(canaries_path)
        gaps = load_gaps(gaps_path)
        entries = [e for har in hars for e in parse_har(har)]
        junit = [row for j in junits for row in parse_junit(j)]
    except (InvalidInput, UnicodeDecodeError, OSError) as ex:
        print(f"sim-api-report: {ex}", file=sys.stderr)
        return 2
    groups, statuses, problems = evaluate(entries, junit, canaries, gaps, seed)
    text, results = render(groups, statuses, problems, seed, len(entries))
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "api-report.md").write_text(text, encoding="utf-8")
    (out_dir / "api-results.jsonl").write_text(results, encoding="utf-8")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(text)
    if problems:
        for problem in problems:
            print(f"sim-api-report: red: {scrub(problem, 300)}", file=sys.stderr)
        print(f"sim-api-report: {len(problems)} problem(s), the job is red", file=sys.stderr)
        return 1
    return 0


def self_test() -> int:
    def entry(status=200, body="{}", headers=None, url="http://127.0.0.1:5071/odata/v4/x", location=None):
        hs = [{"name": "content-type", "value": "application/json"}] + (headers or [])
        if location:
            hs.append({"name": "Location", "value": location})
        return {"request": {"method": "GET", "url": url, "headers": [{"name": "X-Test-Roles", "value": "Reader"}]},
                "response": {"status": status, "headers": hs, "content": {"mimeType": "application/json", "text": body}}}

    def har(*entries):
        return json.dumps({"log": {"version": "1.2", "creator": {"name": "test"}, "entries": list(entries)}})

    with tempfile.TemporaryDirectory() as tmp:
        d = pathlib.Path(tmp)
        (d / "KnownGaps" / "drafts").mkdir(parents=True)
        (d / "ApiFuzz").mkdir()
        (d / "KnownGaps" / "drafts" / "SIM-GAP-901.md").write_text("draft", encoding="utf-8")
        canaries = d / "canaries.txt"
        canaries.write_text("\n".join(SELF_CANARIES) + "\n", encoding="utf-8")
        gaps = d / "ApiFuzz" / "gaps.json"
        gaps.write_text(json.dumps({"v": 1, "gaps": []}), encoding="utf-8")
        out = d / "out"

        def check(expected, har_text=None, raw=None, junit_text=None, gaps_text=None, canary_text=None, label=""):
            h = d / "x.har"
            if raw is not None:
                h.write_bytes(raw)
            else:
                h.write_text(har_text, encoding="utf-8")
            junits = []
            if junit_text is not None:
                j = d / "x.xml"
                j.write_text(junit_text, encoding="utf-8")
                junits = [j]
            if gaps_text is not None:
                gaps.write_text(gaps_text, encoding="utf-8")
            if canary_text is not None:
                canaries.write_text(canary_text, encoding="utf-8")
            try:
                code = run([h], junits, canaries, gaps, out)
            finally:
                gaps.write_text(json.dumps({"v": 1, "gaps": []}), encoding="utf-8")
                canaries.write_text("\n".join(SELF_CANARIES) + "\n", encoding="utf-8")
            assert code == expected, f"{label}: expected exit {expected}, got {code}"

        junit_fail = ('<testsuites><testsuite><testcase name="GET /a/b"><failure type="failure" message="ST108">1. Test Case ID: x\n\n'
                      '- ST108 Response violates schema\n\n    null is not of type "string"\n\nReproduce with:\n  curl -H \'X-Test-Roles: Reader\'</failure>'
                      '</testcase></testsuite></testsuites>')
        gap_class = "ST108 Response violates schema | null is not of type '?'"
        gap_json = json.dumps({"v": 1, "gaps": [{"id": "SIM-GAP-901", "kind": "SchemaNonConformance", "class": gap_class, "draft": "SIM-GAP-901.md"}]})
        ok = har(entry(200), entry(400, '{"code":"INVALID_FILTER"}'), entry(400, '{"error":{"code":"INVALID_FILTER"}}'), entry(403, "{}"))

        check(0, ok, label="clean")
        report = (out / "api-report.md").read_text(encoding="utf-8")
        assert "400:INVALID_FILTER" in report, "4xx must be grouped by status and code"
        rows = [json.loads(line) for line in (out / "api-results.jsonl").read_text(encoding="utf-8").splitlines()]
        grouped = next(r for r in rows if r["errorClass"] == "400:INVALID_FILTER")
        assert grouped["count"] == 2 and grouped["repro"].startswith("st run <schema> --seed "), "dedupe by (status, code) with a repro line"
        check(1, har(entry(200), entry(500, '{"code":"E1"}')), label="5xx")
        gql_internal = '{"errors":[{"message":"An internal server error occurred.","extensions":{"code":"INTERNAL_SERVER_ERROR"}}]}'
        gql_client = '{"errors":[{"message":"The aggregation arguments are not valid.","extensions":{"code":"AGGREGATION_ARGUMENT_INVALID"}}]}'
        check(1, har(entry(200), entry(200, gql_internal)), label="graphql internal error behind a 200")
        check(1, har(entry(200), entry(200, '{"errors":[{"message":"boom"}]}')), label="graphql error without a code")
        check(0, har(entry(200), entry(200, gql_client)), label="graphql governed client error is not a failure")
        assert "200:GraphQL:AGGREGATION_ARGUMENT_INVALID" in (out / "api-report.md").read_text(encoding="utf-8")
        check(1, har(entry(200), entry(503, "")), label="503")
        check(1, har(entry(200, '{"v":"SIMCANARY-MASK-7f3a91"}')), label="canary body")
        check(1, har(entry(200, "{}", [{"name": "X-Leak", "value": "SIMCANARY-TENANT-9b1c42"}])), label="canary header")
        check(1, har(entry(200, '{"v":"SIMCANARY-UNLISTED-1"}')), label="reserved canary prefix")
        check(1, har(entry(400, '{"detail":"   at Autheris.Api.Foo.Bar(String x)"}')), label="stack frame")
        check(1, har(entry(400, '{"detail":"System.NullReferenceException: x"}')), label="exception name")
        check(1, har(entry(400, "--- End of stack trace from previous location ---")), label="stack marker")
        check(1, har(entry(200, url="http://evil.example/x")), label="off-host request")
        check(1, har(entry(302, location="http://evil.example/x", url="http://127.0.0.1:5071/x")), label="off-host redirect")
        check(2, raw=b'{"log":{"version":"1.2","entries":[', label="truncated")
        check(2, raw=b"not json", label="malformed")
        check(2, har_text=json.dumps({"log": {"version": "9.9", "entries": []}}), label="unknown version")
        check(2, raw=b"x" * (MAX_BYTES + 1), label="oversized")
        check(2, har_text=har(), label="empty HAR")
        check(2, har_text='{"log":{"version":"1.2","entries":[{"request":{},"response":{}}]}}', label="malformed entry")
        check(2, ok, canary_text="\n", label="empty canaries")
        assert run([d / "missing.har"], [], canaries, gaps, out) == 2, "missing input must fail"
        missing_canaries = d / "none.txt"
        assert run([d / "x.har"], [], missing_canaries, gaps, out) == 2, "missing canaries file must fail"

        check(1, ok, junit_text=junit_fail, label="unclassified schema failure")
        check(0, ok, junit_text=junit_fail, gaps_text=gap_json, label="KnownGap")
        check(1, ok, gaps_text=gap_json, label="stale gap (strict xfail)")
        check(1, ok, junit_text=junit_fail.replace("Response violates schema", "Server error"), label="junit server error")
        check(0, ok, junit_text=junit_fail.replace("ST108 Response violates schema", "ST113 GraphQL server error"), label="junit GraphQL error is classified by the HAR")
        check(1, ok, junit_text=junit_fail.replace("ST108 Response violates schema", "ST999 Something else"), label="unknown junit failure")
        check(2, ok, junit_text="<testsuites><testsuite>", label="malformed junit")
        check(2, ok, junit_text="<!DOCTYPE x [<!ENTITY a 'b'>]><testsuites><testsuite><testcase name='GET /a'/></testsuite></testsuites>", label="doctype")
        for bad, why in (
            ({"id": "SIM-GAP-901", "kind": "ServerError", "class": "x", "draft": "SIM-GAP-901.md"}, "security kind"),
            ({"id": "SIM-GAP-901", "kind": "SchemaNonConformance", "class": "ServerError:500", "draft": "SIM-GAP-901.md"}, "security class"),
            ({"id": "SIM-GAP-901", "kind": "Documented4xx", "class": "500:E", "draft": "SIM-GAP-901.md"}, "5xx class"),
            ({"id": "SIM-GAP-901", "kind": "Nope", "class": "x", "draft": "SIM-GAP-901.md"}, "unknown kind"),
            ({"id": "SIM-GAP-901", "kind": "SchemaNonConformance", "class": "x", "draft": "SIM-GAP-901.md", "extra": 1}, "unknown key"),
            ({"id": "SIM-GAP-902", "kind": "SchemaNonConformance", "class": "x", "draft": "SIM-GAP-902.md"}, "missing draft"),
            ({"id": "bad", "kind": "SchemaNonConformance", "class": "x", "draft": "bad.md"}, "bad id"),
        ):
            check(2, ok, gaps_text=json.dumps({"v": 1, "gaps": [bad]}), label=f"gaps file: {why}")
        check(2, ok, gaps_text=json.dumps({"v": 1, "gaps": [], "extra": 1}), label="gaps file: unknown top-level key")
        check(2, ok, gaps_text="{broken", label="gaps file: malformed")

        # scrubbing: no header, no request value, no canary in the rendered output
        check(1, har(entry(500, '{"detail":"SIMCANARY-MASK-7f3a91 | `x` <b>"}', [{"name": "Authorization", "value": "Bearer secret-token"}])), label="scrub")
        text = (out / "api-report.md").read_text(encoding="utf-8") + (out / "api-results.jsonl").read_text(encoding="utf-8")
        assert "SIMCANARY" not in text and "secret-token" not in text and "X-Test-Roles" not in text and "<b>" not in text, "output not scrubbed"

    # the committed planted fixtures (the workflow runs this before the fuzz; a missing fixture fails closed)
    with tempfile.TemporaryDirectory() as tmp:
        d = pathlib.Path(tmp)
        (d / "KnownGaps" / "drafts").mkdir(parents=True)
        (d / "ApiFuzz").mkdir()
        canaries = d / "c.txt"
        canaries.write_text("\n".join(SELF_CANARIES) + "\n", encoding="utf-8")
        gaps = d / "ApiFuzz" / "g.json"
        gaps.write_text(json.dumps({"v": 1, "gaps": []}), encoding="utf-8")
        for name, expected in (("clean", 0), ("planted-5xx", 1), ("planted-canary", 1), ("planted-stacktrace", 1), ("malformed", 2)):
            fixture = FIXTURES / f"{name}.har"
            if not fixture.is_file():
                print(f"sim-api-report: self-test fixture missing: {name}.har", file=sys.stderr)
                return 1
            code = run([fixture], [], canaries, gaps, d / "out")
            assert code == expected, f"fixture {name}.har: expected exit {expected}, got {code}"
    print("sim-api-report: self-test passed")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        try:
            return self_test()
        except AssertionError as ex:
            print(f"sim-api-report: self-test FAILED: {ex}", file=sys.stderr)
            return 1
    hars, junits, canaries, gaps, out = [], [], None, None, None
    i = 0
    target = None
    while i < len(argv):
        arg = argv[i]
        if arg in ("--har", "--junit"):
            target = hars if arg == "--har" else junits
        elif arg in ("--canaries", "--gaps", "--out"):
            if i + 1 >= len(argv):
                print(f"sim-api-report: missing value for {arg}", file=sys.stderr)
                return 2
            value = pathlib.Path(argv[i + 1])
            if arg == "--canaries":
                canaries = value
            elif arg == "--gaps":
                gaps = value
            else:
                out = value
            i += 1
            target = None
        elif target is not None and not arg.startswith("--"):
            target.append(pathlib.Path(arg))
        else:
            print(f"sim-api-report: unknown argument {arg[:40]}", file=sys.stderr)
            return 2
        i += 1
    if canaries is None or gaps is None or out is None:
        print("usage: sim-api-report.py --har <f>... [--junit <f>...] --canaries <f> --gaps <f> --out <dir> | --self-test", file=sys.stderr)
        return 2
    return run(hars, junits, canaries, gaps, out)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
