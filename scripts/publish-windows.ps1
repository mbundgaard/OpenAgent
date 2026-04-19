#requires -Version 5.1
<#
.SYNOPSIS
    Publishes the OpenAgent Windows distribution to publish/win-x64/.

.DESCRIPTION
    Always cleans the existing publish folder, then:
      1. Builds the React app (src/web -> dist/).
      2. Publishes a self-contained single-file win-x64 exe.
      3. Stages the React dist/ into publish/win-x64/wwwroot/.
      4. Prints the final size.

    Run from anywhere - the script resolves paths relative to the repo root.
#>

$ErrorActionPreference = 'Stop'

$repoRoot       = Split-Path -Parent $PSScriptRoot
$projectDir     = Join-Path $repoRoot 'src/agent/OpenAgent'
$webDir         = Join-Path $repoRoot 'src/web'
$publishDir     = Join-Path $repoRoot 'publish/win-x64'
$projectWwwroot = Join-Path $projectDir 'wwwroot'

# Step 1: clean publish output (keep the dir itself so an open shell cd'd into it doesn't block us)
if (Test-Path $publishDir) {
    Write-Host "Cleaning publish folder contents..." -ForegroundColor Yellow
    Get-ChildItem -Path $publishDir -Force | Remove-Item -Recurse -Force
}

# Step 2: build the React app
Write-Host "Building React app (src/web)..." -ForegroundColor Cyan
Push-Location $webDir
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

# Step 3: stage the React build into the .NET project's wwwroot/ so dotnet publish bundles it
$webDist = Join-Path $webDir 'dist'
if (-not (Test-Path $webDist)) {
    throw "Expected $webDist after npm run build."
}
Write-Host "Staging React build into project wwwroot/..." -ForegroundColor Cyan
if (Test-Path $projectWwwroot) {
    Get-ChildItem -Path $projectWwwroot -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $projectWwwroot | Out-Null
}
Copy-Item -Path "$webDist\*" -Destination $projectWwwroot -Recurse -Force

# Step 4: publish self-contained single-file exe (wwwroot embedded via IncludeAllContentForSelfExtract)
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

# Step 5: summary
$exePath = Join-Path $publishDir 'OpenAgent.exe'
$exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  OpenAgent.exe : $exeSize MB"
Write-Host "  Output        : $publishDir"
