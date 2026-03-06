#Requires -Version 5.1
<#
.SYNOPSIS
    memory-mcp run script.
.DESCRIPTION
    Builds and starts the MCP server with default settings.
#>

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoDir = Split-Path -Parent $ScriptDir

Write-Host "Building memory-mcp..."
& dotnet build "$RepoDir\MemoryMcp.slnx" --configuration Release --verbosity quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Starting memory-mcp server (stdio transport)..."
Write-Host "Press Ctrl+C to stop."
Write-Host ""

& dotnet run --project "$RepoDir\src\MemoryMcp" --configuration Release --no-build
