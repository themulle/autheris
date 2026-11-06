#!/usr/bin/env python3
"""
Strip plan/ticket IDs from C# comments (//, ///, /* */) without modifying any
code tokens, string literals, character literals, or line numbers.
"""

import argparse
import os
import re
import sys
from typing import List, Tuple

PLAN_ID_PATTERN = (
    r"(?:REV-\d+|SEC-[A-Z0-9-]+|AST-X\d+|ARCH-MNT-\d+|"
    r"PLAN-[A-Z0-9-]+|WP-[A-Z0-9.-]+|REQ-[A-Z0-9-]+|API-\d+)"
)
PLAN_ID_RE = re.compile(rf"\b{PLAN_ID_PATTERN}\b")

# Exact regex from ProgramRatchetTests.cs
RATCHET_PLAN_ID_RE = re.compile(
    r"\b(REV-\d+|SEC-[A-Z0-9-]+|AST-X\d+|ARCH-MNT-\d+|PLAN-[A-Z]+-\d+|WP-[A-Z0-9.-]+|REQ-[A-Z]+-\d+)\b"
)

SKIPPED_DIRS = {"bin", "obj", "node_modules", ".git", ".claude", "worktrees"}


def tokenize_csharp(source: str) -> List[Tuple[str, str]]:
    """
    Tokenize C# source into ('CODE', text) and ('COMMENT', text) segments.
    Strings and char literals are kept inside 'CODE' segments so they are never treated as comments.
    """
    tokens: List[Tuple[str, str]] = []
    n = len(source)
    i = 0
    code_start = 0

    while i < n:
        ch = source[i]

        # Check for comments
        if ch == "/" and i + 1 < n:
            nxt = source[i + 1]
            if nxt == "/":
                if i > code_start:
                    tokens.append(("CODE", source[code_start:i]))
                j = i + 2
                while j < n and source[j] not in ("\r", "\n"):
                    j += 1
                tokens.append(("COMMENT", source[i:j]))
                i = j
                code_start = i
                continue
            elif nxt == "*":
                if i > code_start:
                    tokens.append(("CODE", source[code_start:i]))
                end_idx = source.find("*/", i + 2)
                if end_idx == -1:
                    j = n
                else:
                    j = end_idx + 2
                tokens.append(("COMMENT", source[i:j]))
                i = j
                code_start = i
                continue

        # Check for interpolated/raw/verbatim prefix ($ or @) or string quote (")
        if ch in ('"', "$", "@"):
            # Try matching raw string literal: zero or more '$' or '@' followed by >= 3 '"'
            j = i
            while j < n and source[j] in ("$", "@"):
                j += 1
            if j < n and source[j] == '"':
                # Count consecutive quotes
                q_start = j
                while j < n and source[j] == '"':
                    j += 1
                q_count = j - q_start
                prefix = source[i:q_start]

                if q_count >= 3 and "@" not in prefix:
                    # Raw string literal: ends at q_count consecutive quotes
                    closing = '"' * q_count
                    while j < n:
                        pos = source.find(closing, j)
                        if pos == -1:
                            j = n
                            break
                        j = pos + q_count
                        while j < n and source[j] == '"':
                            j += 1
                        break
                    i = j
                    continue
                elif "@" in prefix:
                    # Verbatim string literal: starts at q_start, "" is escaped quote
                    # Wait: if q_count == 2, that's an empty verbatim string @""!
                    k = q_start + 1
                    while k < n:
                        if source[k] == '"':
                            if k + 1 < n and source[k + 1] == '"':
                                k += 2
                                continue
                            k += 1
                            break
                        k += 1
                    i = k
                    continue
                else:
                    # Regular or interpolated string literal starting at q_start
                    # If q_count == 2, that's an empty string ""!
                    k = q_start + 1
                    while k < n:
                        c = source[k]
                        if c == "\\":
                            k += 2
                            continue
                        if c == '"' or c in ("\r", "\n"):
                            if c == '"':
                                k += 1
                            break
                        k += 1
                    i = k
                    continue

        # Character literal
        if ch == "'":
            k = i + 1
            while k < n:
                c = source[k]
                if c == "\\":
                    k += 2
                    continue
                if c == "'" or c in ("\r", "\n"):
                    if c == "'":
                        k += 1
                    break
                k += 1
            i = k
            continue

        i += 1

    if code_start < n:
        tokens.append(("CODE", source[code_start:n]))

    return tokens


def strip_comments(source: str) -> str:
    """Return source with all comment tokens removed (preserving newlines inside block comments)."""
    out: List[str] = []
    for kind, text in tokenize_csharp(source):
        if kind == "CODE":
            out.append(text)
        else:
            # Preserve line breaks inside block comments so line structure stays identical
            newlines = "".join(c for c in text if c in ("\r", "\n"))
            out.append(newlines)
    return "".join(out)


