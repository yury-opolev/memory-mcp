#!/usr/bin/env bash
set -euo pipefail

# memory-mcp run script
# Builds and starts the MCP server with default settings.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "Building memory-mcp..."
dotnet build "$REPO_DIR/MemoryMcp.slnx" --configuration Release --verbosity quiet

echo "Starting memory-mcp server (stdio transport)..."
echo "Press Ctrl+C to stop."
echo ""

dotnet run --project "$REPO_DIR/src/MemoryMcp" --configuration Release --no-build
