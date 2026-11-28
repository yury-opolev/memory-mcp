#!/usr/bin/env bash
set -euo pipefail

# memory-mcp install script
# Builds, publishes, and installs memory-mcp to ~/.local/share/memory-mcp.
# Usage:
#   ./scripts/install.sh                    # build and install
#   ./scripts/install.sh --rebuild          # force fresh build
#   ./scripts/install.sh --skip-ollama      # skip Ollama checks
#   ./scripts/install.sh --data-encryption true     # enable data-at-rest encryption
#   ./scripts/install.sh --data-encryption false    # disable data-at-rest encryption
#   ./scripts/install.sh --shared true      # switch shared mode on: one memory service for all sessions
#   ./scripts/install.sh --shared false     # switch shared mode off (open agent sessions must be restarted)
#
# Without --shared, shared mode is left as it is; when it is on, every install updates the service.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

REBUILD=false
SKIP_OLLAMA=false
DATA_ENCRYPTION=""
SHARED=""
while [ $# -gt 0 ]; do
    case "$1" in
        --rebuild)      REBUILD=true ;;
        --skip-ollama)  SKIP_OLLAMA=true ;;
        --data-encryption)   DATA_ENCRYPTION="$2"; shift ;;
        --shared)       SHARED="${2:-missing}"; [ $# -gt 1 ] && shift ;;
    esac
    shift
done

if [ -n "$SHARED" ] && [ "$SHARED" != "true" ] && [ "$SHARED" != "false" ]; then
    echo "ERROR: --shared takes true or false."
    exit 1
fi

# --- Install paths ---
INSTALL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/memory-mcp"
BIN_DIR="$INSTALL_ROOT/bin"
DATA_DIR="$INSTALL_ROOT/data"
SERVER_EXE="$BIN_DIR/MemoryMcp"
SERVICE_ROOT="$INSTALL_ROOT/service"
SETTINGS_FILE="$DATA_DIR/settings.json"
SERVICE_PATH_FILE="$DATA_DIR/service-path.txt"
STOP_MARKER_FILE="$DATA_DIR/service.stopped"
DATA_DIR_ARGUMENT="--MemoryMcp:DataDirectory=$DATA_DIR"
SYSTEMD_UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
LAUNCH_AGENT_DIR="$HOME/Library/LaunchAgents"

# --- settings.json helpers (python3 or jq; every other key is kept) ---
json_tool_available() {
    command -v python3 &> /dev/null || command -v jq &> /dev/null
}

# Prints true or false: MemoryMcp.Shared.Enabled in settings.json.
settings_shared_enabled() {
    if [ ! -f "$SETTINGS_FILE" ]; then
        echo false
        return
    fi
    if command -v python3 &> /dev/null; then
        python3 - "$SETTINGS_FILE" <<'PY'
import json, sys
def get(node, key):
    return node.get(key) if isinstance(node, dict) else None
try:
    with open(sys.argv[1], encoding="utf-8-sig") as settings:
        value = get(get(get(json.load(settings), "MemoryMcp"), "Shared"), "Enabled")
except ValueError:
    value = None
print("true" if value is True or (isinstance(value, str) and value.lower() == "true") else "false")
PY
    elif command -v jq &> /dev/null; then
        local value
        value=$(jq -r '(.MemoryMcp.Shared.Enabled // false) | tostring | ascii_downcase' "$SETTINGS_FILE" 2> /dev/null || echo false)
        if [ "$value" = "true" ]; then echo true; else echo false; fi
    elif tr -d ' \t\r\n' < "$SETTINGS_FILE" | grep -qiE '"Shared":\{[^}]*"Enabled":(true|"true")'; then
        echo true
    else
        echo false
    fi
}

# settings_set_bool <section> <true|false>: sets MemoryMcp.<section>.Enabled.
settings_set_bool() {
    local section="$1" value="$2" temporary
    mkdir -p "$DATA_DIR"
    temporary="$SETTINGS_FILE.tmp.$$"
    if command -v python3 &> /dev/null; then
        python3 - "$SETTINGS_FILE" "$section" "$value" "$temporary" <<'PY'
import json, os, sys
path, section, value, temporary = sys.argv[1:5]
settings = {}
if os.path.exists(path):
    with open(path, encoding="utf-8-sig") as existing:
        text = existing.read()
    if text.strip():
        settings = json.loads(text)
if not isinstance(settings.get("MemoryMcp"), dict):
    settings["MemoryMcp"] = {}
if not isinstance(settings["MemoryMcp"].get(section), dict):
    settings["MemoryMcp"][section] = {}
settings["MemoryMcp"][section]["Enabled"] = value == "true"
with open(temporary, "w", encoding="utf-8") as updated:
    json.dump(settings, updated, indent=2)
    updated.write("\n")
PY
    elif [ -s "$SETTINGS_FILE" ]; then
        jq --arg section "$section" --argjson value "$value" '.MemoryMcp[$section].Enabled = $value' "$SETTINGS_FILE" > "$temporary"
    else
        jq -n --arg section "$section" --argjson value "$value" '{} | .MemoryMcp[$section].Enabled = $value' > "$temporary"
    fi
    mv "$temporary" "$SETTINGS_FILE"
}

# --- Shared mode helpers ---
# The full path of a file, following symbolic links.
resolve_path() {
    readlink -f "$1" 2> /dev/null || python3 -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$1" 2> /dev/null || echo "$1"
}

# Escapes a value for a double-quoted systemd unit setting.
systemd_escape_value() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/%/%%/g' -e 's/\$/$$/g'
}

