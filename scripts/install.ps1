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
    Enable or disable data-at-rest encryption (AES-256-GCM for files, SQLite3 Multiple Ciphers for the
    database). An encrypted database written by an earlier release (SQLCipher) is converted on the first
    start; the original is kept beside it as memory.db.sqlcipher-backup.
    Pass $true or $false. When omitted, the existing setting is left unchanged.
.PARAMETER Shared
    Switch shared mode on or off. In shared mode one long-lived memory service owns the database and every agent
    session's MemoryMcp process relays to it. $true publishes the service to %LOCALAPPDATA%\memory-mcp\service\<time>,
    registers the scheduled task MemoryMcp-<hash> that starts it at logon, restarts it on the new version and sets
    MemoryMcp.Shared.Enabled in settings.json. $false stops the service, removes the task and sets it false; open
    agent sessions must then be restarted. When omitted, shared mode is left as it is (and, when it is on, the service
    is updated).
#>

param(
    [switch]$Rebuild,
    [switch]$SkipOllama,
    [Nullable[bool]]$DataEncryption = $null,
    [Nullable[bool]]$Shared = $null
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoDir = Split-Path -Parent $ScriptDir

# --- Install paths ---
$InstallRoot = Join-Path $env:LOCALAPPDATA "memory-mcp"
$BinDir = Join-Path $InstallRoot "bin"
$DataDir = Join-Path $InstallRoot "data"
$ServerExe = Join-Path $BinDir "MemoryMcp.exe"
$ServiceRoot = Join-Path $InstallRoot "service"
$SettingsFile = Join-Path $DataDir "settings.json"
$ServicePathFile = Join-Path $DataDir "service-path.txt"
$StopMarkerFile = Join-Path $DataDir "service.stopped"
$DataDirArgument = "--MemoryMcp:DataDirectory=$DataDir"

# --- Shared mode helpers ---
function Read-UserSettings {
    if (Test-Path -LiteralPath $SettingsFile) {
        $raw = Get-Content -LiteralPath $SettingsFile -Raw
        if ($raw -and $raw.Trim()) {
            return $raw | ConvertFrom-Json -AsHashtable
        }
    }
    return @{}
}

function Get-SharedEnabled {
    try {
        $settings = Read-UserSettings
    } catch {
        # An unreadable settings.json is reported by the server itself; an install without -Shared goes on as before.
        return $false
    }
    $memoryMcp = $settings["MemoryMcp"]
    if ($memoryMcp -is [System.Collections.IDictionary] -and $memoryMcp["Shared"] -is [System.Collections.IDictionary]) {
        $enabled = $memoryMcp["Shared"]["Enabled"]
        if ($enabled -is [bool]) {
            return $enabled
        }
        return "$enabled" -eq "true"
    }
    return $false
}

# Sets MemoryMcp.Shared.Enabled in settings.json and keeps every other key.
function Set-SharedEnabled([bool]$enabled) {
    if (-not (Test-Path -LiteralPath $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    $settings = Read-UserSettings
    if (-not ($settings["MemoryMcp"] -is [System.Collections.IDictionary])) {
        $settings["MemoryMcp"] = @{}
    }
    if (-not ($settings["MemoryMcp"]["Shared"] -is [System.Collections.IDictionary])) {
        $settings["MemoryMcp"]["Shared"] = @{}
    }
    $settings["MemoryMcp"]["Shared"]["Enabled"] = $enabled
    $settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SettingsFile -Encoding UTF8
}

# The service copy is a GUI-subsystem build (it has no console window), so its commands run through the dotnet host,
# a console program, for their output and exit code.
function Invoke-ServiceCommand([string]$serviceDll, [string[]]$arguments) {
    & dotnet $serviceDll @arguments $DataDirArgument | Out-Host
    return $LASTEXITCODE
}

# The <hash> in the launcher name, from the program (`status --print-hash`); null when it does not report one.
function Get-LauncherHash([string]$serviceDll) {
    $output = & dotnet $serviceDll status $DataDirArgument --print-hash
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    $hash = "$($output | Select-Object -Last 1)".Trim()
    if ($hash -notmatch '^[0-9a-f]{8}$') {
        return $null
    }
    return $hash
}

# Service directories, newest first.
function Get-ServiceDirectories {
    if (-not (Test-Path -LiteralPath $ServiceRoot)) {
        return @()
    }
    return @(Get-ChildItem -LiteralPath $ServiceRoot -Directory |
        Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
        Sort-Object Name -Descending)
}

# The installed service, from service-path.txt or else the newest service directory; null when there is none.
function Find-InstalledServiceDll {
    if (Test-Path -LiteralPath $ServicePathFile) {
        $servicePath = "$(Get-Content -LiteralPath $ServicePathFile -Raw)".Trim()
        if ($servicePath) {
            $dll = Join-Path (Split-Path -Parent $servicePath) "MemoryMcp.dll"
            if (Test-Path -LiteralPath $dll) {
                return $dll
            }
        }
    }
    foreach ($directory in Get-ServiceDirectories) {
        $dll = Join-Path $directory.FullName "MemoryMcp.dll"
        if (Test-Path -LiteralPath $dll) {
            return $dll
        }
    }
    return $null
}

# True when a file in the directory cannot be opened for writing, as while a process runs from it.
function Test-DirectoryInUse([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory)) {
        return $false
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
        try {
            $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Dispose()
        } catch {
            return $true
        }
    }
    return $false
}

# Registers (or replaces) the logon task that the program's WindowsTaskLauncher queries and runs by name: as the
# current user, only while that user is logged on, without elevation. Register-ScheduledTask is used rather than
# schtasks because schtasks cannot clear the defaults that would stop the service: start only on AC power, stop when
# going on battery, stop after 72 hours, and ignore a run request while an instance is still ending.
function Register-ServiceTask([string]$taskName, [string]$serviceExe) {
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute "`"$serviceExe`"" -Argument "serve `"$DataDirArgument`"" -WorkingDirectory (Split-Path -Parent $serviceExe)
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
    $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel -Priority 5
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $taskSettings -Description "memory-mcp shared memory service for $DataDir" -Force | Out-Null
}

function Unregister-ServiceTask([string]$taskName) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "  Removed scheduled task $taskName" -ForegroundColor Green
    } else {
        Write-Host "  No scheduled task $taskName is registered."
    }
}

