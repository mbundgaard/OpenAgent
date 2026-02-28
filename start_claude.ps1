# StartClaude.ps1
Set-Location $PSScriptRoot
$env:ENABLE_LSP_TOOLS = "1"
claude --dangerously-skip-permissions