xml_escape_value() {
    printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
}

# render_template <template> <output> <name> <value> ...: replaces every @name@ with its (already escaped) value.
render_template() {
    local template="$1" output="$2" content name value
    shift 2
    content="$(cat "$template")"
    shopt -u patsub_replacement 2> /dev/null || true
    while [ $# -gt 1 ]; do
        name="@$1@"
        value="$2"
        content="${content//$name/$value}"
        shift 2
    done
    printf '%s\n' "$content" > "$output"
}

systemd_user_available() {
    command -v systemctl &> /dev/null && systemctl --user show-environment &> /dev/null
}

# The <hash> in the launcher name, from the program (`status --print-hash`); empty when it does not report one.
launcher_hash() {
    local hash
    hash=$("$1" status "$DATA_DIR_ARGUMENT" --print-hash 2> /dev/null | tail -n 1 || true)
    if [[ "$hash" =~ ^[0-9a-f]{8}$ ]]; then
        echo "$hash"
    fi
}

# Service directories, newest first.
service_directories() {
    if [ -d "$SERVICE_ROOT" ]; then
        find "$SERVICE_ROOT" -mindepth 1 -maxdepth 1 -type d -name '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]' | sort -r
    fi
}

# The installed service executable, from service-path.txt or else the newest service directory; empty when none.
installed_service_exe() {
    local candidate directory
    if [ -f "$SERVICE_PATH_FILE" ]; then
        candidate="$(head -n 1 "$SERVICE_PATH_FILE")"
        if [ -n "$candidate" ] && [ -x "$candidate" ]; then
            echo "$candidate"
            return
        fi
    fi
    while IFS= read -r directory; do
        if [ -x "$directory/MemoryMcp" ]; then
            echo "$directory/MemoryMcp"
            return
        fi
    done < <(service_directories)
}

# True when one of this user's processes runs from, or has mapped a file of, the directory.
directory_in_use() {
    local directory proc
    [ -d "$1" ] || return 1
    directory="$(cd "$1" && pwd -P)"
    case "$(uname -s)" in
        Linux)
            for proc in /proc/[0-9]*; do
                case "$(readlink "$proc/exe" 2> /dev/null || true)" in
                    "$directory"/*) return 0 ;;
                esac
                if grep -qF -- "$directory/" "$proc/maps" 2> /dev/null; then
                    return 0
                fi
            done
            return 1
            ;;
        Darwin)
            lsof -t +D "$directory" &> /dev/null
            ;;
        *)
            return 1
            ;;
    esac
}

# register_launcher <hash> <service executable>: registers or re-registers the per-OS launcher the program starts the
# service through (LauncherNames in src/MemoryMcp/Shared/Launch).
register_launcher() {
    local hash="$1" service_exe="$2" dotnet_root
    dotnet_root="${DOTNET_ROOT:-$(dirname "$(resolve_path "$(command -v dotnet)")")}"
    case "$(uname -s)" in
        Linux)
            if ! systemd_user_available; then
                echo "  systemctl --user is not available: the service is started as a detached process (not at logon)."
                return 0
            fi
            local unit="memory-mcp-$hash.service"
            mkdir -p "$SYSTEMD_UNIT_DIR"
            render_template "$SCRIPT_DIR/shared/memory-mcp.service.template" "$SYSTEMD_UNIT_DIR/$unit" \
                DESCRIPTION "$(printf '%s' "$DATA_DIR" | sed -e 's/%/%%/g')" \
                DOTNET_ROOT "$(systemd_escape_value "$dotnet_root")" \
                SERVICE_EXE "$(systemd_escape_value "$service_exe")" \
                DATA_DIR "$(systemd_escape_value "$DATA_DIR")" || return 1
            systemctl --user daemon-reload || return 1
            systemctl --user enable "$unit" 2>&1 | sed 's/^/  /' || return 1
            echo "  Registered systemd user unit $unit ($SYSTEMD_UNIT_DIR/$unit)"
            ;;
        Darwin)
            local label="io.github.memory-mcp.$hash" domain
            local plist="$LAUNCH_AGENT_DIR/$label.plist"
            domain="gui/$(id -u)"
            mkdir -p "$LAUNCH_AGENT_DIR"
            render_template "$SCRIPT_DIR/shared/io.github.memory-mcp.plist.template" "$plist" \
                LABEL "$label" \
                SERVICE_EXE "$(xml_escape_value "$service_exe")" \
                DATA_DIR "$(xml_escape_value "$DATA_DIR")" \
                DOTNET_ROOT "$(xml_escape_value "$dotnet_root")" \
                HOME "$(xml_escape_value "$HOME")" || return 1
            launchctl bootout "$domain/$label" &> /dev/null || true
            launchctl bootstrap "$domain" "$plist" || return 1
            echo "  Registered launchd agent $label ($plist)"
            ;;
        *)
            echo "  No OS launcher on $(uname -s): the service is started as a detached process (not at logon)."
            ;;
    esac
}

unregister_launcher() {
    local hash="$1"
    case "$(uname -s)" in
        Linux)
            local unit="memory-mcp-$hash.service"
            if [ -f "$SYSTEMD_UNIT_DIR/$unit" ]; then
                if systemd_user_available; then
                    systemctl --user disable "$unit" 2>&1 | sed 's/^/  /' || true
                fi
                rm -f "$SYSTEMD_UNIT_DIR/$unit"
                if systemd_user_available; then
                    systemctl --user daemon-reload
                    systemctl --user reset-failed "$unit" &> /dev/null || true
                fi
                echo "  Removed systemd user unit $unit"
            else
                echo "  No systemd user unit $unit is registered."
            fi
            ;;
        Darwin)
            local label="io.github.memory-mcp.$hash"
            launchctl bootout "gui/$(id -u)/$label" &> /dev/null || true
            if [ -f "$LAUNCH_AGENT_DIR/$label.plist" ]; then
                rm -f "$LAUNCH_AGENT_DIR/$label.plist"
                echo "  Removed launchd agent $label"
            else
                echo "  No launchd agent $label is registered."
            fi
            ;;
    esac
}

publish_bin() {
    mkdir -p "$BIN_DIR"

    dotnet publish "$REPO_DIR/src/MemoryMcp" \
        --configuration Release \
        --output "$BIN_DIR" \
        --verbosity quiet
}

disable_shared_mode() {
    echo "Switching shared mode off..."
    settings_set_bool Shared false
    echo "  Shared mode disabled in: $SETTINGS_FILE"

    local service_exe hash
    service_exe="$(installed_service_exe)"
    if [ -n "$service_exe" ]; then
        export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(resolve_path "$(command -v dotnet)")")}"
        hash="$(launcher_hash "$service_exe")"
        if ! "$service_exe" stop "$DATA_DIR_ARGUMENT"; then
            echo "  WARNING: The memory service did not stop. End the MemoryMcp process running from $SERVICE_ROOT."
        fi
        if [ -n "$hash" ]; then
            unregister_launcher "$hash"
        else
            echo "  WARNING: The launcher name could not be read; remove the memory-mcp-* launcher by hand."
        fi
    else
        echo "  No shared-mode service is installed."
    fi

    rm -f "$SERVICE_PATH_FILE"
    echo "Restart open agent sessions: they keep reporting that shared mode was switched off until restarted."
}

# Publishes a new service version and restarts the service on it with agent sessions left open (their relays
# reconnect). On a failure the previous service keeps running, or the message says how to start it.
update_shared_service() {
    echo "Updating the shared memory service..."
    mkdir -p "$DATA_DIR"
    # The service and its commands find the .NET runtime through DOTNET_ROOT, also when dotnet is not installed
    # system-wide; the launcher passes the same value on.
    export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(resolve_path "$(command -v dotnet)")")}"

    local stamp service_dir service_exe hash old_directory
    stamp="$(date +%Y%m%d-%H%M%S)"
    while [ -e "$SERVICE_ROOT/$stamp" ]; do
        sleep 1
        stamp="$(date +%Y%m%d-%H%M%S)"
    done
    service_dir="$SERVICE_ROOT/$stamp"
    service_exe="$service_dir/MemoryMcp"

    if ! dotnet publish "$REPO_DIR/src/MemoryMcp" \
        --configuration Release \
        --output "$service_dir" \
        --verbosity quiet; then
        rm -rf "$service_dir"
        echo "ERROR: Publish of the service failed. The installed service was not changed."
        exit 1
    fi
    echo "  Published the service to: $service_dir"

    hash="$(launcher_hash "$service_exe")"
    if [ -z "$hash" ]; then
        rm -rf "$service_dir"
        echo "ERROR: The new service did not report its launcher name. The installed service was not changed."
        exit 1
    fi

    if ! register_launcher "$hash" "$service_exe"; then
        echo "ERROR: The launcher could not be registered. The previous service keeps running."
        exit 1
    fi

    printf '%s\n' "$service_exe" > "$SERVICE_PATH_FILE"
    echo "  Service path written to: $SERVICE_PATH_FILE"

    if ! "$service_exe" stop "$DATA_DIR_ARGUMENT"; then
        echo "ERROR: The previous memory service did not stop; it keeps running and serving open sessions."
        echo "  The new version is registered: it runs after the next login, or run this install again."
        exit 1
    fi

    rm -f "$STOP_MARKER_FILE"

    if ! "$service_exe" start "$DATA_DIR_ARGUMENT" || ! "$service_exe" status "$DATA_DIR_ARGUMENT"; then
        echo "ERROR: The memory service did not start. See the service log in $DATA_DIR/logs."
        echo "  Open sessions wait for it. Start it with: \"$SERVER_EXE\" start \"$DATA_DIR_ARGUMENT\", or run this install again."
        exit 1
    fi

    # bin is left as it is while open sessions run from it; it then may still hold an older build, which keeps
    # starting standalone servers if it predates shared mode.
    # The service build is the build bin should hold (they differ only on Windows). It is copied over bin rather than
    # published into it: a publish skips files whose copy in bin is newer, which an older build can be.
    local is_bin_old=false
    if directory_in_use "$BIN_DIR"; then
        echo "bin is in use by open sessions and was left as is; the service was updated"
        if ! cmp -s "$BIN_DIR/MemoryMcp.dll" "$service_dir/MemoryMcp.dll"; then
            is_bin_old=true
        fi
    elif mkdir -p "$BIN_DIR" && cp -R -f "$service_dir/." "$BIN_DIR/"; then
        echo "  Published to: $BIN_DIR"
    else
        echo "  WARNING: Copying the build to $BIN_DIR failed; the service was updated."
        is_bin_old=true
    fi

    if [ "$SHARED" = "true" ] && [ "$WAS_SHARED_ENABLED" != "true" ] && [ "$is_bin_old" = true ]; then
        # The old bin cannot be a relay, so shared mode is not switched on in settings.json: no session would be shared.
        FINAL_WARNING="Shared mode is NOT in effect yet: the memory service is installed and running, but bin was not updated
because open agent sessions are using it. Sessions started now still run the previous build in bin, which
runs standalone against the same database, and shared mode was not switched on in settings.json.
Close all agent sessions, then run:
  bash \"$SCRIPT_DIR/install.sh\" --shared true"
    else
        if [ "$SHARED" = "true" ]; then
            settings_set_bool Shared true
            echo "  Shared mode enabled in: $SETTINGS_FILE"
        fi
        if [ "$is_bin_old" = true ]; then
            FINAL_WARNING="bin was not updated because open agent sessions are using it. Sessions started now still run the
previous build in bin; if that build predates shared mode, they run standalone against the same database.
Close all agent sessions, then run:
  bash \"$SCRIPT_DIR/install.sh\""
        fi
    fi

    while IFS= read -r old_directory; do
        if rm -rf "$old_directory"; then
            echo "  Removed old service directory: $old_directory"
        else
            echo "  Old service directory $old_directory could not be removed; a later install removes it."
        fi
    done < <(service_directories | tail -n +3)
}

if [ -n "$SHARED" ] && ! json_tool_available; then
    echo "ERROR: --shared needs python3 or jq to update $SETTINGS_FILE without losing its other settings."
    exit 1
fi

WAS_SHARED_ENABLED="$(settings_shared_enabled)"
if [ -n "$SHARED" ]; then
    SHARED_ON="$SHARED"
else
    SHARED_ON="$WAS_SHARED_ENABLED"
fi
FINAL_WARNING=""

echo "=== memory-mcp install ==="
echo ""
echo "Install root : $INSTALL_ROOT"
echo "Binaries     : $BIN_DIR"
echo "Data         : $DATA_DIR"
echo ""

# --- 1. Check .NET SDK ---
echo "Checking for .NET SDK..."
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found. Install from https://dot.net/download"
    exit 1
fi
DOTNET_VERSION=$(dotnet --version)
echo "  Found .NET SDK $DOTNET_VERSION"
echo ""

# --- 2. Shared mode off (before publishing, so a failed publish cannot leave it half switched) ---
if [ "$SHARED" = "false" ]; then
    disable_shared_mode
    echo ""
fi

# --- 3. Publish ---
if [ "$SHARED_ON" = true ]; then
    echo "Shared mode is on: the service is published and $BIN_DIR refreshed below."
elif [ "$REBUILD" = true ] || [ ! -f "$SERVER_EXE" ]; then
    if [ "$REBUILD" = true ]; then
        echo "Rebuild requested. Publishing..."
    else
        echo "Publishing memory-mcp..."
    fi

    publish_bin

    echo "  Published to: $BIN_DIR"
else
    echo "Binaries already exist at: $BIN_DIR"
    echo "  (use --rebuild to force a fresh build)"
fi
echo ""

# --- 4. Ensure data directory exists ---
mkdir -p "$DATA_DIR"
echo "Data directory: $DATA_DIR"
echo ""

# --- 5. User settings (survives rebuilds) ---
if [ -n "$DATA_ENCRYPTION" ]; then
    if json_tool_available; then
        settings_set_bool Encryption "$DATA_ENCRYPTION"
    else
        cat > "$SETTINGS_FILE" <<SETTINGS_EOF
{
  "MemoryMcp": {
    "Encryption": {
      "Enabled": $DATA_ENCRYPTION
    }
  }
}
SETTINGS_EOF
    fi
    if [ "$DATA_ENCRYPTION" = "true" ]; then
        echo "Encryption enabled in: $SETTINGS_FILE"
    else
        echo "Encryption disabled in: $SETTINGS_FILE"
    fi
elif [ -f "$SETTINGS_FILE" ]; then
    echo "User settings: $SETTINGS_FILE"
else
    echo "No user settings file (use --data-encryption true or --data-encryption false to configure)"
fi
echo ""

# --- 6. Shared mode service ---
if [ "$SHARED_ON" = true ]; then
    update_shared_service
    echo ""
fi

# --- 7. Ollama checks ---
if [ "$SKIP_OLLAMA" = false ]; then
    echo "Checking for Ollama..."
    if ! command -v ollama &> /dev/null; then
        echo "  WARNING: Ollama not found. Install from https://ollama.com/download"
        echo "  The server will start but embedding tools will fail without Ollama."
        echo ""
    else
        OLLAMA_VERSION=$(ollama --version 2>&1 || true)
        echo "  Found Ollama: $OLLAMA_VERSION"

        OLLAMA_RUNNING=false
        if curl -s http://localhost:11434/api/tags &> /dev/null; then
            OLLAMA_RUNNING=true
            echo "  Ollama is running."
        else
            echo "  WARNING: Ollama is not running. Start it with: ollama serve"
        fi

        if [ "$OLLAMA_RUNNING" = true ]; then
            MODEL="qwen3-embedding:0.6b"
            echo "  Pulling embedding model '$MODEL'..."
            ollama pull "$MODEL"
            echo "  Model '$MODEL' is ready."
        fi
        echo ""
    fi
fi

# --- 8. MCP client configuration ---
EXE_PATH_JSON=$(echo "$SERVER_EXE" | sed 's/\\/\\\\/g')
DATA_DIR_JSON=$(echo "$DATA_DIR" | sed 's/\\/\\\\/g')

echo "=== MCP Client Configuration ==="
echo ""
echo "Claude Code (~/.claude.json or project .mcp.json):"
cat <<EOF
  {
    "mcpServers": {
      "memory": {
        "command": "$EXE_PATH_JSON",
        "env": {
          "MemoryMcp__DataDirectory": "$DATA_DIR_JSON"
        }
      }
    }
  }
EOF
echo ""
echo "VS Code / Cursor:"
cat <<EOF
  {
    "mcpServers": {
      "memory": {
        "command": "$EXE_PATH_JSON",
        "env": {
          "MemoryMcp__DataDirectory": "$DATA_DIR_JSON"
        }
      }
    }
  }
EOF
echo ""
echo "OpenCode (opencode.jsonc):"
cat <<EOF
  {
    "\$schema": "https://opencode.ai/config.json",
    "mcp": {
      "memory": {
        "type": "local",
        "command": ["$EXE_PATH_JSON"],
        "env": {
          "MemoryMcp__DataDirectory": "$DATA_DIR_JSON"
        },
        "enabled": true
      }
    }
  }
EOF
echo ""
echo "=== Install complete ==="
echo ""
echo "Binaries : $SERVER_EXE"
echo "Data     : $DATA_DIR"
echo "Settings : $SETTINGS_FILE"
echo ""
echo "Make sure Ollama is running (ollama serve) before using the MCP server."

if [ -n "$FINAL_WARNING" ]; then
    echo ""
    echo "=== WARNING ==="
    printf '%s\n' "$FINAL_WARNING"
fi
