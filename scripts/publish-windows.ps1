#requires -Version 5.1
<#
.SYNOPSIS
    Publishes the OpenAgent Windows exe to publish/win-x64/.

.DESCRIPTION
    Always deletes the existing publish/ directory, then publishes a
    self-contained single-file win-x64 exe. Prints the final size.

    Run from anywhere - the script resolves paths relative to the repo root.
#>

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src/agent/OpenAgent'
$publishDir = Join-Path $repoRoot 'publish/win-x64'

# Step 1: clean publish output
if (Test-Path $publishDir) {
    Write-Host "Removing existing publish folder..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $publishDir
}

# Step 2: publish self-contained single-file exe
Write-Host "Publishing OpenAgent.exe (self-contained, single-file, win-x64)..." -ForegroundColor Cyan
Push-Location $projectDir
try {
    dotnet publish -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Step 3: summary
$exePath = Join-Path $publishDir 'OpenAgent.exe'
$exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  OpenAgent.exe : $exeSize MB"
Write-Host "  Output        : $publishDir"
