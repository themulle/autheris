#!/usr/bin/env bash
# Runs the parser fuzz (REQ-SIMTEST P3): SharpFuzz instrumentation of TrinoSqlEngine plus AFL++ against tests/Autheris.Tests.Fuzz.
# Called by .github/workflows/simulation-nightly.yml (jobs sim-parser-fuzz with 300 s and sim-parser-fuzz-weekly with 3600 s).
#
# Usage:
#   run-parser-fuzz.sh <seconds>      publish, instrument, fuzz for <seconds> (1 to 7200), replay AFL crashes and hangs
#   run-parser-fuzz.sh --check-paths  validate RUNNER_TEMP and the derived paths, print them, exit 0 (no build, no AFL)
#
# Environment: RUNNER_TEMP (required, absolute, no spaces), AUTHERIS_SIM_SEED (1 to 10 digits, default UTC date),
# SIM_AFL_MEM_MB (1024 to 8192, default 4096; the afl-fuzz memory limit is always finite, never none), AUTHERIS_SIM_REPORT_DIR (drafts).
#
# Safety (SEC-SIM-09, SEC-SIM-16, SEC-SIM-20): only the copy $RUNNER_TEMP/fuzz-bin/TrinoSqlEngine.dll is instrumented and every path the
# script writes or deletes must resolve under $RUNNER_TEMP (symlinks are resolved first). The tool version is pinned. The instrumented
# output stays under $RUNNER_TEMP and is never uploaded; AFL findings are replayed with an uninstrumented copy. The workflow restores in locked mode before this script runs.
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
  die "usage: run-parser-fuzz.sh <seconds 1-7200> | --check-paths"
}

MODE="${1:-}"
[[ -n "$MODE" && $# -eq 1 ]] || usage
if [[ "$MODE" != "--check-paths" ]]; then
  [[ "$MODE" =~ $SECONDS_REGEX ]] || usage
  (( 10#$MODE >= 1 && 10#$MODE <= 7200 )) || usage
fi

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
  echo "fuzz-bin=$FUZZ_BIN replay-bin=$REPLAY_BIN tool-dir=$TOOL_DIR afl-in=$AFL_IN afl-out=$AFL_OUT seed=$SEED mem-mb=$SIM_AFL_MEM_MB"
  exit 0
fi

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
set +e
afl-fuzz -i "$AFL_IN" -x "$CORPUS/sql.dict" -o "$AFL_OUT" -t 5000 -G 65536 -m "$SIM_AFL_MEM_MB" -s "$SEED" -V "$MODE" -- dotnet "$FUZZ_BIN/Autheris.Tests.Fuzz.dll" --afl
AFL_RC=$?
set -e
echo "run-parser-fuzz: afl-fuzz exit code $AFL_RC"

# A run that executed nothing is a failure (never a silent pass).
STATS="$AFL_OUT/default/fuzzer_stats"
[[ -f "$STATS" ]] || die "afl-fuzz produced no fuzzer_stats (exit code $AFL_RC)"
grep -Eq '^execs_done +: +[1-9]' "$STATS" || die "afl-fuzz executed no inputs (exit code $AFL_RC)"
# Coverage edges prove that the instrumentation reaches AFL (an uninstrumented target would report none).
grep -Eq '^edges_found +: +[1-9]' "$STATS" || die "afl-fuzz saw no coverage edges: the instrumentation is not effective"
grep -E '^(execs_done|execs_per_sec|saved_crashes|saved_hangs|corpus_count|edges_found|total_edges) ' "$STATS" || true

mkdir -p "$AFL_OUT/default/crashes" "$AFL_OUT/default/hangs"
DRAFTS="${AUTHERIS_SIM_REPORT_DIR:-$RUNNER_TEMP/fuzz-report}/fuzz-drafts"
dotnet "$REPLAY_BIN/Autheris.Tests.Fuzz.dll" --afl-findings "$AFL_OUT/default/crashes" "$AFL_OUT/default/hangs" --drafts "$DRAFTS"
