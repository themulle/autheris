#!/usr/bin/env bash
# ==============================================================================
# run_multi_host.sh
# End-to-End Orchestrator for the 3-Tier Isolated Benchmark on Hetzner Cloud
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BENCH_DIR="${SCRIPT_DIR}"
REPO_DIR="$(cd "${BENCH_DIR}/../.." && pwd)"
HCLOUD_DIR="${BENCH_DIR}/hcloud"
RESULTS_DIR="${BENCH_DIR}/results"

DURATION="${DURATION:-30s}"
RPS="${RPS:-1000}"
VUS="${VUS:-50}"
SCENARIO="${SCENARIO:-rps}"
AUTO_DESTROY=false
SKIP_PROVISION=false

print_usage() {
    cat << EOF
Usage: ./run_multi_host.sh [OPTIONS]

Orchestrates the 3-Tier isolated benchmark across 3 Hetzner Cloud servers:
  1. bench-db      (Postgres 16 Chinook)
  2. bench-gateway (SUT: GqlGateway / Hasura / Apollo / PostGraphile on Dedicated vCPU)
  3. bench-client  (Isolated k6 Load Generator)

Options:
  --quick            Quick run (10s duration, 500 RPS)
  --full             Standard benchmark (30s duration, 1000 RPS, default)
  --deep             Deep statistical benchmark (60s duration, 2500 RPS)
  --duration <time>  Duration per test (default: 30s)
  --rps <number>     Target RPS (default: 1000)
  --skip-provision   Skip hcloud creation if servers are already running
  --auto-destroy     Automatically delete servers after benchmark completes
  -h, --help         Show this help message

Requirements:
  - Hetzner Cloud API token (HCLOUD_TOKEN environment variable)
  - 'hcloud' CLI installed
  - SSH key added to Hetzner Cloud

Example:
  export HCLOUD_TOKEN="your_hetzner_token"
  ./run_multi_host.sh --full
EOF
}

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
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --rps)
            RPS="$2"
            shift 2
            ;;
        --skip-provision)
            SKIP_PROVISION=true
            shift
            ;;
        --auto-destroy)
            AUTO_DESTROY=true
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

if ! command -v hcloud &> /dev/null; then
    echo "ERROR: 'hcloud' CLI not found. Please install it (e.g. apt install hcloud-cli)."
    exit 1
fi

if [ -z "${HCLOUD_TOKEN:-}" ]; then
    echo "ERROR: HCLOUD_TOKEN environment variable is not set."
    echo "Export your token via: export HCLOUD_TOKEN='<your_token>'"
    exit 1
fi

mkdir -p "${RESULTS_DIR}"
TIMESTAMP=$(date +"%Y%m%d_%H%M%S")

# Per-run random credentials (written to the untracked .bench-secrets.env and shipped to the hosts).
. "${BENCH_DIR}/scripts/lib_secrets.sh"

echo "================================================================================"
echo " 3-Tier Multi-Host GraphQL Benchmark (Hetzner Cloud)"
echo " Duration:    ${DURATION}"
echo " Target RPS:  ${RPS}"
echo "================================================================================"

# Step 1: Provision 3-Tier Cluster (if not skipped)
if [ "$SKIP_PROVISION" = false ]; then
    echo ""
    echo ">>> Step 1/5: Provisioning 3-Tier Hetzner Cloud Infrastructure..."
    "${HCLOUD_DIR}/provision_3tier_cluster.sh"
else
    echo ""
    echo ">>> Step 1/5: Skipping Server Provisioning (--skip-provision)."
fi

# Fetch Public IPs
IP_DB=$(hcloud server ip "bench-db")
IP_GW=$(hcloud server ip "bench-gateway")
IP_CLIENT=$(hcloud server ip "bench-client")

echo ">>> Cluster IPs:"
echo "    DB Host:       ${IP_DB} (Private: 10.0.1.10)"
echo "    Gateway Host:  ${IP_GW} (Private: 10.0.1.20)"
echo "    Client Host:   ${IP_CLIENT} (Private: 10.0.1.30)"

