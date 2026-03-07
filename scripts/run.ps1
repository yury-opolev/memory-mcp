#Requires -Version 5.1
<#
.SYNOPSIS
    memory-mcp run script.
.DESCRIPTION
    Publishes (if needed) and starts the MCP server.
    Use -Rebuild to force a fresh publish even if binaries exist.
.PARAMETER Rebuild
    Force a fresh publish, ignoring existing binaries.
#>

param(
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoDir = Split-Path -Parent $ScriptDir
$PublishDir = Join-Path $RepoDir "publish"
$ServerExe = Join-Path $PublishDir "MemoryMcp.exe"

# --- Publish step ---
if ($Rebuild -or -not (Test-Path $ServerExe)) {
    if ($Rebuild) {
        Write-Host "Rebuild requested. Publishing memory-mcp..." -ForegroundColor Yellow
    } else {
        Write-Host "No published binaries found. Publishing memory-mcp..." -ForegroundColor Yellow
    }

    & dotnet publish "$RepoDir\src\MemoryMcp" `
        --configuration Release `
        --output "$PublishDir" `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "Published to: $PublishDir" -ForegroundColor Green
} else {
    Write-Host "Using existing published binaries at: $PublishDir" -ForegroundColor Green
    Write-Host "  (use -Rebuild to force a fresh build)"
}

Write-Host ""

# --- MCP client setup instructions ---
Write-Host "=== MCP Client Setup ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "To use this server with an MCP client, add the following to your config:"
Write-Host ""
Write-Host "  OpenCode (opencode.jsonc):" -ForegroundColor Yellow
Write-Host @"
  {
    "`$schema": "https://opencode.ai/config.json",
    "mcp": {
      "memory": {
        "type": "local",
        "command": ["$($ServerExe -replace '\\', '\\\\')"],
        "enabled": true
      }
    }
  }
"@
Write-Host ""
Write-Host "  Claude Desktop / VS Code / Cursor (mcp.json):" -ForegroundColor Yellow
Write-Host @"
  {
    "mcpServers": {
      "memory": {
        "command": "$($ServerExe -replace '\\', '\\\\')"
      }
    }
  }
"@
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host ""

# --- Start the server ---
Write-Host "Starting memory-mcp server (stdio transport)..."
Write-Host "Press Ctrl+C to stop."
Write-Host ""

& $ServerExe
