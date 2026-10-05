param(
    [string]$Duration = "30s",
    [int]$Rps = 1000,
    [int]$Vus = 50,
    [string]$Scenario = "rps",
    [switch]$RunRampAfter = $true,
    [switch]$SkipProvision = $false,
    [switch]$AutoDestroy = $false
)

$ErrorActionPreference = "Stop"

# 1. Locate hcloud CLI
$cmd = Get-Command hcloud -ErrorAction SilentlyContinue
$hcloud = if ($cmd) { $cmd.Source } else { $null }
if (-not $hcloud) {
    $found = Get-ChildItem -Path "C:\Users\themu\AppData\Local\Microsoft\WinGet\Packages" -Filter "hcloud.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $hcloud = $found.FullName }
}

if (-not $hcloud) {
    Write-Error "hcloud CLI could not be found."
    exit 1
}

Write-Host "Using hcloud: $hcloud"

# Ensure Token
if (-not $env:HCLOUD_TOKEN) {
    Write-Error "Please set `$env:HCLOUD_TOKEN before running this script."
    exit 1
}

$Location = "fsn1"
$NetworkName = "bench-net"
$SshKeyName = "themu@DellLatitude"
$TypeDb = "cpx42"
$TypeGateway = "ccx33"
$TypeClient = "cpx32"

$ScriptDir = $PSScriptRoot
$BenchDir = $ScriptDir
$RepoDir = (Get-Item "$BenchDir\..\..").FullName
$CloudInitFile = Join-Path $BenchDir "hcloud\cloud-init.yaml"
$ResultsDir = Join-Path $BenchDir "results"

if (-not (Test-Path $ResultsDir)) {
    New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
}

$SshOpts = @("-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL", "-o", "LogLevel=ERROR")

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host " Hetzner Cloud 3-Tier Multi-Host GraphQL Benchmark Orchestrator" -ForegroundColor Cyan
Write-Host " Location:    $Location"
Write-Host " DB Host:     $TypeDb (cpx42: 8 vCPU, 16 GB RAM)"
Write-Host " Gateway:     $TypeGateway (ccx33: 8 Dedicated AMD vCPU, 32 GB RAM)"
Write-Host " Client (k6): $TypeClient (cpx32: 4 vCPU, 8 GB RAM)"
Write-Host " Duration:    $Duration (Target RPS: $Rps)"
Write-Host "================================================================================" -ForegroundColor Cyan

# Step 1: Provision
if (-not $SkipProvision) {
    Write-Host "`n>>> Step 1/5: Provisioning 3-Tier Network and Servers on Hetzner Cloud..." -ForegroundColor Yellow

    # Network
    $allNets = & $hcloud network list -o noheader
    $netFound = $allNets | Where-Object { $_ -match "\b$NetworkName\b" }
    if (-not $netFound) {
        Write-Host "Creating private network $NetworkName (10.0.0.0/16)..."
        & $hcloud network create --name $NetworkName --ip-range "10.0.0.0/16"
        & $hcloud network add-subnet $NetworkName --network-zone "eu-central" --type "cloud" --ip-range "10.0.1.0/24"
    } else {
        Write-Host "Private network $NetworkName already exists."
    }

    # Helper function to create server if not present
    function Ensure-Server {
        param($Name, $Type)
        $allServers = & $hcloud server list -o noheader
        $srvFound = $allServers | Where-Object { $_ -match "\b$Name\b" }
        if (-not $srvFound) {
            Write-Host "Creating server $Name ($Type)..."
            & $hcloud server create --name $Name --type $Type --image "ubuntu-24.04" --location $Location --network $NetworkName --ssh-key $SshKeyName --user-data-from-file $CloudInitFile
        } else {
            Write-Host "Server $Name already exists."
        }
        Write-Host "Waiting for server $Name to become active..."
        for ($s = 0; $s -lt 60; $s++) {
            $info = & $hcloud server describe $Name -o json | ConvertFrom-Json
            if ($info.status -eq "running") {
                Write-Host "Server $Name is running." -ForegroundColor Green
                break
            }
            Start-Sleep -Seconds 2
        }
    }

    Ensure-Server "bench-db" $TypeDb
    Ensure-Server "bench-gateway" $TypeGateway
    Ensure-Server "bench-client" $TypeClient
}

