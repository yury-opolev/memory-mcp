#Requires -Version 5.1
<#
.SYNOPSIS
    memory-mcp setup script.
.DESCRIPTION
    Restores .NET dependencies, checks for Ollama, and pulls the embedding model.
#>

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoDir = Split-Path -Parent $ScriptDir

Write-Host "=== memory-mcp setup ===" -ForegroundColor Cyan
Write-Host ""

# 1. Check for .NET SDK
Write-Host "Checking for .NET SDK..."
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "ERROR: .NET SDK not found." -ForegroundColor Red
    Write-Host "Install .NET 10 SDK from https://dot.net/download"
    exit 1
}

$dotnetVersion = & dotnet --version
Write-Host "Found .NET SDK $dotnetVersion"
Write-Host ""

# 2. Restore NuGet packages
Write-Host "Restoring NuGet packages..."
& dotnet restore "$RepoDir\MemoryMcp.slnx"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "NuGet packages restored."
Write-Host ""

# 3. Build the solution
Write-Host "Building solution..."
& dotnet build "$RepoDir\MemoryMcp.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Build succeeded."
Write-Host ""

# 4. Check for Ollama
Write-Host "Checking for Ollama..."
$ollamaCmd = Get-Command ollama -ErrorAction SilentlyContinue
if (-not $ollamaCmd) {
    Write-Host "WARNING: Ollama not found." -ForegroundColor Yellow
    Write-Host "Install Ollama to enable semantic search:"
    Write-Host "  Download from https://ollama.com/download"
    Write-Host ""
    Write-Host "After installing, run: ollama serve"
    Write-Host "Then re-run this setup script to pull the embedding model."
    Write-Host ""
    Write-Host "Setup completed (without Ollama)."
    exit 0
}

$ollamaVersion = & ollama --version 2>&1
Write-Host "Found Ollama: $ollamaVersion"
Write-Host ""

# 5. Check if Ollama is running
Write-Host "Checking if Ollama is running..."
try {
    $null = Invoke-WebRequest -Uri "http://localhost:11434/api/tags" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
    Write-Host "Ollama is running."
    Write-Host ""
} catch {
    Write-Host "WARNING: Ollama does not appear to be running." -ForegroundColor Yellow
    Write-Host "Start it with: ollama serve"
    Write-Host ""
    Write-Host "Setup completed (Ollama not running, model not pulled)."
    exit 0
}

# 6. Pull the embedding model
$model = "qwen3-embedding:0.6b"
Write-Host "Pulling embedding model '$model'..."
& ollama pull $model
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Model '$model' is ready."
Write-Host ""

Write-Host "=== Setup complete ===" -ForegroundColor Cyan
Write-Host "Run the server with: .\scripts\run.ps1"
