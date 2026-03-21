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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

REBUILD=false
SKIP_OLLAMA=false
DATA_ENCRYPTION=""
while [ $# -gt 0 ]; do
    case "$1" in
        --rebuild)      REBUILD=true ;;
        --skip-ollama)  SKIP_OLLAMA=true ;;
        --data-encryption)   DATA_ENCRYPTION="$2"; shift ;;
    esac
    shift
done

# --- Install paths ---
INSTALL_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/memory-mcp"
BIN_DIR="$INSTALL_ROOT/bin"
DATA_DIR="$INSTALL_ROOT/data"
SERVER_EXE="$BIN_DIR/MemoryMcp"

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

# --- 2. Publish ---
if [ "$REBUILD" = true ] || [ ! -f "$SERVER_EXE" ]; then
    if [ "$REBUILD" = true ]; then
        echo "Rebuild requested. Publishing..."
    else
        echo "Publishing memory-mcp..."
    fi

    mkdir -p "$BIN_DIR"

    dotnet publish "$REPO_DIR/src/MemoryMcp" \
        --configuration Release \
        --output "$BIN_DIR" \
        --verbosity quiet

    echo "  Published to: $BIN_DIR"
else
    echo "Binaries already exist at: $BIN_DIR"
    echo "  (use --rebuild to force a fresh build)"
fi
echo ""

# --- 3. Ensure data directory exists ---
mkdir -p "$DATA_DIR"
echo "Data directory: $DATA_DIR"
echo ""

# --- 4. User settings (survives rebuilds) ---
SETTINGS_FILE="$DATA_DIR/settings.json"
if [ -n "$DATA_ENCRYPTION" ]; then
    cat > "$SETTINGS_FILE" <<SETTINGS_EOF
{
  "MemoryMcp": {
    "Encryption": {
      "Enabled": $DATA_ENCRYPTION
    }
  }
}
SETTINGS_EOF
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

# --- 5. Ollama checks ---
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

# --- 6. MCP client configuration ---
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