# Trust-on-first-use with a per-run known_hosts file (no blanket host key bypass).
KNOWN_HOSTS="${RESULTS_DIR}/known_hosts_${TIMESTAMP}"
SSH_OPTS="-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=${KNOWN_HOSTS} -o LogLevel=ERROR"

# Step 2: Deploy & Initialize Database
echo ""
echo ">>> Step 2/5: Initializing Database on bench-db (${IP_DB})..."
ssh ${SSH_OPTS} "root@${IP_DB}" "mkdir -p /root/autheris/benchmarks/load"
rsync -avz -e "ssh ${SSH_OPTS}" --exclude '.git' --exclude 'results' "${BENCH_DIR}/" "root@${IP_DB}:/root/autheris/benchmarks/load/"
ssh ${SSH_OPTS} "root@${IP_DB}" "cd /root/autheris/benchmarks/load && DB_BIND_IP=10.0.1.10 ./scripts/02_init_database.sh"

# Step 3: Deploy & Start Gateways
echo ""
echo ">>> Step 3/5: Deploying & Starting Gateways on bench-gateway (${IP_GW})..."
ssh ${SSH_OPTS} "root@${IP_GW}" "mkdir -p /root/autheris"
rsync -avz -e "ssh ${SSH_OPTS}" --exclude '.git' --exclude 'bin' --exclude 'obj' --exclude 'results' "${REPO_DIR}/" "root@${IP_GW}:/root/autheris/"
# Configure Gateways to connect to Postgres over the 10G private network (10.0.1.10)
ssh ${SSH_OPTS} "root@${IP_GW}" "
    cd /root/autheris/benchmarks/load
    export DB_HOST='10.0.1.10' GATEWAY_BIND_IP='10.0.1.20'
    ./scripts/03_start_gateways.sh
"

# Step 4: Deploy & Run Load Tests from Client Host
echo ""
echo ">>> Step 4/5: Running Isolated Benchmark from bench-client (${IP_CLIENT})..."
ssh ${SSH_OPTS} "root@${IP_CLIENT}" "mkdir -p /root/autheris/benchmarks/load"
rsync -avz -e "ssh ${SSH_OPTS}" --exclude '.git' --exclude 'results' "${BENCH_DIR}/" "root@${IP_CLIENT}:/root/autheris/benchmarks/load/"

# Execute load against Gateway host via 10G private network (10.0.1.20)
ssh ${SSH_OPTS} "root@${IP_CLIENT}" "
    cd /root/autheris/benchmarks/load
    DURATION='${DURATION}' \
    RPS='${RPS}' \
    VUS='${VUS}' \
    SCENARIO='${SCENARIO}' \
    GATEWAY_HOST='10.0.1.20' \
    ./scripts/04_run_benchmarks.sh
"

# Step 5: Download Results and Generate Report
echo ""
echo ">>> Step 5/5: Collecting Results from bench-client..."
rsync -avz -e "ssh ${SSH_OPTS}" "root@${IP_CLIENT}:/root/autheris/benchmarks/load/results/" "${RESULTS_DIR}/"

python3 "${BENCH_DIR}/scripts/05_analyze_results.py"

# Cleanup (if requested)
if [ "$AUTO_DESTROY" = true ]; then
    echo ""
    echo ">>> Destroying 3-Tier Hetzner Cloud Servers (--auto-destroy)..."
    hcloud server delete "bench-db" || true
    hcloud server delete "bench-gateway" || true
    hcloud server delete "bench-client" || true
    hcloud network delete "bench-net" || true
    echo "All cloud servers deleted."
else
    echo ""
    echo ">>> Servers are still running. To delete them when finished, run:"
    echo "    hcloud server delete bench-db bench-gateway bench-client && hcloud network delete bench-net"
fi

echo ""
echo "================================================================================"
echo " 3-Tier Isolated Multi-Host Benchmark Run Complete!"
echo " Results available in '${RESULTS_DIR}'"
echo "================================================================================"
