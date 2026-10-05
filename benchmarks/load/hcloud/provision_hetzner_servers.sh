#!/usr/bin/env bash
# Provision Hetzner Cloud benchmark server using hcloud CLI
set -euo pipefail

SERVER_NAME="${SERVER_NAME:-gql-bench-runner}"
SERVER_TYPE="${SERVER_TYPE:-cpx42}" # 8 AMD vCPUs, 16 GB RAM
LOCATION="${LOCATION:-fsn1}"        # Falkenstein
IMAGE="${IMAGE:-ubuntu-24.04}"
SSH_KEY_NAME="${SSH_KEY_NAME:-themu@DellLatitude}"
CLOUD_INIT_FILE="$(dirname "$0")/cloud-init.yaml"

echo "=== Hetzner Cloud Benchmark Server Provisioning ==="
echo "Server Name: ${SERVER_NAME}"
echo "Server Type: ${SERVER_TYPE}"
echo "Location:    ${LOCATION}"
echo "Image:       ${IMAGE}"

if ! command -v hcloud &> /dev/null; then
    echo "ERROR: hcloud CLI is not installed."
    echo "Install it via: https://github.com/hetznercloud/cli"
    echo "Or use brew/apt: apt install -y hcloud-cli"
    exit 1
fi

echo "Creating server on Hetzner Cloud..."
hcloud server create \
  --name "${SERVER_NAME}" \
  --type "${SERVER_TYPE}" \
  --image "${IMAGE}" \
  --location "${LOCATION}" \
  --ssh-key "${SSH_KEY_NAME}" \
  --user-data-from-file "${CLOUD_INIT_FILE}"

echo "Waiting for server to become active..."
hcloud server wait-for-status-running "${SERVER_NAME}"

SERVER_IP=$(hcloud server ip "${SERVER_NAME}")
echo "Server created successfully! IP: ${SERVER_IP}"
echo ""
echo "Next step: copy benchmark workspace to server:"
echo "  scp -r /root/lis-git/gql root@${SERVER_IP}:/root/"
echo "  ssh root@${SERVER_IP}"