# True when bin holds the build this install publishes: its MemoryMcp.dll is identical to a fresh console build's
# (the service copy cannot be compared directly, as its windowless build differs in the assembly itself).
function Test-BinMatchesBuild {
    $binDll = Join-Path $BinDir "MemoryMcp.dll"
    if (-not (Test-Path -LiteralPath $binDll)) {
        return $false
    }
    $stagingDir = Join-Path $InstallRoot "bin-compare"
    try {
        & dotnet publish "$RepoDir\src\MemoryMcp" `
            --configuration Release `
            --output "$stagingDir" `
            --verbosity quiet | Out-Host
        $stagingDll = Join-Path $stagingDir "MemoryMcp.dll"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stagingDll)) {
            return $false
        }
        return (Get-FileHash -LiteralPath $binDll -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $stagingDll -Algorithm SHA256).Hash
    } finally {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Publish-Bin {
    if (-not (Test-Path $BinDir)) {
        New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    }

    & dotnet publish "$RepoDir\src\MemoryMcp" `
        --configuration Release `
        --output "$BinDir" `
        --verbosity quiet | Out-Host
    return $LASTEXITCODE
}

function Disable-SharedMode {
    Write-Host "Switching shared mode off..." -ForegroundColor Yellow
    Set-SharedEnabled $false
    Write-Host "  Shared mode disabled in: $SettingsFile" -ForegroundColor Green

    $serviceDll = Find-InstalledServiceDll
    if ($serviceDll) {
        $hash = Get-LauncherHash $serviceDll
        if ((Invoke-ServiceCommand $serviceDll @("stop")) -ne 0) {
            Write-Host "  WARNING: The memory service did not stop. End MemoryMcp.exe from $ServiceRoot in Task Manager." -ForegroundColor Yellow
        }
        if ($hash) {
            Unregister-ServiceTask "MemoryMcp-$hash"
        } else {
            Write-Host "  WARNING: The launcher name could not be read; delete the MemoryMcp-* task in Task Scheduler." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  No shared-mode service is installed."
    }

    if (Test-Path -LiteralPath $ServicePathFile) {
        Remove-Item -LiteralPath $ServicePathFile -Force
    }
    Write-Host "Restart open agent sessions: they keep reporting that shared mode was switched off until restarted." -ForegroundColor Yellow
}

# Publishes a new service version and restarts the service on it with agent sessions left open (their relays
# reconnect). On a failure the previous service keeps running, or the message says how to start it.
function Update-SharedService {
    Write-Host "Updating the shared memory service..." -ForegroundColor Yellow
    if (-not (Test-Path -LiteralPath $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    while (Test-Path -LiteralPath (Join-Path $ServiceRoot $stamp)) {
        Start-Sleep -Seconds 1
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    }
    $serviceDir = Join-Path $ServiceRoot $stamp
    $serviceExe = Join-Path $serviceDir "MemoryMcp.exe"
    $serviceDll = Join-Path $serviceDir "MemoryMcp.dll"

    & dotnet publish "$RepoDir\src\MemoryMcp" `
        --configuration Release `
        --output "$serviceDir" `
        --verbosity quiet `
        -p:MemoryMcpWindowlessService=true | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $serviceDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "ERROR: Publish of the service failed. The installed service was not changed." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Published the service to: $serviceDir" -ForegroundColor Green

    $hash = Get-LauncherHash $serviceDll
    if (-not $hash) {
        Remove-Item -LiteralPath $serviceDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "ERROR: The new service did not report its launcher name. The installed service was not changed." -ForegroundColor Red
        exit 1
    }
    $taskName = "MemoryMcp-$hash"

    try {
        Register-ServiceTask $taskName $serviceExe
    } catch {
        Remove-Item -LiteralPath $serviceDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "ERROR: The scheduled task $taskName could not be registered: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  The installed service was not changed." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Registered scheduled task $taskName (at logon, current user, no window)" -ForegroundColor Green

    Set-Content -LiteralPath $ServicePathFile -Value $serviceExe -Encoding UTF8
    Write-Host "  Service path written to: $ServicePathFile" -ForegroundColor Green

    if ((Invoke-ServiceCommand $serviceDll @("stop")) -ne 0) {
        Write-Host "ERROR: The previous memory service did not stop; it keeps running and serving open sessions." -ForegroundColor Red
        Write-Host "  The new version is registered: it runs after the next logon, or run this install again." -ForegroundColor Red
        exit 1
    }

    if (Test-Path -LiteralPath $StopMarkerFile) {
        Remove-Item -LiteralPath $StopMarkerFile -Force
    }

    $statusExitCode = Invoke-ServiceCommand $serviceDll @("start")
    if ($statusExitCode -eq 0) {
        $statusExitCode = Invoke-ServiceCommand $serviceDll @("status")
    }
    if ($statusExitCode -ne 0) {
        Write-Host "ERROR: The memory service did not start. See the service log in $(Join-Path $DataDir 'logs')." -ForegroundColor Red
        Write-Host "  Open sessions wait for it. Start it with: `"$ServerExe`" start `"$DataDirArgument`", or run this install again." -ForegroundColor Red
        exit 1
    }

    # bin is left as it is while open sessions run from it; it then may still hold an older build, which keeps
    # starting standalone servers if it predates shared mode.
    $isBinOld = $false
    if (Test-DirectoryInUse $BinDir) {
        Write-Host "bin is in use by open sessions and was left as is; the service was updated" -ForegroundColor Yellow
        $isBinOld = -not (Test-BinMatchesBuild)
    } elseif ((Publish-Bin) -ne 0) {
        Write-Host "  WARNING: Publish to $BinDir failed; the service was updated." -ForegroundColor Yellow
        $isBinOld = $true
    } else {
        Write-Host "  Published to: $BinDir" -ForegroundColor Green
    }

    $isSwitchingOn = ($Shared -eq $true) -and -not $WasSharedEnabled
    if ($isSwitchingOn -and $isBinOld) {
        # The old bin cannot be a relay, so shared mode is not switched on in settings.json: no session would be shared.
        $script:FinalWarning = @(
            "Shared mode is NOT in effect yet: the memory service is installed and running, but bin was not updated",
            "because open agent sessions are using it. Sessions started now still run the previous build in bin, which",
            "runs standalone against the same database, and shared mode was not switched on in settings.json.",
            "Close all agent sessions, then run:",
            "  & `"$PSCommandPath`" -Shared `$true"
        )
    } else {
        if ($Shared -eq $true) {
            Set-SharedEnabled $true
            Write-Host "  Shared mode enabled in: $SettingsFile" -ForegroundColor Green
        }
        if ($isBinOld) {
            $script:FinalWarning = @(
                "bin was not updated because open agent sessions are using it. Sessions started now still run the",
                "previous build in bin; if that build predates shared mode, they run standalone against the same database.",
                "Close all agent sessions, then run:",
                "  & `"$PSCommandPath`""
            )
        }
    }

    foreach ($oldDirectory in (Get-ServiceDirectories | Select-Object -Skip 2)) {
        try {
            Remove-Item -LiteralPath $oldDirectory.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "  Removed old service directory: $($oldDirectory.FullName)"
        } catch {
            Write-Host "  Old service directory $($oldDirectory.FullName) is in use; a later install removes it." -ForegroundColor Yellow
        }
    }
}

$WasSharedEnabled = Get-SharedEnabled
$SharedOn = if ($null -ne $Shared) { [bool]$Shared } else { $WasSharedEnabled }
$FinalWarning = $null

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

# --- 2. Shared mode off (before publishing, so a failed publish cannot leave it half switched) ---
if ($Shared -eq $false) {
    Disable-SharedMode
    Write-Host ""
}

# --- 3. Publish ---
if ($SharedOn) {
    Write-Host "Shared mode is on: the service is published and $BinDir refreshed below." -ForegroundColor Green
} elseif ($Rebuild -or -not (Test-Path $ServerExe)) {
    if ($Rebuild) {
        Write-Host "Rebuild requested. Publishing..." -ForegroundColor Yellow
    } else {
        Write-Host "Publishing memory-mcp..." -ForegroundColor Yellow
    }

    $publishExitCode = Publish-Bin
    if ($publishExitCode -ne 0) {
        Write-Host "ERROR: Publish failed." -ForegroundColor Red
        exit $publishExitCode
    }

    Write-Host "  Published to: $BinDir" -ForegroundColor Green
} else {
    Write-Host "Binaries already exist at: $BinDir" -ForegroundColor Green
    Write-Host "  (use -Rebuild to force a fresh build)"
}
Write-Host ""

# --- 4. Ensure data directory exists ---
if (-not (Test-Path $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    Write-Host "Created data directory: $DataDir" -ForegroundColor Green
} else {
    Write-Host "Data directory exists: $DataDir" -ForegroundColor Green
}
Write-Host ""

# --- 5. User settings (survives rebuilds) ---
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

# --- 6. Shared mode service ---
if ($SharedOn) {
    Update-SharedService
    Write-Host ""
}

# --- 7. Ollama checks ---
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

# --- 8. MCP client configuration ---
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

if ($FinalWarning) {
    Write-Host ""
    Write-Host "=== WARNING ===" -ForegroundColor Red
    foreach ($line in $FinalWarning) {
        Write-Host $line -ForegroundColor Red
    }
}
