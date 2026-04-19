#requires -Version 5.1
<#
.SYNOPSIS
    Publishes the OpenAgent Windows service distribution to publish/win-x64/.

.DESCRIPTION
    Always deletes the existing publish/ directory, then:
      1. Publishes a self-contained single-file win-x64 exe.
      2. Installs the Baileys node_modules in the staged node/ folder.
      3. Prints the final size and folder layout.

    Run from anywhere - the script resolves paths relative to the repo root.
#>

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src/agent/OpenAgent'
$publishDir = Join-Path $repoRoot 'publish/win-x64'
$nodeDir    = Join-Path $publishDir 'node'

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

# Step 3: install production node_modules for the Baileys bridge
if (-not (Test-Path (Join-Path $nodeDir 'package.json'))) {
    throw "Expected $nodeDir/package.json after publish - Baileys bridge missing from output."
}

Write-Host "Installing Baileys production dependencies..." -ForegroundColor Cyan
Push-Location $nodeDir
try {
    npm ci --omit=dev
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Step 4: summary
$exePath  = Join-Path $publishDir 'OpenAgent.exe'
$exeSize  = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
$nodeSize = [math]::Round(((Get-ChildItem -Recurse $nodeDir | Measure-Object Length -Sum).Sum) / 1MB, 1)

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  OpenAgent.exe : $exeSize MB"
Write-Host "  node/         : $nodeSize MB"
Write-Host "  Output        : $publishDir"
Write-Host ""
Write-Host "Next: from an elevated CMD, run 'OpenAgent.exe --install' inside the publish folder."
