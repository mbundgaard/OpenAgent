#requires -Version 5.1
<#
.SYNOPSIS
    Swaps a freshly published OpenAgent.exe into the local Windows service install.

.DESCRIPTION
    Upgrade flow for the in-place Windows service (see CLAUDE.md):
      1. Stop the OpenAgent service (releases the lock on the running exe).
      2. Back up the current exe, move the staged OpenAgent.exe.new into place.
      3. Start the service and wait for Running.
      4. Probe /health to confirm the new build is live.

    Expects OpenAgent.exe.new to already be staged in $InstallDir (publish-windows.ps1
    output copied there). Must run elevated -- service control is Administrators-only.
    On a failed start it rolls the previous exe back automatically.
#>
param(
    [string]$InstallDir  = 'C:\OpenAgent',
    [string]$ServiceName = 'OpenAgent',
    [string]$HealthUrl   = 'http://localhost:8080/health'
)

$ErrorActionPreference = 'Stop'

# Elevation gate -- Stop/Start-Service on this service requires Administrators.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) { throw "Must run elevated (Administrator)." }

$exe    = Join-Path $InstallDir 'OpenAgent.exe'
$staged = Join-Path $InstallDir 'OpenAgent.exe.new'
$backup = Join-Path $InstallDir ("OpenAgent.exe.bak-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (-not (Test-Path $staged)) { throw "Staged build not found: $staged" }

# Step 1: stop the service -- Stop-Service blocks until Stopped.
Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
Stop-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')

# Step 2: back up current exe, then swap. Retry the swap briefly -- the OS can hold
# the image handle for a moment after the process exits.
Write-Host "Backing up current exe -> $backup" -ForegroundColor Cyan
Copy-Item $exe $backup -Force

Write-Host "Swapping in OpenAgent.exe.new..." -ForegroundColor Cyan
$swapped = $false
foreach ($attempt in 1..10) {
    try { Move-Item $staged $exe -Force; $swapped = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $swapped) {
    Start-Service $ServiceName
    throw "Could not replace $exe (still locked). Service restarted on the OLD build."
}

# Step 3: start and wait for Running.
Write-Host "Starting $ServiceName..." -ForegroundColor Cyan
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')

# Step 4: health probe. On failure, roll back to the backup.
Write-Host "Probing $HealthUrl ..." -ForegroundColor Cyan
$healthy = $false
foreach ($attempt in 1..15) {
    try {
        $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $healthy = $true; break }
    } catch { Start-Sleep -Seconds 1 }
}

if (-not $healthy) {
    Write-Host "Health check failed -- rolling back to previous exe." -ForegroundColor Red
    Stop-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Stopped', '00:00:30')
    Copy-Item $backup $exe -Force
    Start-Service $ServiceName
    (Get-Service $ServiceName).WaitForStatus('Running', '00:00:30')
    throw "Deploy rolled back. Service is Running on the previous build. Backup kept at $backup."
}

Write-Host ""
Write-Host "Deploy complete -- $ServiceName is Running and healthy." -ForegroundColor Green
Write-Host "  Backup kept: $backup"
