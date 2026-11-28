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
    is updated). Shared mode needs PowerShell 7 or later (pwsh).
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
$LogDir = Join-Path $DataDir "logs"
$IsPowerShell7 = $PSVersionTable.PSVersion.Major -ge 7

# --- settings.json: read once, before anything is changed ---
# $UserSettings is the parsed file (a hashtable on PowerShell 7, which every change goes through); $SettingsError says
# why it could not be read. PowerShell 5.1 has no ConvertFrom-Json -AsHashtable, so there it is parsed only to see
# whether shared mode is on.
$UserSettings = @{}
$SettingsError = $null
$HasSettingsContent = $false
if (Test-Path -LiteralPath $SettingsFile) {
    $rawSettings = Get-Content -LiteralPath $SettingsFile -Raw
    if ($rawSettings -and $rawSettings.Trim()) {
        $HasSettingsContent = $true
        try {
            if ($IsPowerShell7) {
                $UserSettings = $rawSettings | ConvertFrom-Json -AsHashtable -ErrorAction Stop
            } else {
                $UserSettings = $rawSettings | ConvertFrom-Json -ErrorAction Stop
            }
            if ($null -eq $UserSettings) {
                $UserSettings = @{}
            }
        } catch {
            $SettingsError = $_.Exception.Message
            $UserSettings = @{}
        }
    }
}

# --- Shared mode helpers ---
function Get-SharedEnabled {
    # Member access reads a hashtable key or an object property alike, and gives $null for a missing level.
    $enabled = $UserSettings.MemoryMcp.Shared.Enabled
    return "$enabled" -eq "true"
}

function Save-UserSettings {
    if (-not (Test-Path -LiteralPath $DataDir)) {
        New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    }
    $UserSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SettingsFile -Encoding UTF8
}

# Sets MemoryMcp.<section>.Enabled in the settings read at the start and writes them; every other key is kept.
function Set-SectionEnabled([string]$section, [bool]$enabled) {
    if (-not ($UserSettings["MemoryMcp"] -is [System.Collections.IDictionary])) {
        $UserSettings["MemoryMcp"] = @{}
    }
    if (-not ($UserSettings["MemoryMcp"][$section] -is [System.Collections.IDictionary])) {
        $UserSettings["MemoryMcp"][$section] = @{}
    }
    $UserSettings["MemoryMcp"][$section]["Enabled"] = $enabled
    Save-UserSettings
}

function Set-SharedEnabled([bool]$enabled) {
    Set-SectionEnabled "Shared" $enabled
}

# The exact command that starts the service of this data directory with the given service copy.
function Get-StartCommandText([string]$serviceDll) {
    return "dotnet `"$serviceDll`" start `"$DataDirArgument`""
}

