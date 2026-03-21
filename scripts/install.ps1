#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, publishes, and installs memory-mcp to %LOCALAPPDATA%\memory-mcp.
.DESCRIPTION
    Publishes the MCP server to %LOCALAPPDATA%\memory-mcp\bin, verifies Ollama
    and the embedding model, then prints MCP client configuration snippets.
    All data (database, memory files) is stored under %LOCALAPPDATA%\memory-mcp\data.
.PARAMETER Rebuild
    Force a fresh publish even if binaries already exist.
.PARAMETER SkipOllama
    Skip Ollama checks and model pull.
.PARAMETER DataEncryption
    Enable or disable data-at-rest encryption (AES-256-GCM for files, SQLCipher for database).
    Pass $true or $false. When omitted, the existing setting is left unchanged.
#>

param(
    [switch]$Rebuild,
    [switch]$SkipOllama,
    [Nullable[bool]]$DataEncryption = $null
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoDir = Split-Path -Parent $ScriptDir

# --- Install paths ---
$InstallRoot = Join-Path $env:LOCALAPPDATA "memory-mcp"
$BinDir = Join-Path $InstallRoot "bin"
$DataDir = Join-Path $InstallRoot "data"
$ServerExe = Join-Path $BinDir "MemoryMcp.exe"

Write-Host "=== memory-mcp install ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Install root : $InstallRoot"
Write-Host "Binaries     : $BinDir"
Write-Host "Data         : $DataDir"
Write-Host ""

# --- 1. Check .NET SDK ---
Write-Host "Checking for .NET SDK..." -ForegroundColor Yellow
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "ERROR: .NET SDK not found. Install from https://dot.net/download" -ForegroundColor Red
    exit 1
}
$dotnetVersion = & dotnet --version
Write-Host "  Found .NET SDK $dotnetVersion"
Write-Host ""

# --- 2. Publish ---
if ($Rebuild -or -not (Test-Path $ServerExe)) {
    if ($Rebuild) {
        Write-Host "Rebuild requested. Publishing..." -ForegroundColor Yellow
    } else {
        Write-Host "Publishing memory-mcp..." -ForegroundColor Yellow
    }

    # Ensure bin directory exists
    if (-not (Test-Path $BinDir)) {
        New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    }

    & dotnet publish "$RepoDir\src\MemoryMcp" `
        --configuration Release `
        --output "$BinDir" `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Publish failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    Write-Host "  Published to: $BinDir" -ForegroundColor Green
} else {
    Write-Host "Binaries already exist at: $BinDir" -ForegroundColor Green
    Write-Host "  (use -Rebuild to force a fresh build)"
}
Write-Host ""

# --- 3. Ensure data directory exists ---
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    Write-Host "Created data directory: $DataDir" -ForegroundColor Green
} else {
    Write-Host "Data directory exists: $DataDir" -ForegroundColor Green
}
Write-Host ""

# --- 4. User settings (survives rebuilds) ---
$SettingsFile = Join-Path $DataDir "settings.json"
if ($null -ne $DataEncryption) {
    # Load existing settings or start fresh
    $settings = @{}
    if (Test-Path $SettingsFile) {
        $settings = Get-Content $SettingsFile -Raw | ConvertFrom-Json -AsHashtable
    }

    # Ensure nested structure
    if (-not $settings.ContainsKey("MemoryMcp")) {
        $settings["MemoryMcp"] = @{}
    }
    if (-not $settings["MemoryMcp"].ContainsKey("Encryption")) {
        $settings["MemoryMcp"]["Encryption"] = @{}
    }

    $settings["MemoryMcp"]["Encryption"]["Enabled"] = $DataEncryption

    $settings | ConvertTo-Json -Depth 10 | Set-Content $SettingsFile -Encoding UTF8
    $label = if ($DataEncryption) { "enabled" } else { "disabled" }
    Write-Host "Encryption $label in: $SettingsFile" -ForegroundColor Green
} elseif (Test-Path $SettingsFile) {
    Write-Host "User settings : $SettingsFile" -ForegroundColor Green
} else {
    Write-Host "No user settings file (use -DataEncryption `$true or -DataEncryption `$false to configure)"
}
Write-Host ""

# --- 5. Ollama checks ---
if (-not $SkipOllama) {
    Write-Host "Checking for Ollama..." -ForegroundColor Yellow
    $ollamaCmd = Get-Command ollama -ErrorAction SilentlyContinue
    if (-not $ollamaCmd) {
        Write-Host "  WARNING: Ollama not found. Install from https://ollama.com/download" -ForegroundColor Yellow
        Write-Host "  The server will start but embedding tools will fail without Ollama."
        Write-Host ""
    } else {
        $ollamaVersion = & ollama --version 2>&1
        Write-Host "  Found Ollama: $ollamaVersion"

        # Check if Ollama is running
        $ollamaRunning = $false
        try {
            $null = Invoke-WebRequest -Uri "http://localhost:11434/api/tags" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            $ollamaRunning = $true
            Write-Host "  Ollama is running."
        } catch {
            Write-Host "  WARNING: Ollama is not running. Start it with: ollama serve" -ForegroundColor Yellow
        }

        if ($ollamaRunning) {
            $model = "qwen3-embedding:0.6b"
            Write-Host "  Pulling embedding model '$model'..."
            & ollama pull $model
            if ($LASTEXITCODE -ne 0) {
                Write-Host "  WARNING: Failed to pull model." -ForegroundColor Yellow
            } else {
                Write-Host "  Model '$model' is ready." -ForegroundColor Green
            }
        }
        Write-Host ""
    }
}

# --- 6. MCP client configuration ---
$ExePath = $ServerExe -replace '\\', '/'
$ExePathJson = $ServerExe -replace '\\', '\\\\'

Write-Host "=== MCP Client Configuration ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Claude Code (~/.claude.json or project .mcp.json):" -ForegroundColor Yellow
Write-Host @"
  {
    "mcpServers": {
      "memory": {
        "command": "$ExePathJson",
        "env": {
          "MemoryMcp__DataDirectory": "$($DataDir -replace '\\', '\\\\')"
        }
      }
    }
  }
"@
Write-Host ""
Write-Host "VS Code / Cursor (settings or .vscode/mcp.json):" -ForegroundColor Yellow
Write-Host @"
  {
    "mcpServers": {
      "memory": {
        "command": "$ExePathJson",
        "env": {
          "MemoryMcp__DataDirectory": "$($DataDir -replace '\\', '\\\\')"
        }
      }
    }
  }
"@
Write-Host ""
Write-Host "OpenCode (opencode.jsonc):" -ForegroundColor Yellow
Write-Host @"
  {
    "`$schema": "https://opencode.ai/config.json",
    "mcp": {
      "memory": {
        "type": "local",
        "command": ["$ExePathJson"],
        "env": {
          "MemoryMcp__DataDirectory": "$($DataDir -replace '\\', '\\\\')"
        },
        "enabled": true
      }
    }
  }
"@
Write-Host ""
Write-Host "=== Install complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Binaries : $ServerExe" -ForegroundColor Green
Write-Host "Data     : $DataDir" -ForegroundColor Green
Write-Host "Settings : $SettingsFile" -ForegroundColor Green
Write-Host ""
Write-Host "Make sure Ollama is running (ollama serve) before using the MCP server."
