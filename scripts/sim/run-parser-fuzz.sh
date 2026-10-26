#!/usr/bin/env bash
# Runs the parser fuzz (REQ-SIMTEST P3): SharpFuzz instrumentation of TrinoSqlEngine plus AFL++ against tests/Autheris.Tests.Fuzz.
# Called by .github/workflows/simulation-nightly.yml (jobs sim-parser-fuzz with 300 s and sim-parser-fuzz-weekly with 3600 s).
#
# Usage:
#   run-parser-fuzz.sh <seconds>      publish, instrument, fuzz for <seconds> (1 to 7200), replay AFL crashes and hangs
#   run-parser-fuzz.sh --check-paths  validate RUNNER_TEMP, the report dir and the derived paths, print them, exit 0 (no build, no AFL)
#   run-parser-fuzz.sh --summarize <afl-out-dir> <replay-exit-code> [<replay-output-file>]
#                                     print the failure summary and pack the AFL findings (no build, no AFL); the normal run calls the
#                                     same function after the replay. The exit code is the replay exit code (1 if packing fails on a clean run).
#
# Environment: RUNNER_TEMP (required, absolute, no spaces), AUTHERIS_SIM_SEED (1 to 10 digits, default UTC date),
# SIM_AFL_MEM_MB (1024 to 8192, default 4096; the afl-fuzz memory limit is always finite, never none), AUTHERIS_SIM_REPORT_DIR (drafts and
# findings pack; absolute, resolves under RUNNER_TEMP or GITHUB_WORKSPACE, never under .git; default $RUNNER_TEMP/fuzz-report).
#
# Safety (SEC-SIM-09, SEC-SIM-16, SEC-SIM-20): only the copy $RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll is instrumented and every path the
# script writes or deletes must resolve under $RUNNER_TEMP (symlinks are resolved first). The tool version is pinned. The instrumented
# output stays under $RUNNER_TEMP and is never uploaded; AFL findings are replayed with an uninstrumented copy. The workflow restores in locked mode before this script runs.
# The single exception to "write only under RUNNER_TEMP" (SEC-PF-01/02): the findings pack. AFL crashes and hangs (regular files only, never
# symlinks, at most 2000 files and 64 MB) are packed under $RUNNER_TEMP/fuzz-pack into afl-findings.tar.gz (original AFL names inside, since
# actions/upload-artifact rejects ':') plus afl-findings.manifest (sha256, size, name), and those two regular files are copied into the report
# dir. Extract with: tar --no-same-owner -xzf afl-findings.tar.gz. A sampler reads /proc/<pid>/status of the target to report the address-space peak.
#
# Exit codes: 0 clean; 1 a crash or hang that is not quarantined (or an AFL file that does not replay), or an infrastructure problem.
set -euo pipefail

SECONDS_REGEX='^[0-9]{1,5}$'
SEED_REGEX='^[0-9]{1,10}$'
MEM_REGEX='^[0-9]{4}$'
RUNNER_TEMP_REGEX='^/[A-Za-z0-9._/-]+$'

die() {
  echo "run-parser-fuzz: $*" >&2
  exit 1
}

usage() {
  die "usage: run-parser-fuzz.sh <seconds 1-7200> | --check-paths | --summarize <afl-out-dir> <replay-exit-code> [<replay-output-file>]"
}

