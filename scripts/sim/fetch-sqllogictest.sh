#!/usr/bin/env bash
# Fetches the pinned sqllogictest files named in the manifest (SEC-SIM-10, SEC-SIM-11).
#
# Usage:
#   fetch-sqllogictest.sh <manifest.json> <outdir>          download, verify, write to <outdir>
#   fetch-sqllogictest.sh --verify <manifest.json> <dir>    verify an existing directory only (no network)
#   fetch-sqllogictest.sh --check-url <manifest.json> <url> check that <url> is the pinned https URL of a manifest file
#   fetch-sqllogictest.sh --self-test
#
# Fail-closed: a non-https URL, a host other than the pinned host, a missing or empty file, a file over 20 MB or any sha256 mismatch exits
# non-zero and removes <outdir>. The files are data: they are never executed, never committed and never uploaded.
set -euo pipefail

MAX_BYTES=20971520
ALLOWED_HOSTS="raw.githubusercontent.com"

die() {
  echo "fetch-sqllogictest: $*" >&2
  exit 1
}

# Prints "commit<TAB>host<TAB>repo" then one "path<TAB>sha256" line per file; validates every field.
read_manifest() {
  python3 -I - "$1" <<'PY'
import json, re, sys
try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        data = json.load(handle)
except (OSError, ValueError) as ex:
    sys.exit(f"manifest unreadable: {ex}")
if not isinstance(data, dict):
    sys.exit("manifest must be an object")
commit, host, repo, files = (data.get(k) for k in ("commit", "host", "repo", "files"))
if not isinstance(commit, str) or not re.fullmatch(r"[0-9a-f]{40}", commit):
    sys.exit("manifest commit must be 40 lowercase hex")
if not isinstance(host, str) or not re.fullmatch(r"[a-z0-9.\-]{1,100}", host):
    sys.exit("manifest host is invalid")
if not isinstance(repo, str) or not re.fullmatch(r"[A-Za-z0-9_.\-]{1,100}/[A-Za-z0-9_.\-]{1,100}", repo):
    sys.exit("manifest repo is invalid")
if not isinstance(files, list) or not files:
    sys.exit("manifest has no files")
print(f"{commit}\t{host}\t{repo}")
for entry in files:
    path = entry.get("path") if isinstance(entry, dict) else None
    sha = entry.get("sha256") if isinstance(entry, dict) else None
    if not isinstance(path, str) or not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_./\-]{0,127}", path) or ".." in path:
        sys.exit("manifest file path is invalid")
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{64}", sha):
        sys.exit("manifest file needs a 64-hex sha256")
    print(f"{path}\t{sha}")
PY
}

host_allowed() {
  local host="$1" allowed
  for allowed in $ALLOWED_HOSTS; do
    [ "$host" = "$allowed" ] && return 0
  done
  return 1
}

# check_url <url> <commit> <host> <repo>: https only, pinned host, pinned commit in the path, no userinfo, no query.
check_url() {
  local url="$1" commit="$2" host="$3" repo="$4"
  case "$url" in
    https://*) ;;
    *) die "url is not https" ;;
  esac
  host_allowed "$host" || die "host $host is not allowed"
  case "$url" in
    "https://$host/$repo/$commit/"*) ;;
    *) die "url is not the pinned host, repo and commit" ;;
  esac
  case "$url" in
    *@* | *\?* | *\#* | *..*) die "url contains userinfo, a query, a fragment or a parent path" ;;
  esac
}

# verify_dir <manifest> <dir>: size and sha256 of every manifest file; removes <dir> on any failure.
verify_dir() {
  local manifest="$1" dir="$2" parsed path sha size actual
  parsed="$(read_manifest "$manifest")" || { rm -rf -- "$dir"; die "invalid manifest"; }
  while IFS=$'\t' read -r path sha; do
    if [ ! -f "$dir/$path" ]; then rm -rf -- "$dir"; die "missing file $path"; fi
    size="$(wc -c < "$dir/$path")"
    if [ "$size" -le 0 ] || [ "$size" -gt "$MAX_BYTES" ]; then rm -rf -- "$dir"; die "file $path is empty or over 20 MB"; fi
    actual="$(sha256sum -- "$dir/$path" | cut -d' ' -f1)"
    if [ "$actual" != "$sha" ]; then rm -rf -- "$dir"; die "sha256 mismatch for $path"; fi
  done <<< "$(printf '%s\n' "$parsed" | tail -n +2)"
}

