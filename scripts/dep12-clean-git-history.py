#!/usr/bin/env python3
"""
DEP-12 Git History Sanitization Tool
Remediates historical benchmark secrets from the Git repository history using git-filter-repo.

Targeted Historical Secrets:
  - REDACTED_HISTORICAL_BENCHMARK_SECRET (HMAC master key in deploy/podman-compose.yaml & configs)
  - REDACTED_HISTORICAL_BENCHMARK_SECRET                        (MSSQL SA password in benchmark configurations)
  - REDACTED_HISTORICAL_BENCHMARK_SECRET                                 (PostgreSQL benchmark container password)
  - REDACTED_HISTORICAL_BENCHMARK_SECRET                        (PostgreSQL load benchmark password)
  - X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET        (Hardcoded Hasura admin secret in load benchmark script)

Usage:
  python scripts/dep12-clean-git-history.py --scan
  python scripts/dep12-clean-git-history.py --execute [--force]
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile

def load_replacements(secrets_file=None):
    """
    Loads target secrets to sanitize from environment variables or an external file.
    Does not store any raw or base64-encoded secrets in the codebase (SEC-CRYPTO-02).
    """
    replacements = {}

    # 1. From external file if specified
    filepath = secrets_file or os.environ.get("DEP12_SECRETS_FILE")
    if filepath and os.path.exists(filepath):
        with open(filepath, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    if "==>" in line:
                        k, v = line.split("==>", 1)
                        replacements[k.strip()] = v.strip()
                    else:
                        replacements[line] = "REDACTED_HISTORICAL_BENCHMARK_SECRET"

    # 2. From JSON environment variable
    json_env = os.environ.get("DEP12_REPLACEMENTS_JSON")
    if json_env:
        try:
            parsed = json.loads(json_env)
            if isinstance(parsed, dict):
                replacements.update(parsed)
            elif isinstance(parsed, list):
                for item in parsed:
                    replacements[str(item)] = "REDACTED_HISTORICAL_BENCHMARK_SECRET"
        except json.JSONDecodeError as ex:
            print(f"WARNING: Invalid JSON in DEP12_REPLACEMENTS_JSON: {ex}", file=sys.stderr)

    # 3. From comma/newline-separated environment variable
    raw_env = os.environ.get("DEP12_TARGET_SECRETS")
    if raw_env:
        for s in raw_env.replace("\r\n", "\n").replace(",", "\n").split("\n"):
            s = s.strip()
            if s:
                replacements[s] = "REDACTED_HISTORICAL_BENCHMARK_SECRET"

    return replacements

def run_cmd(cmd, cwd=None, capture=True):
    res = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE if capture else None,
                         stderr=subprocess.PIPE if capture else None, text=True)
    return res

def scan_history(replacements):
    if not replacements:
        print("[*] No secrets supplied for scanning. Provide secrets via --secrets-file <path> or DEP12_TARGET_SECRETS environment variable.")
        return False

    print("[*] Scanning Git history for specified secrets (DEP-12)...")
    found_any = False
    for secret in replacements.keys():
        cmd = ["git", "log", "-S", secret, "--oneline"]
        res = run_cmd(cmd)
        lines = [line.strip() for line in res.stdout.strip().splitlines() if line.strip()]
        if lines:
            found_any = True
            print(f"  [!] Found target secret in {len(lines)} commit(s):")
            for line in lines[:5]:
                print(f"      - {line}")
            if len(lines) > 5:
                print(f"      - ... and {len(lines) - 5} more")
        else:
            print(f"  [OK] Target secret not found in commit history.")
    return found_any

def execute_sanitization(replacements, force=False):
    if not replacements:
        print("ERROR: No secrets supplied for history rewriting.")
        print("Provide secrets via --secrets-file <path> or DEP12_TARGET_SECRETS environment variable.")
        sys.exit(1)

    print("[*] Preparing git-filter-repo execution...")
    try:
        res = run_cmd([sys.executable, "-m", "git_filter_repo", "--version"])
        if res.returncode != 0:
            print("ERROR: git-filter-repo is not installed in the current Python environment.")
            print("Install it via: pip install git-filter-repo")
            sys.exit(1)
    except Exception as e:
        print(f"ERROR checking git-filter-repo: {e}")
        sys.exit(1)

    status = run_cmd(["git", "status", "--porcelain"])
    if status.stdout.strip() and not force:
        print("ERROR: Working directory contains uncommitted changes.")
        print("Commit or stash changes before rewriting history, or pass --force (use with caution).")
        sys.exit(1)

    with tempfile.NamedTemporaryFile("w", delete=False, suffix="-filter-repo-replace.txt") as tmp:
        for secret, replacement in replacements.items():
            tmp.write(f"literal:{secret}==>{replacement}\n")
        tmp_path = tmp.name

    try:
        cmd = [sys.executable, "-m", "git_filter_repo", "--replace-text", tmp_path]
        if force:
            cmd.append("--force")
        print(f"[*] Executing: {' '.join(cmd)}")
        res = subprocess.run(cmd)
        if res.returncode != 0:
            print(f"ERROR: git-filter-repo failed with exit code {res.returncode}")
            sys.exit(res.returncode)
        print("[+] Sanitization complete!")
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)

    print("\n[*] Verifying post-rewrite Git history...")
    scan_history(replacements)
    print("\n[!] NOTE: History rewrite changes commit hashes.")
    print("    To synchronize remotes, run:")
    print("      git push origin --force --all")
    print("      git push origin --force --tags")
    print("    Consult docs/security/dep-12-git-history-cleanup.md for full post-cleanup instructions.")

def main():
    parser = argparse.ArgumentParser(description="DEP-12 Git History Sanitization Tool")
    parser.add_argument("--scan", action="store_true", help="Scan Git history for secrets without modifying")
    parser.add_argument("--execute", action="store_true", help="Rewrite history using git-filter-repo")
    parser.add_argument("--force", action="store_true", help="Pass --force to git-filter-repo and skip dirty check")
    parser.add_argument("--secrets-file", default=None, help="Path to file containing secrets to sanitize")

    args = parser.parse_args()
    replacements = load_replacements(args.secrets_file)

    if args.execute:
        execute_sanitization(replacements, force=args.force)
    elif args.scan:
        has_secrets = scan_history(replacements)
        sys.exit(1 if has_secrets else 0)
    else:
        parser.print_help()

if __name__ == "__main__":
    main()
