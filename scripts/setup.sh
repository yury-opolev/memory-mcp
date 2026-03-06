#!/usr/bin/env bash
set -euo pipefail

# memory-mcp setup script
# Restores .NET dependencies, checks for Ollama, and pulls the embedding model.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "=== memory-mcp setup ==="
echo ""

# 1. Check for .NET SDK
echo "Checking for .NET SDK..."
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found."
    echo "Install .NET 10 SDK from https://dot.net/download"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
echo "Found .NET SDK $DOTNET_VERSION"
echo ""

# 2. Restore NuGet packages
echo "Restoring NuGet packages..."
dotnet restore "$REPO_DIR/MemoryMcp.slnx"
echo "NuGet packages restored."
echo ""

# 3. Build the solution
echo "Building solution..."
dotnet build "$REPO_DIR/MemoryMcp.slnx" --no-restore
echo "Build succeeded."
echo ""

# 4. Check for Ollama
echo "Checking for Ollama..."
if ! command -v ollama &> /dev/null; then
    echo "WARNING: Ollama not found."
    echo "Install Ollama to enable semantic search:"
    echo "  Linux:  curl -fsSL https://ollama.com/install.sh | sh"
    echo "  macOS:  brew install ollama"
    echo ""
    echo "After installing, run: ollama serve"
    echo "Then re-run this setup script to pull the embedding model."
    echo ""
    echo "Setup completed (without Ollama)."
    exit 0
fi

OLLAMA_VERSION=$(ollama --version 2>&1 || true)
echo "Found Ollama: $OLLAMA_VERSION"
echo ""

# 5. Check if Ollama is running
echo "Checking if Ollama is running..."
if ! curl -s http://localhost:11434/api/tags &> /dev/null; then
    echo "WARNING: Ollama does not appear to be running."
    echo "Start it with: ollama serve"
    echo ""
    echo "Setup completed (Ollama not running, model not pulled)."
    exit 0
fi

echo "Ollama is running."
echo ""

# 6. Pull the embedding model
MODEL="qwen3-embedding:0.6b"
echo "Pulling embedding model '$MODEL'..."
ollama pull "$MODEL"
echo "Model '$MODEL' is ready."
echo ""

echo "=== Setup complete ==="
echo "Run the server with: ./scripts/run.sh"