MODE="${1:-}"
[[ -n "$MODE" ]] || usage
case "$MODE" in
  --check-paths) [[ $# -eq 1 ]] || usage ;;
  --summarize) [[ $# -ge 3 && $# -le 4 ]] || usage ;;
  *)
    [[ $# -eq 1 ]] || usage
    [[ "$MODE" =~ $SECONDS_REGEX ]] || usage
    (( 10#$MODE >= 1 && 10#$MODE <= 7200 )) || usage
    ;;
esac

[[ -n "${RUNNER_TEMP:-}" ]] || die "RUNNER_TEMP is not set"
[[ "$RUNNER_TEMP" =~ $RUNNER_TEMP_REGEX ]] || die "RUNNER_TEMP must be an absolute path without spaces or special characters"
[[ "$RUNNER_TEMP" != *..* ]] || die "RUNNER_TEMP must not contain '..'"
RT_REAL="$(realpath -e -- "$RUNNER_TEMP")" || die "RUNNER_TEMP does not exist"
[[ "$RT_REAL" != "/" && "$RT_REAL" == /*/* ]] || die "RUNNER_TEMP must not be a top-level directory"

# Succeeds only for a path that resolves (symlinks included) strictly below the real RUNNER_TEMP.
under_runner_temp() {
  local resolved
  resolved="$(realpath -m -- "$1")" || return 1
  [[ "$resolved" == "$RT_REAL"/* ]]
}

# SEC-PF-01: the report dir is absolute, resolves (symlinks included) under RUNNER_TEMP or GITHUB_WORKSPACE and never under .git.
REPORT_DIR="${AUTHERIS_SIM_REPORT_DIR:-$RUNNER_TEMP/fuzz-report}"
[[ "$REPORT_DIR" =~ $RUNNER_TEMP_REGEX && "$REPORT_DIR" != *..* ]] || die "report dir must be an absolute path without spaces, special characters or '..'"
REPORT_REAL="$(realpath -m -- "$REPORT_DIR")" || die "report dir cannot be resolved"
report_ok=0
[[ "$REPORT_REAL" == "$RT_REAL"/* ]] && report_ok=1
if [[ -n "${GITHUB_WORKSPACE:-}" && "$GITHUB_WORKSPACE" =~ $RUNNER_TEMP_REGEX && "$GITHUB_WORKSPACE" != *..* ]]; then
  WS_REAL="$(realpath -e -- "$GITHUB_WORKSPACE" 2>/dev/null)" || WS_REAL=""
  [[ -n "$WS_REAL" && "$WS_REAL" != "/" && "$REPORT_REAL" == "$WS_REAL"/* ]] && report_ok=1
fi
(( report_ok == 1 )) || die "refusing report dir outside RUNNER_TEMP and GITHUB_WORKSPACE: $REPORT_DIR"
[[ "$REPORT_REAL" != */.git && "$REPORT_REAL" != */.git/* ]] || die "refusing report dir under .git"

FUZZ_BIN="$RUNNER_TEMP/fuzz-bin"
TOOL_DIR="$RUNNER_TEMP/sf"
REPLAY_BIN="$RUNNER_TEMP/fuzz-replay"
AFL_IN="$RUNNER_TEMP/afl-in"
AFL_OUT="$RUNNER_TEMP/afl"
for p in "$FUZZ_BIN" "$TOOL_DIR" "$REPLAY_BIN" "$AFL_IN" "$AFL_OUT" "$FUZZ_BIN/TrinoSqlEngine.dll"; do
  under_runner_temp "$p" || die "refusing path outside RUNNER_TEMP: $p"
done

SEED="${AUTHERIS_SIM_SEED:-$(date -u +%Y%m%d)}"
[[ "$SEED" =~ $SEED_REGEX ]] || die "AUTHERIS_SIM_SEED must be 1 to 10 digits"
SIM_AFL_MEM_MB="${SIM_AFL_MEM_MB:-4096}"
[[ "$SIM_AFL_MEM_MB" =~ $MEM_REGEX ]] || die "SIM_AFL_MEM_MB must be 4 digits (1024 to 8192); the memory limit must be finite"
(( 10#$SIM_AFL_MEM_MB >= 1024 && 10#$SIM_AFL_MEM_MB <= 8192 )) || die "SIM_AFL_MEM_MB must be between 1024 and 8192"

if [[ "$MODE" == "--check-paths" ]]; then
  echo "report-dir=$REPORT_REAL fuzz-bin=$FUZZ_BIN replay-bin=$REPLAY_BIN tool-dir=$TOOL_DIR afl-in=$AFL_IN afl-out=$AFL_OUT seed=$SEED mem-mb=$SIM_AFL_MEM_MB"
  exit 0
fi

SAMPLER_DIR="$RUNNER_TEMP/fuzz-sampler"
PACK_DIR="$RUNNER_TEMP/fuzz-pack"
PACK_MAX_FILES=2000
PACK_MAX_BYTES=67108864
SAFE_NAME_REGEX='^[A-Za-z0-9._:,+=-]{1,200}$'
SAFE_TOKEN_REGEX='^[A-Za-z][A-Za-z]{0,23}$'

# Prints a file name only when it is plainly safe (no workflow command can start from it), else a sha256 prefix.
safe_name() {
  if [[ "$1" =~ $SAFE_NAME_REGEX && "$1" != *::* ]]; then
    printf '%s' "$1"
  else
    printf 'unsafe-name-%s' "$(printf '%s' "$1" | sha256sum | cut -c1-12)"
  fi
}

# SEC-PF-02: packs regular files crashes/<name> and hangs/<name> (never symlinks) into $PACK_DIR, then copies the two files to the report dir.
pack_findings() {
  local default_dir="$1/default" sub name count=0 total=0 size
  local -a list=()
  for sub in crashes hangs; do
    [[ -d "$default_dir/$sub" && ! -L "$default_dir/$sub" ]] || continue
    while IFS= read -r -d '' name; do
      [[ "$name" != "README.txt" && "$name" != .state && "$name" != */* ]] || continue
      list+=("$sub/$name")
      size="$(stat -c %s -- "$default_dir/$sub/$name")" || return 1
      count=$((count + 1))
      total=$((total + size))
    done < <(find "$default_dir/$sub" -maxdepth 1 -type f -printf '%f\0' | sort -z)
  done
  if (( count == 0 )); then
    echo "run-parser-fuzz: no AFL findings to pack"
    return 0
  fi
  if (( count > PACK_MAX_FILES || total > PACK_MAX_BYTES )); then
    echo "run-parser-fuzz: findings pack over bound: $count files, $total bytes (max $PACK_MAX_FILES files, $PACK_MAX_BYTES bytes)" >&2
    return 1
  fi
  rm -rf -- "$PACK_DIR"
  mkdir -p -- "$PACK_DIR" || return 1
  local entry hash
  for entry in "${list[@]}"; do
    hash="$(sha256sum -- "$default_dir/$entry" | cut -d' ' -f1)" || return 1
    printf '%s %s %s\n' "$hash" "$(stat -c %s -- "$default_dir/$entry")" "$(safe_name "${entry#*/}")" >> "$PACK_DIR/afl-findings.manifest"
  done
  printf '%s\0' "${list[@]}" > "$PACK_DIR/list.null"
  tar --null --owner=0 --group=0 --numeric-owner -C "$default_dir" -czf "$PACK_DIR/afl-findings.tar.gz" -T "$PACK_DIR/list.null" || return 1
  mkdir -p -- "$REPORT_REAL" || return 1
  local f
  for f in afl-findings.tar.gz afl-findings.manifest; do
    rm -f -- "$REPORT_REAL/$f" || return 1
    cp --no-dereference -- "$PACK_DIR/$f" "$REPORT_REAL/$f" || return 1
  done
  echo "run-parser-fuzz: packed $count findings ($total bytes) into $REPORT_REAL/afl-findings.tar.gz and afl-findings.manifest"
}

# SEC-PF-05: counts (validated integers) and static text only; no input bytes, no raw harness output.
print_summary() {
  local out_dir="$1" rc="$2" replay_file="$3" stats="$1/default/fuzzer_stats"
  local crashes hangs n first="" peak_vm=0 peak_rss=0 peak_thr=0 vm_mb rss_mb
  crashes="$(find "$out_dir/default/crashes" -maxdepth 1 -type f ! -name README.txt 2>/dev/null | wc -l)"
  hangs="$(find "$out_dir/default/hangs" -maxdepth 1 -type f ! -name README.txt 2>/dev/null | wc -l)"
  if [[ -f "$stats" ]]; then
    n="$(awk -F' *: *' '$1=="saved_crashes" && $2 ~ /^[0-9]+$/ {print $2}' "$stats")"; [[ "$n" =~ ^[0-9]+$ ]] && crashes="$n"
    n="$(awk -F' *: *' '$1=="saved_hangs" && $2 ~ /^[0-9]+$/ {print $2}' "$stats")"; [[ "$n" =~ ^[0-9]+$ ]] && hangs="$n"
  fi
  first="$(find "$out_dir/default/crashes" -maxdepth 1 -type f -name 'id:*' -printf '%f\n' 2>/dev/null | LC_ALL=C sort | head -n 1)"
  echo "run-parser-fuzz summary: replay exit code $rc, saved_crashes $crashes, saved_hangs $hangs"
  if [[ "$first" =~ ^id:[0-9]{6},.*time:([0-9]+),execs:([0-9]+) ]]; then
    echo "run-parser-fuzz summary: first crash $(safe_name "$first") at ${BASH_REMATCH[1]} ms after ${BASH_REMATCH[2]} execs"
  else
    echo "run-parser-fuzz summary: first crash none"
  fi
  SUMMARY_REPLAY="n/a"
  if [[ -n "$replay_file" && -f "$replay_file" ]]; then
    local token counts=""
    while read -r n token; do
      [[ "$n" =~ ^[0-9]+$ && "$token" =~ $SAFE_TOKEN_REGEX ]] || continue
      counts+="$token=$n "
    done < <(awk '{ t = $1; if (t !~ /^[A-Za-z][A-Za-z]*$/ || length(t) > 24) t = "Other"; c[t]++ } END { for (t in c) print c[t], t }' "$replay_file" | LC_ALL=C sort -k2 | head -n 12)
    SUMMARY_REPLAY="${counts% }"
  fi
  echo "run-parser-fuzz summary: replay statuses (first token per line): $SUMMARY_REPLAY"
  if [[ -f "$SAMPLER_DIR/peak" ]]; then
    read -r peak_vm peak_rss peak_thr < "$SAMPLER_DIR/peak" || true
    [[ "$peak_vm" =~ ^[0-9]{1,12}$ ]] || peak_vm=0
    [[ "$peak_rss" =~ ^[0-9]{1,12}$ ]] || peak_rss=0
    [[ "$peak_thr" =~ ^[0-9]{1,6}$ ]] || peak_thr=0
  fi
  vm_mb=$((peak_vm / 1024)); rss_mb=$((peak_rss / 1024))
  SUMMARY_PEAK="target address space peak ${vm_mb} MB of limit ${SIM_AFL_MEM_MB} MB (rss peak ${rss_mb} MB, threads peak ${peak_thr})"
  echo "run-parser-fuzz summary: $SUMMARY_PEAK"
  SUMMARY_COUNTS="saved_crashes $crashes, saved_hangs $hangs"
}

# SEC-PF-06: the replay exit code is captured by the caller and re-applied last; a summary or pack failure never turns a red run green.
finish_run() {
  local out_dir="$1" rc="$2" replay_file="${3:-}" pack_rc=0
  print_summary "$out_dir" "$rc" "$replay_file" || true
  pack_findings "$out_dir" || pack_rc=$?
  if (( rc != 0 )); then
    echo "::error::parser fuzz failed: replay exit code $rc, ${SUMMARY_COUNTS:-counts unavailable}, replay statuses ${SUMMARY_REPLAY:-n/a}, ${SUMMARY_PEAK:-no address space sample}"
    exit "$rc"
  fi
  if (( pack_rc != 0 )); then
    echo "::error::parser fuzz: packing the findings failed (infrastructure)"
    exit 1
  fi
  exit 0
}

if [[ "$MODE" == "--summarize" ]]; then
  OUT_DIR_ARG="$2"
  RC_ARG="$3"
  REPLAY_FILE_ARG="${4:-}"
  [[ "$RC_ARG" =~ ^[0-9]{1,3}$ ]] || die "replay exit code must be 0 to 255"
  [[ "$OUT_DIR_ARG" =~ $RUNNER_TEMP_REGEX ]] && under_runner_temp "$OUT_DIR_ARG" || die "refusing afl output dir outside RUNNER_TEMP"
  if [[ -n "$REPLAY_FILE_ARG" ]]; then
    [[ "$REPLAY_FILE_ARG" =~ $RUNNER_TEMP_REGEX ]] && under_runner_temp "$REPLAY_FILE_ARG" || die "refusing replay output file outside RUNNER_TEMP"
  fi
  finish_run "$(realpath -m -- "$OUT_DIR_ARG")" "$((10#$RC_ARG))" "$REPLAY_FILE_ARG"
fi

# SEC-PF-04: background sampler of the AFL target; reads only /proc/<pid>/status, writes only under RUNNER_TEMP, bounded by the fuzz seconds + 60.
SAMPLER_PID=""
sampler_loop() {
  set +e
  local SLEEP_PID=""
  trap 'kill "$SLEEP_PID" 2>/dev/null; exit 0' TERM
  local -a argv=()
  local deadline=$((SECONDS + 10#$1 + 60)) pid key value vm rss thr pvm=0 prss=0 pthr=0 p
  while (( SECONDS < deadline )); do
    for pid in $(pgrep -x dotnet 2>/dev/null); do
      [[ "$pid" =~ ^[0-9]{1,8}$ ]] || continue
      # Only the AFL target (exact argv), not publish leftovers such as MSBuild or compiler server nodes.
      mapfile -d '' -t argv < "/proc/$pid/cmdline" 2>/dev/null || continue
      [[ ${#argv[@]} -eq 3 && "${argv[0]}" == "dotnet" && "${argv[1]}" == "$FUZZ_BIN/Autheris.Tests.Fuzz.dll" && "${argv[2]}" == "--afl" ]] || continue
      vm=0; rss=0; thr=0
      while read -r key value _; do
        case "$key" in
          VmSize:) vm="$value" ;;
          VmRSS:) rss="$value" ;;
          Threads:) thr="$value" ;;
        esac
      done < "/proc/$pid/status" 2>/dev/null
      [[ "$vm" =~ ^[0-9]{1,12}$ && "$rss" =~ ^[0-9]{1,12}$ && "$thr" =~ ^[0-9]{1,6}$ ]] || continue
      (( vm > pvm )) && pvm=$vm
      (( rss > prss )) && prss=$rss
      (( thr > pthr )) && pthr=$thr
    done
    p="$SAMPLER_DIR/peak"
    echo "$pvm $prss $pthr" > "$p.tmp" 2>/dev/null && mv -f -- "$p.tmp" "$p" 2>/dev/null
    sleep 2 &
    SLEEP_PID=$!
    wait "$SLEEP_PID"
  done
}
stop_sampler() {
  if [[ -n "$SAMPLER_PID" ]]; then
    kill "$SAMPLER_PID" 2>/dev/null || true
    wait "$SAMPLER_PID" 2>/dev/null || true
    SAMPLER_PID=""
  fi
}
trap stop_sampler EXIT

cd "$(dirname "${BASH_SOURCE[0]}")/../.."
CORPUS="tests/Autheris.Tests.Fuzz/corpus"
[[ -d "$CORPUS" && -f "$CORPUS/sql.dict" ]] || die "seed corpus or dictionary missing"
command -v afl-fuzz >/dev/null || die "afl-fuzz is not installed"
echo "run-parser-fuzz: $( (afl-fuzz -h 2>&1 || true) | head -n 1 ), seed $SEED, ${MODE} s, memory limit ${SIM_AFL_MEM_MB} MB"

rm -rf -- "$FUZZ_BIN" "$REPLAY_BIN" "$AFL_IN" "$AFL_OUT"
dotnet publish tests/Autheris.Tests.Fuzz -c Release --no-restore -o "$FUZZ_BIN" --nologo -v q
under_runner_temp "$FUZZ_BIN/TrinoSqlEngine.dll" || die "published TrinoSqlEngine.dll resolves outside RUNNER_TEMP"
[[ -f "$FUZZ_BIN/TrinoSqlEngine.dll" && -f "$FUZZ_BIN/Autheris.Tests.Fuzz.dll" ]] || die "publish did not produce the harness"

# The published (not yet instrumented) harness must replay the committed seeds and findings before any instrumentation.
dotnet "$FUZZ_BIN/Autheris.Tests.Fuzz.dll" --replay "$CORPUS" tests/Autheris.Tests.Fuzz/findings

# An instrumented build cannot run outside AFL (no shared memory), so the replay of AFL findings uses this uninstrumented copy.
cp -a -- "$FUZZ_BIN" "$REPLAY_BIN"

rm -rf -- "$TOOL_DIR"
dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path "$TOOL_DIR"
# The only instrumented assembly: the copy under RUNNER_TEMP, never a production build.
"$TOOL_DIR/sharpfuzz" "$RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll"

mkdir -p "$AFL_IN"
cp -- "$CORPUS"/*.sql "$AFL_IN"/
[[ -n "$(ls -A "$AFL_IN")" ]] || die "no seeds to fuzz"

# The harness is a .NET process: AFL cannot see its binary, and a finite address-space limit needs ICU off, a bounded GC heap
# and no tiered JIT (otherwise the runtime reserves more virtual memory than -m allows).
export AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1 AFL_SKIP_BIN_CHECK=1
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_GCHeapHardLimit=0x20000000 DOTNET_TieredCompilation=0 DOTNET_TieredPGO=0 DOTNET_gcServer=0
# Address-space budget: afl-fuzz -m is RLIMIT_AS; without these the runtime sizes its GC region range and executable range to that limit and
# glibc adds a 64 MB arena per contending thread, which exhausts the 4 GiB at ~150 MB RSS and fails allocations at random.
export MALLOC_ARENA_MAX=2 DOTNET_GCRegionRange=0x40000000
rm -rf -- "$SAMPLER_DIR"
mkdir -p -- "$SAMPLER_DIR"
sampler_loop "$MODE" &
SAMPLER_PID=$!
set +e
afl-fuzz -i "$AFL_IN" -x "$CORPUS/sql.dict" -o "$AFL_OUT" -t 5000 -G 65536 -m "$SIM_AFL_MEM_MB" -s "$SEED" -V "$MODE" -- dotnet "$FUZZ_BIN/Autheris.Tests.Fuzz.dll" --afl
AFL_RC=$?
set -e
# The address-space settings apply to the afl-fuzz target only; the findings replay below must not inherit them.
unset MALLOC_ARENA_MAX DOTNET_GCRegionRange
echo "run-parser-fuzz: afl-fuzz exit code $AFL_RC"
stop_sampler

# A run that executed nothing is a failure (never a silent pass).
STATS="$AFL_OUT/default/fuzzer_stats"
[[ -f "$STATS" ]] || die "afl-fuzz produced no fuzzer_stats (exit code $AFL_RC)"
grep -Eq '^execs_done +: +[1-9]' "$STATS" || die "afl-fuzz executed no inputs (exit code $AFL_RC)"
# Coverage edges prove that the instrumentation reaches AFL (an uninstrumented target would report none).
grep -Eq '^edges_found +: +[1-9]' "$STATS" || die "afl-fuzz saw no coverage edges: the instrumentation is not effective"
grep -E '^(execs_done|execs_per_sec|saved_crashes|saved_hangs|corpus_count|edges_found|total_edges) ' "$STATS" || true

mkdir -p "$AFL_OUT/default/crashes" "$AFL_OUT/default/hangs"
DRAFTS="${AUTHERIS_SIM_REPORT_DIR:-$RUNNER_TEMP/fuzz-report}/fuzz-drafts"
REPLAY_LOG="$RUNNER_TEMP/fuzz-replay.log"
set +e
dotnet "$REPLAY_BIN/Autheris.Tests.Fuzz.dll" --afl-findings "$AFL_OUT/default/crashes" "$AFL_OUT/default/hangs" --drafts "$DRAFTS" 2>&1 | tee "$REPLAY_LOG"
REPLAY_RC="${PIPESTATUS[0]}"
set -e
finish_run "$AFL_OUT" "$REPLAY_RC" "$REPLAY_LOG"
