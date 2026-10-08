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
import os
import subprocess
import sys
import tempfile

REPLACEMENTS = {
    "REDACTED_HISTORICAL_BENCHMARK_SECRET": "REDACTED_HISTORICAL_BENCHMARK_SECRET",
    "REDACTED_HISTORICAL_BENCHMARK_SECRET": "REDACTED_HISTORICAL_BENCHMARK_SECRET",
    "REDACTED_HISTORICAL_BENCHMARK_SECRET": "REDACTED_HISTORICAL_BENCHMARK_SECRET",
    "REDACTED_HISTORICAL_BENCHMARK_SECRET": "REDACTED_HISTORICAL_BENCHMARK_SECRET",
    "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET": "X-Hasura-Admin-Secret: REDACTED_HISTORICAL_BENCHMARK_SECRET",
}

def run_cmd(cmd, cwd=None, capture=True):
    res = subprocess.run(cmd, cwd=cwd, stdout=subprocess.PIPE if capture else None,
                         stderr=subprocess.PIPE if capture else None, text=True)
    return res

def scan_history():
    print("[*] Scanning Git history for known historical benchmark secrets (DEP-12)...")
    found_any = False
    for secret in REPLACEMENTS.keys():
        cmd = ["git", "log", "-S", secret, "--oneline"]
        res = run_cmd(cmd)
        lines = [line.strip() for line in res.stdout.strip().splitlines() if line.strip()]
        if lines:
            found_any = True
            print(f"  [!] Found '{secret}' in {len(lines)} commit(s):")
            for line in lines[:5]:
                print(f"      - {line}")
            if len(lines) > 5:
                print(f"      - ... and {len(lines) - 5} more")
        else:
            print(f"  [OK] '{secret}' not found in commit history.")
    return found_any

def execute_sanitization(force=False):
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
        for secret, replacement in REPLACEMENTS.items():
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
    scan_history()
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

    args = parser.parse_args()

    if args.execute:
        execute_sanitization(force=args.force)
    elif args.scan:
        has_secrets = scan_history()
        sys.exit(1 if has_secrets else 0)
    else:
        parser.print_help()

if __name__ == "__main__":
    main()