# Runs `status` until the service is ready (exit 0), while it reports that it is still starting (bounded to 120
# seconds); prints what it reports. Returns "ready", "failed" (it runs, but its store failed to start; the error and
# the log directory are printed) or "not ready" (not running, still starting after the bound, or not answering).
function Wait-ServiceReady([string]$serviceDll) {
    $deadline = (Get-Date).AddSeconds(120)
    while ($true) {
        $output = @(& dotnet $serviceDll status $DataDirArgument)
        $exitCode = $LASTEXITCODE
        $state = "$($output | Select-Object -First 1)".Trim()
        if ($exitCode -eq 0) {
            $output | Out-Host
            return "ready"
        }
        if ($exitCode -eq 4 -and $state -eq "running, still starting" -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            continue
        }
        $output | Out-Host
        if ($state -eq "running, startup failed") {
            $startupError = "$($output | Where-Object { "$_".StartsWith("StartupError: ") } | Select-Object -First 1)"
            Write-Host "ERROR: The memory service runs, but its store failed to start: $($startupError -replace '^StartupError: ', '')" -ForegroundColor Red
            Write-Host "  See the service log in $LogDir" -ForegroundColor Red
            return "failed"
        }
        if ($state -eq "running, still starting") {
            Write-Host "ERROR: The memory service was still starting after 120 seconds. See the service log in $LogDir" -ForegroundColor Red
        }
        return "not ready"
    }
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

# True when a file in the directory is open in a process that does not share it, as while a process runs from it.
# Only a sharing or lock violation counts: a read-only file (access denied) is not in use.
function Test-DirectoryInUse([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory)) {
        return $false
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
        try {
            $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $stream.Dispose()
        } catch [System.IO.IOException] {
            # ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).
            if (($_.Exception.HResult -band 0xFFFF) -in @(32, 33)) {
                return $true
            }
        } catch {
            # Access denied (a read-only file) or anything else: not a sign that a process uses the file.
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

# Publishes the console build that bin should hold into a fresh directory; returns its path, or null on a failure.
# (The service copy cannot stand in for it: its windowless build differs in the assembly itself.)
function Publish-BinBuild {
    $buildDir = Join-Path $InstallRoot "bin-build"
    if (Test-Path -LiteralPath $buildDir) {
        Remove-Item -LiteralPath $buildDir -Recurse -Force
    }
    & dotnet publish "$RepoDir\src\MemoryMcp" `
        --configuration Release `
        --output "$buildDir" `
        --verbosity quiet | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $buildDir "MemoryMcp.dll"))) {
        Remove-Item -LiteralPath $buildDir -Recurse -Force -ErrorAction SilentlyContinue
        return $null
    }
    return $buildDir
}

function Test-SameFile([string]$first, [string]$second) {
    if (-not (Test-Path -LiteralPath $first) -or -not (Test-Path -LiteralPath $second)) {
        return $false
    }
    return (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
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
        # The stop either got no answer or the service was asked to stop and has not exited. The stop marker is
        # withdrawn only when the previous service is confirmed alive and serving; otherwise it stays, and open
        # sessions report the service as stopped until it is started.
        & dotnet $serviceDll status $DataDirArgument | Out-Null
        if ($LASTEXITCODE -eq 0) {
            if (Test-Path -LiteralPath $StopMarkerFile) {
                Remove-Item -LiteralPath $StopMarkerFile -Force
            }
            Write-Host "ERROR: The previous memory service did not stop and is still running and serving open sessions; the stop was withdrawn." -ForegroundColor Red
            Write-Host "  The new version is registered: it runs after the next logon, or run this install again." -ForegroundColor Red
        } else {
            Write-Host "ERROR: The previous memory service was asked to stop and has not exited yet. Open sessions report it as stopped." -ForegroundColor Red
            Write-Host "  It stays stopped until it is started. When it has exited, start it with:" -ForegroundColor Red
            Write-Host "    $(Get-StartCommandText $serviceDll)" -ForegroundColor Red
            Write-Host "  or run this install again." -ForegroundColor Red
        }
        exit 1
    }

    if (Test-Path -LiteralPath $StopMarkerFile) {
        Remove-Item -LiteralPath $StopMarkerFile -Force
    }

    $readiness = "not ready"
    if ((Invoke-ServiceCommand $serviceDll @("start")) -eq 0) {
        $readiness = Wait-ServiceReady $serviceDll
    }
    if ($readiness -eq "failed") {
        Write-Host "  Fix the cause; the running service retries its startup on the next tool call that comes 5 seconds or" -ForegroundColor Red
        Write-Host "  more after the failure. Or run this install again." -ForegroundColor Red
        exit 1
    }
    if ($readiness -ne "ready") {
        Write-Host "ERROR: The memory service did not start. See the service log in $LogDir." -ForegroundColor Red
        Write-Host "  Open sessions wait for it. Start it with:" -ForegroundColor Red
        Write-Host "    $(Get-StartCommandText $serviceDll)" -ForegroundColor Red
        Write-Host "  or run this install again." -ForegroundColor Red
        exit 1
    }

    # bin is left as it is while open sessions run from it; it then may still hold an older build, which keeps
    # starting standalone servers if it predates shared mode.
    # The build is copied over bin rather than published into it: a publish skips files whose copy in bin is newer,
    # which an older build can be, and then bin would silently keep it.
    $isBinOld = $false
    $binBuildDir = Publish-BinBuild
    if (-not $binBuildDir) {
        Write-Host "  WARNING: The build for $BinDir failed; bin was left as is and the service was updated." -ForegroundColor Yellow
        $isBinOld = $true
    } elseif (Test-DirectoryInUse $BinDir) {
        Write-Host "bin is in use by open sessions and was left as is; the service was updated" -ForegroundColor Yellow
        $isBinOld = -not (Test-SameFile (Join-Path $BinDir "MemoryMcp.dll") (Join-Path $binBuildDir "MemoryMcp.dll"))
    } else {
        try {
            New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
            Copy-Item -Path (Join-Path $binBuildDir "*") -Destination $BinDir -Recurse -Force -ErrorAction Stop
            Write-Host "  Published to: $BinDir" -ForegroundColor Green
        } catch {
            Write-Host "  WARNING: Copying the build to $BinDir failed: $($_.Exception.Message)" -ForegroundColor Yellow
            $isBinOld = $true
        }
    }
    if ($binBuildDir) {
        Remove-Item -LiteralPath $binBuildDir -Recurse -Force -ErrorAction SilentlyContinue
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

# --- Checks before anything is changed ---
if (($null -ne $Shared -or $WasSharedEnabled) -and -not $IsPowerShell7) {
    Write-Host "ERROR: Shared mode needs PowerShell 7 or later; this is PowerShell $($PSVersionTable.PSVersion)." -ForegroundColor Red
    Write-Host "  Open PowerShell 7 (pwsh) and run the same command there: & `"$PSCommandPath`" ..." -ForegroundColor Red
    Write-Host "  Nothing was changed." -ForegroundColor Red
    exit 1
}
if ($null -ne $DataEncryption -and $HasSettingsContent -and -not $IsPowerShell7) {
    Write-Host "ERROR: Changing -DataEncryption in an existing $SettingsFile needs PowerShell 7 or later (pwsh)." -ForegroundColor Red
    Write-Host "  Nothing was changed." -ForegroundColor Red
    exit 1
}
if ($SettingsError) {
    if ($null -ne $Shared -or $null -ne $DataEncryption) {
        Write-Host "ERROR: $SettingsFile is not valid JSON: $SettingsError" -ForegroundColor Red
        Write-Host "  Fix or remove it and run this install again. Nothing was changed." -ForegroundColor Red
        exit 1
    }
    Write-Host "WARNING: $SettingsFile is not valid JSON ($SettingsError); this install treats shared mode as off." -ForegroundColor Yellow
}

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
    # The settings read at the start, with every other key kept.
    if (-not $IsPowerShell7) {
        # PowerShell 5.1 only gets here without an existing settings.json (checked above): start fresh.
        $UserSettings = @{}
    }
    Set-SectionEnabled "Encryption" ([bool]$DataEncryption)
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