# Fetch IPs
function Get-Server-Private-Ip {
    param($Name)
    $json = & $hcloud server describe $Name -o json | ConvertFrom-Json
    if ($json.private_net -and $json.private_net.Count -gt 0) {
        return $json.private_net[0].ip
    }
    return $null
}

$IpDb = (& $hcloud server ip bench-db).Trim()
$IpGw = (& $hcloud server ip bench-gateway).Trim()
$IpClient = (& $hcloud server ip bench-client).Trim()

$PrivateIpDb = Get-Server-Private-Ip "bench-db"
$PrivateIpGw = Get-Server-Private-Ip "bench-gateway"
$PrivateIpClient = Get-Server-Private-Ip "bench-client"

Write-Host "`n>>> Cluster Topology:" -ForegroundColor Green
Write-Host "    DB Host (bench-db):          Public: $IpDb | Private: $PrivateIpDb"
Write-Host "    Gateway SUT (bench-gateway): Public: $IpGw | Private: $PrivateIpGw"
Write-Host "    Load Gen (bench-client):     Public: $IpClient | Private: $PrivateIpClient"

# Wait for SSH and cloud-init completion on all nodes
function Wait-For-Host {
    param($Ip, $Name)
    Write-Host "Waiting for SSH on $Name ($Ip)..." -NoNewline
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        $res = ssh @SshOpts "root@$Ip" "echo ssh_ok" 2>&1
        if ($res -match "ssh_ok") {
            $ready = $true
            break
        }
        Write-Host "." -NoNewline
        Start-Sleep -Seconds 3
    }
    if (-not $ready) {
        throw "Timeout waiting for SSH on $Name ($Ip)"
    }
    Write-Host " SSH Ready! Waiting for cloud-init package installation..."
    ssh @SshOpts "root@$Ip" "cloud-init status --wait || true"
    Write-Host "$Name is fully provisioned." -ForegroundColor Green
}

Wait-For-Host $IpDb "bench-db"
Wait-For-Host $IpGw "bench-gateway"
Wait-For-Host $IpClient "bench-client"

# Step 2: Packaging & Deployment
Write-Host "`n>>> Packaging code and uploading to servers..." -ForegroundColor Yellow

$TempDir = [System.IO.Path]::GetTempPath()
$BenchTar = Join-Path $TempDir "autheris_bench.tar.gz"
$RepoTar = Join-Path $TempDir "autheris_repo.tar.gz"

if (Test-Path $BenchTar) { Remove-Item $BenchTar -Force }
if (Test-Path $RepoTar) { Remove-Item $RepoTar -Force }

Write-Host "Creating benchmark archive..."
tar -czf $BenchTar -C $BenchDir --exclude="results/*.json" --exclude="results/*.csv" --exclude="results/*.md" .

Write-Host "Creating full repo archive (src, Directory.Build.props, benchmarks)..."
tar -czf $RepoTar -C $RepoDir --exclude=".git" --exclude="**/bin" --exclude="**/obj" --exclude="**/node_modules" src Directory.Build.props benchmarks

# Upload to bench-db
Write-Host "Uploading to bench-db..."
scp @SshOpts $BenchTar "root@${IpDb}:/root/bench.tar.gz"
ssh @SshOpts "root@${IpDb}" "mkdir -p /root/autheris/benchmarks/load && tar -xzf /root/bench.tar.gz -C /root/autheris/benchmarks/load && find /root/autheris -name '*.sh' -exec sed -i 's/\r$//' {} + && chmod +x /root/autheris/benchmarks/load/scripts/*.sh"

# Upload to bench-gateway
Write-Host "Uploading to bench-gateway..."
scp @SshOpts $RepoTar "root@${IpGw}:/root/repo.tar.gz"
ssh @SshOpts "root@${IpGw}" "mkdir -p /root/autheris && tar -xzf /root/repo.tar.gz -C /root/autheris && find /root/autheris -name '*.sh' -exec sed -i 's/\r$//' {} + && chmod +x /root/autheris/benchmarks/load/scripts/*.sh"