# Pre-compiled cleanup regexes for comment text
# 1. Parentheses or brackets containing ONLY plan IDs (and optional connectors like ',', '/', ';', 'and', '&')
ONLY_IDS_PARENS_RE = re.compile(
    rf"\s*[\(\[]\s*\b{PLAN_ID_PATTERN}\b(?:\s*(?:[,/;]|and|&|\+)\s*\b{PLAN_ID_PATTERN}\b)*\s*[\)\]]"
)

# 2. Plan ID at the start of a parenthesized/bracketed list: "(SEC-01, foo)" -> "(foo)"
LEADING_ID_IN_PARENS_RE = re.compile(
    rf"([\(\[]\s*)\b{PLAN_ID_PATTERN}\b(?:\s*(?:[,/;]|and|&)\s*\b{PLAN_ID_PATTERN}\b)*\s*(?:[,/;]|and|&)\s*"
)

# 3. Plan ID at the end of a parenthesized/bracketed list: "(foo, SEC-01)" -> "(foo)"
TRAILING_ID_IN_PARENS_RE = re.compile(
    rf"\s*(?:[,/;]|and|&)\s*\b{PLAN_ID_PATTERN}\b(?:\s*(?:[,/;]|and|&)\s*\b{PLAN_ID_PATTERN}\b)*(\s*[\)\]])"
)

# 4. Plan ID in the middle of a list: ", SEC-01, " -> ", "
MIDDLE_ID_RE = re.compile(
    rf"(,\s*)\b{PLAN_ID_PATTERN}\b\s*,\s*"
)

# 5. Plan ID prefix before ':' or '-' or '/' at start of sentence/summary:
# e.g., "WP-SIMPL-01 / SEC-SIMPL-02: " or "SEC-ADG-23: "
ID_PREFIX_COLON_RE = re.compile(
    rf"\b{PLAN_ID_PATTERN}\b(?:\s*(?:[,/;]|and|&)\s*\b{PLAN_ID_PATTERN}\b)*\s*[:—–-]\s*"
)

# 6. Plan ID followed by ' / ' (e.g. "SEC-GOV113-02 / Issue #226" -> "Issue #226")
ID_SLASH_LEFT_RE = re.compile(
    rf"\b{PLAN_ID_PATTERN}\b\s*/\s*"
)

# 7. ' / ' followed by Plan ID (e.g. "Issue #226 / SEC-GOV113-02" -> "Issue #226")
ID_SLASH_RIGHT_RE = re.compile(
    rf"\s*/\s*\b{PLAN_ID_PATTERN}\b"
)


def clean_comment_line_content(content: str) -> str:
    """Clean plan IDs from the inner text of a single comment line."""
    if not PLAN_ID_RE.search(content):
        return content

    s = content
    # Apply structured cleanups iteratively until stable
    prev = None
    while prev != s:
        prev = s
        s = ONLY_IDS_PARENS_RE.sub("", s)
        s = LEADING_ID_IN_PARENS_RE.sub(r"\1", s)
        s = TRAILING_ID_IN_PARENS_RE.sub(r"\1", s)
        s = MIDDLE_ID_RE.sub(r"\1", s)
        s = ID_PREFIX_COLON_RE.sub("", s)
        s = ID_SLASH_LEFT_RE.sub("", s)
        s = ID_SLASH_RIGHT_RE.sub("", s)

    # Remove any remaining bare plan IDs
    s = PLAN_ID_RE.sub("", s)

    # Clean up empty parens/brackets or dangling punctuation created by removal
    s = re.sub(r"\(\s*[,;/]*\s*\)", "", s)
    s = re.sub(r"\[\s*[,;/]*\s*\]", "", s)
    s = re.sub(r"\(\s*[,;/]\s*", "(", s)
    s = re.sub(r"\s*[,;/]\s*\)", ")", s)
    # Collapse multiple spaces within text (preserving leading spaces)
    leading_len = len(s) - len(s.lstrip(" \t"))
    leading = s[:leading_len]
    rest = s[leading_len:]
    rest = re.sub(r"  +", " ", rest)
    rest = re.sub(r"\s+\.", ".", rest)
    rest = re.sub(r"^[,;:]\s*", "", rest)
    rest = rest.strip()
    if not rest:
        return ""
    return (leading if leading else " ") + rest


def clean_comment_token(token_text: str) -> str:
    """Clean plan IDs from a single-line (//, ///) or block (/* */) comment token."""
    if not PLAN_ID_RE.search(token_text):
        return token_text

    if token_text.startswith("//"):
        # Distinguish /// (or ////) from //
        m = re.match(r"^(/+)(.*)$", token_text, re.DOTALL)
        assert m is not None
        slashes, body = m.group(1), m.group(2)
        # Preserve XML tags on the line while cleaning text segments
        cleaned_body = clean_xml_or_text_line(body)
        return slashes + cleaned_body

    if token_text.startswith("/*") and token_text.endswith("*/"):
        inner = token_text[2:-2]
        # Split preserving line endings
        parts = re.split(r"(\r\n|\r|\n)", inner)
        out_parts: List[str] = []
        for idx, part in enumerate(parts):
            if idx % 2 == 1:
                out_parts.append(part)
            else:
                # Check for leading ' * ' on block comment lines
                m = re.match(r"^(\s*\*?)(.*)$", part)
                if m:
                    prefix, body = m.group(1), m.group(2)
                    out_parts.append(prefix + clean_xml_or_text_line(body))
                else:
                    out_parts.append(clean_xml_or_text_line(part))
        return "/*" + "".join(out_parts) + "*/"

    return token_text


