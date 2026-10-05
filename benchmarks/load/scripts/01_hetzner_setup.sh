#!/usr/bin/env bash
# ==============================================================================
# 01_hetzner_setup.sh
# Host provisioning & high-throughput Linux tuning for Hetzner Cloud / Dedicated
# ==============================================================================
set -euo pipefail

echo "================================================================================"
echo " [Step 1/5] Hetzner Benchmark Host Setup & Performance Tuning"
echo "================================================================================"

# 1. System packages
echo "Updating apt repositories and installing core packages..."
apt-get update -y
apt-get install -y --no-install-recommends \
    apt-transport-https \
    ca-certificates \
    curl \
    gnupg \
    lsb-release \
    git \
    htop \
    sysstat \
    jq \
    build-essential \
    python3 \
    python3-pip

# 2. Kernel & Network Tuning for High-Concurrency Benchmarking
echo "Configuring high-throughput sysctl parameters..."
cat << 'EOF' > /etc/sysctl.d/99-benchmarks.conf
# File system descriptor limits
fs.file-max = 2097152
fs.inotify.max_user_instances = 8192
fs.inotify.max_user_watches = 524288

# TCP Connection Backlog & Queues
net.core.somaxconn = 65535
net.ipv4.tcp_max_syn_backlog = 65535
net.core.netdev_max_backlog = 100000

# Ephemeral Port Range & TCP Recycling
net.ipv4.ip_local_port_range = 1024 65535
net.ipv4.tcp_tw_reuse = 1
net.ipv4.tcp_fin_timeout = 15

# Buffer memory allocation for high throughput
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.ipv4.tcp_rmem = 4096 87380 16777216
net.ipv4.tcp_wmem = 4096 65536 16777216
net.ipv4.tcp_window_scaling = 1
net.ipv4.tcp_max_tw_buckets = 1440000
EOF
sysctl --system > /dev/null

# 3. Process & File Limits
cat << 'EOF' > /etc/security/limits.d/99-nofile.conf
* soft nofile 1048576
* hard nofile 1048576
root soft nofile 1048576
root hard nofile 1048576
EOF
ulimit -n 1048576 || true

# 4. Install Docker CE and Compose Plugin (if not present)
if ! command -v docker &> /dev/null; then
    echo "Installing Docker CE..."
    install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod a+r /etc/apt/keyrings/docker.asc
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | tee /etc/apt/sources.list.d/docker.list > /dev/null
    apt-get update -y
    apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi

# 5. Install Node.js 20 LTS & Autocannon (if not present)
if ! command -v node &> /dev/null; then
    echo "Installing Node.js 20 LTS..."
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
    apt-get install -y nodejs
fi
npm install -g autocannon yarn --quiet || true

# 6. Install k6 (Official Grafana repo)
if ! command -v k6 &> /dev/null; then
    echo "Installing k6 load generator..."
    curl -fsSL https://dl.k6.io/key.gpg | gpg --dearmor -o /usr/share/keyrings/k6-archive-keyring.gpg 2>/dev/null || true
    echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" | tee /etc/apt/sources.list.d/k6.list
    apt-get update -y
    apt-get install -y k6 || true
fi

# 7. Install .NET 10 SDK (if not present)
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET 10 SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi

echo "================================================================================"
echo " Host Setup & Tuning Complete!"
echo " Docker:     $(docker --version 2>/dev/null || echo 'not installed')"
echo " Node.js:    $(node --version 2>/dev/null || echo 'not installed')"
echo " .NET SDK:   $(dotnet --version 2>/dev/null || echo 'not installed')"
echo " k6:         $(k6 version 2>/dev/null || echo 'not installed')"
echo " Open Files: $(ulimit -n)"
echo "================================================================================"
