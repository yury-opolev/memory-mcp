#!/usr/bin/env bash
set -euo pipefail

# memory-mcp run script
# Publishes (if needed) and starts the MCP server.
# Use --rebuild to force a fresh publish even if binaries exist.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PUBLISH_DIR="$REPO_DIR/publish"
SERVER_EXE="$PUBLISH_DIR/MemoryMcp"

REBUILD=false
for arg in "$@"; do
    case "$arg" in
        --rebuild) REBUILD=true ;;
    esac
done

# --- Publish step ---
if [ "$REBUILD" = true ] || [ ! -f "$SERVER_EXE" ]; then
    if [ "$REBUILD" = true ]; then
        echo "Rebuild requested. Publishing memory-mcp..."
    else
        echo "No published binaries found. Publishing memory-mcp..."
    fi

    dotnet publish "$REPO_DIR/src/MemoryMcp" \
        --configuration Release \
        --output "$PUBLISH_DIR" \
        --verbosity quiet

    echo "Published to: $PUBLISH_DIR"
else
    echo "Using existing published binaries at: $PUBLISH_DIR"
    echo "  (use --rebuild to force a fresh build)"
fi

echo ""

# --- MCP client setup instructions ---
echo "=== MCP Client Setup ==="
echo ""
echo "To use this server with an MCP client, add the following to your config:"
echo ""
echo "  OpenCode (opencode.jsonc):"
cat <<EOF
  {
    "\$schema": "https://opencode.ai/config.json",
    "mcp": {
      "memory": {
        "type": "local",
        "command": ["$SERVER_EXE"],
        "enabled": true
      }
    }
  }
EOF
echo ""
echo "  Claude Desktop / VS Code / Cursor (mcp.json):"
cat <<EOF
  {
    "mcpServers": {
      "memory": {
        "command": "$SERVER_EXE"
      }
    }
  }
EOF
echo ""
echo "=============================="
echo ""

# --- Start the server ---
echo "Starting memory-mcp server (stdio transport)..."
echo "Press Ctrl+C to stop."
echo ""

exec "$SERVER_EXE"
