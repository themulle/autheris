#!/usr/bin/env bash
# ==============================================================================
# run_single_host.sh
# End-to-End Benchmark Runner on a Single Host (with optional CPU core pinning)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCH_DIR="${SCRIPT_DIR}"

# Default parameters
DURATION="${DURATION:-30s}"
RPS="${RPS:-1000}"
VUS="${VUS:-50}"
SCENARIO="${SCENARIO:-rps}" # 'rps', 'vus', 'ramp'
SKIP_SETUP=false
SKIP_TEARDOWN=false

print_usage() {
    cat << EOF
Usage: ./run_single_host.sh [OPTIONS]

Options:
  --quick            Quick smoke test (10s duration, 500 RPS)
  --full             Full standard benchmark (30s duration, 1000 RPS, default)
  --deep             Deep statistical benchmark (60s duration, 2500 RPS)
  --ramp             Multi-stage load ramp test (100 -> 5000 RPS)
  --duration <time>  Custom test duration (e.g. 15s, 45s, 2m)
  --rps <number>     Target requests per second (e.g. 500, 2000, 5000)
  --vus <number>     Target virtual users / concurrency (default: 50)
  --skip-setup       Skip host package installation & kernel tuning
  --teardown         Shut down docker containers when finished
  -h, --help         Show this help message

Examples:
  ./run_single_host.sh --quick
  ./run_single_host.sh --full
  ./run_single_host.sh --duration 45s --rps 2000
EOF
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case "$1" in
        --quick)
            DURATION="10s"
            RPS="500"
            shift
            ;;
        --full)
            DURATION="30s"
            RPS="1000"
            shift
            ;;
        --deep)
            DURATION="60s"
            RPS="2500"
            shift
            ;;
        --ramp)
            SCENARIO="ramp"
            DURATION="70s"
            shift
            ;;
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --rps)
            RPS="$2"
            shift 2
            ;;
        --vus)
            VUS="$2"
            shift 2
            ;;
        --skip-setup)
            SKIP_SETUP=true
            shift
            ;;
        --teardown)
            SKIP_TEARDOWN=true
            shift
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            print_usage
            exit 1
            ;;
    esac
done

echo "================================================================================"
echo " Starting GraphQL Benchmark (Single-Host Mode)"
echo "================================================================================"
TOTAL_CORES=$(nproc)
TOTAL_RAM_GB=$(free -g | awk '/^Mem:/{print $2}')
echo " Host Specs:     ${TOTAL_CORES} CPU Cores, ~${TOTAL_RAM_GB} GB RAM"
echo " Duration:       ${DURATION} per test"
echo " Strategy:       ${SCENARIO} (Target: ${RPS} RPS, ${VUS} VUs)"
echo "================================================================================"

# Hardware advisory check
if [ "${TOTAL_CORES}" -lt 4 ]; then
    echo " [WARN] Host has only ${TOTAL_CORES} CPU cores. Gateways and load generator will compete heavily."
elif [ "${TOTAL_CORES}" -ge 8 ]; then
    echo " [INFO] Host has ${TOTAL_CORES} cores. Sufficient resources for single-host benchmarking."
fi

# Step 1: Host setup & tuning
if [ "$SKIP_SETUP" = false ]; then
    echo ""
    echo ">>> Step 1/5: Host Setup & Kernel Tuning..."
    "${BENCH_DIR}/scripts/01_hetzner_setup.sh"
else
    echo ""
    echo ">>> Step 1/5: Skipping Host Setup (--skip-setup)."
fi

# Step 2: Database initialization
echo ""
echo ">>> Step 2/5: Initializing PostgreSQL & Chinook dataset..."
"${BENCH_DIR}/scripts/02_init_database.sh"

# Step 3: Gateways start & warmup
echo ""
echo ">>> Step 3/5: Launching & warming up GraphQL Gateways..."
"${BENCH_DIR}/scripts/03_start_gateways.sh"

# Step 4: Execute benchmarks
echo ""
echo ">>> Step 4/5: Running Benchmark Suite..."
DURATION="${DURATION}" \
RPS="${RPS}" \
VUS="${VUS}" \
SCENARIO="${SCENARIO}" \
"${BENCH_DIR}/scripts/04_run_benchmarks.sh"

# Step 5: Teardown (if requested)
if [ "$SKIP_TEARDOWN" = true ]; then
    echo ""
    echo ">>> Step 5/5: Stopping Docker containers..."
    docker compose -f "${BENCH_DIR}/docker/docker-compose.yml" down
else
    echo ""
    echo ">>> Step 5/5: Containers left running for inspection (use --teardown to stop)."
fi

echo ""
echo "================================================================================"
echo " Single-Host Benchmark Run Complete!"
echo " Check 'results/' for full JSON metrics, CSV and Markdown reports."
echo "================================================================================"
