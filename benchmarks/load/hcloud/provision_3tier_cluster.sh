#!/usr/bin/env bash
# ==============================================================================
# provision_3tier_cluster.sh
# Provisions the official 3-Tier Isolated Benchmark Topology on Hetzner Cloud
# ==============================================================================
set -euo pipefail

LOCATION="${LOCATION:-fsn1}"            # Falkenstein datacenter
NETWORK_NAME="${NETWORK_NAME:-bench-net}"
SSH_KEY_NAME="${SSH_KEY_NAME:-themu@DellLatitude}"
CLOUD_INIT_FILE="$(dirname "$0")/cloud-init.yaml"

# Sizing according to official Hasura / TechEmpower benchmark standards:
# - DB Server: CPX42 (8 vCPU, 16 GB RAM)
# - Gateway SUT: CCX33 (8 Dedicated AMD vCPUs, 32 GB RAM) -> No CPU-Stealing!
# - Client/k6: CPX32 (4 vCPU, 8 GB RAM)
TYPE_DB="${TYPE_DB:-cpx42}"
TYPE_GATEWAY="${TYPE_GATEWAY:-ccx33}"
TYPE_CLIENT="${TYPE_CLIENT:-cpx32}"

echo "================================================================================"
echo " Hetzner Cloud 3-Tier Benchmark Infrastructure Provisioning"
echo " Location:     ${LOCATION}"
echo " DB Host:      ${TYPE_DB}       (Postgres 16 / Chinook)"
echo " Gateway Host: ${TYPE_GATEWAY}  (Dedicated vCPUs for SUT)"
echo " Client Host:  ${TYPE_CLIENT}   (Isolated k6 Load Generator)"
echo "================================================================================"

if ! command -v hcloud &> /dev/null; then
    echo "ERROR: 'hcloud' CLI is required. Install via: apt install hcloud-cli / brew install hcloud"
    exit 1
fi

# 1. Create Private 10G Cloud Network
echo "1. Creating private network '${NETWORK_NAME}' (10.0.0.0/16)..."
if ! hcloud network describe "${NETWORK_NAME}" &>/dev/null; then
    hcloud network create --name "${NETWORK_NAME}" --ip-range "10.0.0.0/16"
    hcloud network add-subnet "${NETWORK_NAME}" --network-zone "eu-central" --type "cloud" --ip-range "10.0.1.0/24"
fi

# 2. Create Database Server
echo "2. Provisioning Database Host (bench-db: 10.0.1.10)..."
hcloud server create \
    --name "bench-db" \
    --type "${TYPE_DB}" \
    --image "ubuntu-24.04" \
    --location "${LOCATION}" \
    --network "${NETWORK_NAME}" \
    --ssh-key "${SSH_KEY_NAME}" \
    --user-data-from-file "${CLOUD_INIT_FILE}"

# 3. Create Gateway Server Under Test
echo "3. Provisioning Gateway SUT Host (bench-gateway: 10.0.1.20)..."
hcloud server create \
    --name "bench-gateway" \
    --type "${TYPE_GATEWAY}" \
    --image "ubuntu-24.04" \
    --location "${LOCATION}" \
    --network "${NETWORK_NAME}" \
    --ssh-key "${SSH_KEY_NAME}" \
    --user-data-from-file "${CLOUD_INIT_FILE}"

# 4. Create Client Load Generator Host
echo "4. Provisioning Client Load Generator Host (bench-client: 10.0.1.30)..."
hcloud server create \
    --name "bench-client" \
    --type "${TYPE_CLIENT}" \
    --image "ubuntu-24.04" \
    --location "${LOCATION}" \
    --network "${NETWORK_NAME}" \
    --ssh-key "${SSH_KEY_NAME}" \
    --user-data-from-file "${CLOUD_INIT_FILE}"

echo "Waiting for all servers to become active..."
hcloud server wait-for-status-running "bench-db"
hcloud server wait-for-status-running "bench-gateway"
hcloud server wait-for-status-running "bench-client"

IP_DB=$(hcloud server ip "bench-db")
IP_GW=$(hcloud server ip "bench-gateway")
IP_CLIENT=$(hcloud server ip "bench-client")

echo ""
echo "================================================================================"
echo " 3-Tier Cluster Successfully Provisioned!"
echo "================================================================================"
echo " Host               Public IP          Private IP (10G Network)"
echo " ---------------------------------------------------------------"
echo " Database (DB)      ${IP_DB}       10.0.1.10"
echo " Gateway (SUT)      ${IP_GW}       10.0.1.20"
echo " Load Gen (Client)  ${IP_CLIENT}   10.0.1.30"
echo "================================================================================"
echo ""
echo "Next Steps:"
echo " 1. Setup DB:      ssh root@${IP_DB} 'git clone ... && cd gql_bench && ./scripts/02_init_database.sh'"
echo " 2. Start Gateway: DATABASE_URL=postgres://postgres:REDACTED_HISTORICAL_BENCHMARK_SECRET@10.0.1.10:5432/postgres ./scripts/03_start_gateways.sh"
echo " 3. Run Load:      ssh root@${IP_CLIENT} 'TARGET_URL=http://10.0.1.20:5000/graphql ./scripts/04_run_benchmarks.sh'"