# Upload to bench-client
Write-Host "Uploading to bench-client..."
scp @SshOpts $BenchTar "root@${IpClient}:/root/bench.tar.gz"
ssh @SshOpts "root@${IpClient}" "mkdir -p /root/autheris/benchmarks/load && tar -xzf /root/bench.tar.gz -C /root/autheris/benchmarks/load && find /root/autheris -name '*.sh' -exec sed -i 's/\r$//' {} + && chmod +x /root/autheris/benchmarks/load/scripts/*.sh"

# Step 3: Initialize Database on bench-db
Write-Host "`n>>> Step 2/5: Initializing Database on bench-db ($IpDb)..." -ForegroundColor Yellow
ssh @SshOpts "root@${IpDb}" "cd /root/autheris/benchmarks/load && ./scripts/02_init_database.sh"

# Step 4: Deploy & Start Gateways on bench-gateway
Write-Host "`n>>> Step 3/5: Deploying & Starting Gateways on bench-gateway ($IpGw)..." -ForegroundColor Yellow
ssh @SshOpts "root@${IpGw}" "cd /root/autheris/benchmarks/load && export DATABASE_URL='postgres://postgres:postgrespassword@${PrivateIpDb}:5432/postgres' DB_HOST='${PrivateIpDb}' && ./scripts/03_start_gateways.sh"

# Step 5: Run Benchmark from Client Host
Write-Host "`n>>> Step 4/5: Running Isolated Benchmark from bench-client ($IpClient)..." -ForegroundColor Yellow

# Run 1: Standard benchmark
Write-Host "Executing Run 1: Standard Benchmark (Duration: $Duration, Target RPS: $Rps)..." -ForegroundColor Cyan
ssh @SshOpts "root@${IpClient}" "cd /root/autheris/benchmarks/load && DURATION='$Duration' RPS='$Rps' VUS='$Vus' SCENARIO='$Scenario' GATEWAY_HOST='${PrivateIpGw}' ./scripts/04_run_benchmarks.sh"

if ($RunRampAfter) {
    Write-Host "`nExecuting Run 2: Ramp-Up Stress Benchmark (SCENARIO=ramp, Duration: 70s)..." -ForegroundColor Cyan
    ssh @SshOpts "root@${IpClient}" "cd /root/autheris/benchmarks/load && DURATION='70s' RPS='5000' VUS='100' SCENARIO='ramp' GATEWAY_HOST='${PrivateIpGw}' ./scripts/04_run_benchmarks.sh"
}

# Step 6: Download Results and Analyze
Write-Host "`n>>> Step 5/5: Downloading Results from bench-client..." -ForegroundColor Yellow
$RemoteResTar = Join-Path $TempDir "results.tar.gz"
if (Test-Path $RemoteResTar) { Remove-Item $RemoteResTar -Force }
ssh @SshOpts "root@${IpClient}" "cd /root/autheris/benchmarks/load && tar -czf /tmp/results.tar.gz results"
scp @SshOpts "root@${IpClient}:/tmp/results.tar.gz" $RemoteResTar
tar -xzf $RemoteResTar -C $BenchDir

Write-Host "`nAnalyzing Results..." -ForegroundColor Cyan
python "$BenchDir\scripts\05_analyze_results.py" --results-dir "$ResultsDir"

if ($AutoDestroy) {
    Write-Host "`nCleaning up Hetzner Cloud infrastructure (--auto-destroy)..." -ForegroundColor Red
    & $hcloud server delete bench-db bench-gateway bench-client
    & $hcloud network delete bench-net
    Write-Host "Cluster deleted."
} else {
    Write-Host "`n================================================================================" -ForegroundColor Green
    Write-Host " Cluster is kept running for your inspection:" -ForegroundColor Green
    Write-Host "   Database:     ssh root@$IpDb"
    Write-Host "   Gateway (SUT):ssh root@$IpGw"
    Write-Host "   Client (k6):  ssh root@$IpClient"
    Write-Host " To delete later:" -ForegroundColor Yellow
    Write-Host "   hcloud server delete bench-db bench-gateway bench-client"
    Write-Host "   hcloud network delete bench-net"
    Write-Host "================================================================================" -ForegroundColor Green
}
