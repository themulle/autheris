# ==============================================================================
# dep12-clean-git-history.ps1
# PowerShell runner for DEP-12 git history cleanup tool
# ==============================================================================
[CmdletBinding()]
param(
    [switch]$Scan,
    [switch]$Execute,
    [switch]$Force
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pyScript = Join-Path $scriptDir "dep12-clean-git-history.py"

$argsList = @()
if ($Scan) { $argsList += "--scan" }
if ($Execute) { $argsList += "--execute" }
if ($Force) { $argsList += "--force" }

if ($argsList.Count -eq 0) {
    Write-Host "Usage: .\scripts\dep12-clean-git-history.ps1 [-Scan] [-Execute] [-Force]"
    exit 0
}

python $pyScript @argsList
exit $LASTEXITCODE