fetch() {
  local manifest="$1" out="$2" parsed commit host repo path sha url tmp
  [ -n "$out" ] || die "output directory required"
  [ ! -e "$out" ] || die "output directory already exists: $out"
  parsed="$(read_manifest "$manifest")" || die "invalid manifest"
  IFS=$'\t' read -r commit host repo <<< "$(printf '%s\n' "$parsed" | head -n1)"
  tmp="$(mktemp -d "${out%/}.partial.XXXXXX")"
  trap 'rm -rf -- "$tmp"' EXIT
  while IFS=$'\t' read -r path sha; do
    url="https://$host/$repo/$commit/$path"
    check_url "$url" "$commit" "$host" "$repo"
    mkdir -p -- "$tmp/$(dirname -- "$path")"
    curl --proto '=https' --tlsv1.2 --fail --silent --show-error --max-redirs 0 --max-filesize "$MAX_BYTES" --max-time 120 \
      --output "$tmp/$path" -- "$url" || die "download failed for $path"
  done <<< "$(printf '%s\n' "$parsed" | tail -n +2)"
  verify_dir "$manifest" "$tmp"
  mv -- "$tmp" "$out"
  trap - EXIT
  echo "fetch-sqllogictest: verified $(printf '%s\n' "$parsed" | tail -n +2 | wc -l) files at commit $commit into $out"
}

self_test() {
  local manifest good_sha
  root="$(mktemp -d)"
  trap 'rm -rf -- "$root"' EXIT
  mkdir -p "$root/ok/test"
  printf 'statement ok\nSELECT 1\n' > "$root/ok/test/a.test"
  good_sha="$(sha256sum "$root/ok/test/a.test" | cut -d' ' -f1)"
  manifest="$root/m.json"
  printf '{"commit":"%s","host":"raw.githubusercontent.com","repo":"o/r","files":[{"path":"test/a.test","sha256":"%s"}]}' "$(printf 'a%.0s' $(seq 40))" "$good_sha" > "$manifest"
  ( verify_dir "$manifest" "$root/ok" ) || { echo "self-test: good directory rejected" >&2; return 1; }
  printf 'tampered' > "$root/ok/test/a.test"
  if ( verify_dir "$manifest" "$root/ok" ) 2>/dev/null; then echo "self-test: sha mismatch accepted" >&2; return 1; fi
  [ ! -e "$root/ok" ] || { echo "self-test: partial directory left behind" >&2; return 1; }
  local c; c="$(printf 'a%.0s' $(seq 40))"
  ( check_url "https://raw.githubusercontent.com/o/r/$c/test/a.test" "$c" raw.githubusercontent.com o/r ) || { echo "self-test: pinned url rejected" >&2; return 1; }
  if ( check_url "http://raw.githubusercontent.com/o/r/$c/test/a.test" "$c" raw.githubusercontent.com o/r ) 2>/dev/null; then echo "self-test: http accepted" >&2; return 1; fi
  if ( check_url "https://evil.example/o/r/$c/test/a.test" "$c" evil.example o/r ) 2>/dev/null; then echo "self-test: unpinned host accepted" >&2; return 1; fi
  trap - EXIT
  rm -rf -- "$root"
  echo "fetch-sqllogictest: self-test passed"
}

case "${1:-}" in
  --self-test) self_test ;;
  --verify) [ $# -eq 3 ] || die "usage: --verify <manifest> <dir>"; verify_dir "$2" "$3" ;;
  --check-url)
    [ $# -eq 3 ] || die "usage: --check-url <manifest> <url>"
    parsed="$(read_manifest "$2")" || die "invalid manifest"
    IFS=$'\t' read -r commit host repo <<< "$(printf '%s\n' "$parsed" | head -n1)"
    check_url "$3" "$commit" "$host" "$repo"
    ;;
  -*) die "unknown option $1" ;;
  *) [ $# -eq 2 ] || die "usage: fetch-sqllogictest.sh <manifest> <outdir>"; fetch "$1" "$2" ;;
esac