def clean_xml_or_text_line(line_body: str) -> str:
    """
    Clean plan IDs from a comment line body while keeping XML tags (<summary>, etc.) intact.
    """
    if not PLAN_ID_RE.search(line_body):
        return line_body

    # Split by XML tags (<...>) so we only clean text nodes outside tags
    segments = re.split(r"(<[^>]+>)", line_body)
    cleaned_segments: List[str] = []
    for idx, seg in enumerate(segments):
        if idx % 2 == 1:
            # XML tag
            cleaned_segments.append(seg)
        else:
            if PLAN_ID_RE.search(seg):
                cleaned = clean_comment_line_content(seg)
                # If this text segment was between XML tags or after <summary>, don't force a leading space right after '>'
                if idx > 0 and cleaned.startswith(" "):
                    cleaned = cleaned[1:]
                cleaned_segments.append(cleaned)
            else:
                cleaned_segments.append(seg)

    res = "".join(cleaned_segments)
    # Ensure a space after // or /// if non-empty and doesn't start with space
    if res and not res[0].isspace():
        res = " " + res
    return res


def process_csharp_source(source: str) -> Tuple[str, int]:
    """
    Process a C# file's text, returning (new_text, removed_id_count).
    Verifies that non-comment code is byte-for-byte identical.
    """
    tokens = tokenize_csharp(source)
    removed_count = 0
    out: List[str] = []

    for kind, text in tokens:
        if kind == "COMMENT":
            matches = PLAN_ID_RE.findall(text)
            if matches:
                removed_count += len(matches)
                cleaned = clean_comment_token(text)
                assert not PLAN_ID_RE.search(cleaned), f"Failed to strip all plan IDs from: {text!r} -> {cleaned!r}"
                out.append(cleaned)
            else:
                out.append(text)
        else:
            out.append(text)

    new_source = "".join(out)

    # Strict trivia verification: non-comment code must be byte-for-byte identical
    if strip_comments(source) != strip_comments(new_source):
        raise RuntimeError("Trivia verification failed: non-comment code changed!")

    # Also verify ProgramRatchetTests line check has 0 matches
    for line in re.split(r"\r\n|\r|\n", new_source):
        trimmed = line.lstrip()
        if trimmed.startswith("//") or trimmed.startswith("/*") or trimmed.startswith("*"):
            if RATCHET_PLAN_ID_RE.search(line):
                raise RuntimeError(f"Ratchet regex still matches comment line: {line!r}")

    return new_source, removed_count


def iter_cs_files(paths: List[str]) -> List[str]:
    result: List[str] = []
    for base in paths:
        if os.path.isfile(base):
            if base.endswith(".cs"):
                result.append(base)
            continue
        for root, dirs, files in os.walk(base):
            dirs[:] = sorted(d for d in dirs if d not in SKIPPED_DIRS)
            for f in sorted(files):
                if f.endswith(".cs"):
                    result.append(os.path.join(root, f))
    return sorted(result)


def main() -> int:
    parser = argparse.ArgumentParser(description="Strip plan IDs from C# comments.")
    parser.add_argument(
        "--project",
        action="append",
        dest="projects",
        help="Project directory or file path to process (default: src).",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Do not write modified files to disk.",
    )
    parser.add_argument(
        "--stats",
        action="store_true",
        help="Print per-project statistics.",
    )
    args = parser.parse_args()

    targets = args.projects if args.projects else ["src"]
    cs_files = iter_cs_files(targets)

    total_files_changed = 0
    total_ids_removed = 0
    stats_by_project = {}

    for path in cs_files:
        with open(path, "r", encoding="utf-8") as fh:
            original = fh.read()

        updated, count = process_csharp_source(original)
        rel = path.replace("\\", "/")
        parts = rel.split("/")
        proj = parts[1] if len(parts) > 1 and parts[0] in ("src", "modules") else parts[0]
        stats_by_project.setdefault(proj, {"files": 0, "ids": 0})

        if updated != original:
            total_files_changed += 1
            total_ids_removed += count
            stats_by_project[proj]["files"] += 1
            stats_by_project[proj]["ids"] += count
            if not args.dry_run:
                with open(path, "w", encoding="utf-8", newline="") as fh:
                    fh.write(updated)

    if args.stats:
        for proj, st in sorted(stats_by_project.items()):
            print(f"{proj}: {st['ids']} plan-ids stripped across {st['files']} files")
        print(f"Total: {total_ids_removed} plan-ids stripped across {total_files_changed} files (dry_run={args.dry_run})")

    return 0


if __name__ == "__main__":
    sys.exit(main())
