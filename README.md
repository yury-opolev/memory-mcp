# memory-mcp

AI assistants forget everything between conversations. **memory-mcp** fixes that. It's a local server that gives any AI coding agent (Claude Code, Cursor, VS Code Copilot, OpenCode, etc.) the ability to **remember things** across sessions — your preferences, project context, decisions, anything you tell it to save. Memories are stored on your machine, never sent to the cloud, and found later using plain-English search. Think of it as a personal knowledge base that your AI assistant can read and write to.

**Why use it:**
- **Persistent memory** — your AI remembers what you told it yesterday, last week, or last month
- **Semantic search** — find memories by meaning, not exact keywords ("how we handle auth" finds notes about authentication flow)
- **Fully local** — all data stays on your machine, nothing leaves your network
- **Works with any MCP client** — Claude Code, Cursor, VS Code, OpenCode, and more
- **Optional encryption** — AES-256-GCM encryption at rest for sensitive data

Built with .NET 10, [Ollama](https://ollama.com) for embeddings, and [sqlite-vec](https://github.com/asg017/sqlite-vec) for vector search. Communicates over stdio using the [Model Context Protocol](https://modelcontextprotocol.io).

## How It Works

When you store a memory, the server:

1. **Chunks** the text into overlapping word-based segments (default: 512 words, 64-word overlap).
2. **Embeds** each chunk into a 1024-dimensional vector using a local Ollama embedding model.
3. **Stores** the vectors in a sqlite-vec virtual table, chunk metadata in a regular SQLite table, and the full original text in a plain file on disk.

When you search, the server embeds your query with the same model, runs a cosine-similarity nearest-neighbor search across all chunk vectors, groups results by memory, and returns the best matches ranked by score.

```
                        +-----------------+
  MCP Client            |   MemoryTools   |    5 MCP tools (stdio)
  (AI agent)  <--json-->|   (MCP layer)   |
                        +--------+--------+
                                 |
                        +--------v--------+
                        |  MemoryService  |    orchestration
                        +--+-----------+--+
                           |           |
              +------------+     +-----+----------+
              |                  |                 |
    +---------v-------+  +------v------+  +-------v--------+
    | ChunkingService |  |  Embedding  |  |  MemoryStore   |
    | (word-based)    |  |  Service    |  |  (SQLite +     |
    +-----------------+  |  (Ollama)   |  |   sqlite-vec + |
                         +------+------+  |   files)       |
                                |         +----------------+
                         +------v------+
                         |   Ollama    |    local process
                         |  (external) |    port 11434
                         +-------------+
```

## Quick Install

The install script builds the server, checks for Ollama, pulls the embedding model, and prints the config snippet you need to paste into your MCP client. One command does everything.

**Prerequisites:** [.NET 10 SDK](https://dot.net/download) and [Ollama](https://ollama.com/download).

**Linux / macOS:**

```sh
git clone https://github.com/yury-opolev/memory-mcp.git
cd memory-mcp
chmod +x scripts/install.sh
./scripts/install.sh
```

**Windows (PowerShell):**

```powershell
git clone https://github.com/yury-opolev/memory-mcp.git
cd memory-mcp
.\scripts\install.ps1
```

The install script will:
1. Verify the .NET SDK is installed
2. Build and publish the server to a standard location (`~/.local/share/memory-mcp` on Linux/macOS, `%LOCALAPPDATA%\memory-mcp` on Windows)
3. Check if Ollama is installed and running
4. Pull the default embedding model (`qwen3-embedding:0.6b`)
5. Print ready-to-paste MCP configuration snippets for Claude Code, VS Code / Cursor, and OpenCode

After the script finishes, just copy the config snippet into your MCP client and you're done.

| Flag | Description |
|------|-------------|
| `--rebuild` / `-Rebuild` | Force a fresh build even if binaries already exist |
| `--skip-ollama` / `-SkipOllama` | Skip Ollama checks and model pull |
| `--data-encryption true` / `-DataEncryption $true` | Enable AES-256-GCM encryption at rest |

### MCP Client Configuration

The install script prints the exact config for your platform. Here are examples (replace the paths with the actual output from the script):

**Claude Code** (`~/.claude.json` or project `.mcp.json`):

```json
{
  "mcpServers": {
    "memory": {
      "command": "/home/you/.local/share/memory-mcp/bin/MemoryMcp",
      "env": {
        "MemoryMcp__DataDirectory": "/home/you/.local/share/memory-mcp/data"
      }
    }
  }
}
```

**VS Code / Cursor** (`.vscode/mcp.json` or user settings):

```json
{
  "mcpServers": {
    "memory": {
      "command": "/home/you/.local/share/memory-mcp/bin/MemoryMcp",
      "env": {
        "MemoryMcp__DataDirectory": "/home/you/.local/share/memory-mcp/data"
      }
    }
  }
}
```

**OpenCode** (`opencode.jsonc`):

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "memory": {
      "type": "local",
      "command": ["/home/you/.local/share/memory-mcp/bin/MemoryMcp"],
      "env": {
        "MemoryMcp__DataDirectory": "/home/you/.local/share/memory-mcp/data"
      },
      "enabled": true
    }
  }
}
```

### Ollama Setup

[Ollama](https://ollama.com) runs open-source AI models locally. This project uses it solely for generating text embeddings — it does not use an LLM for chat. The install script handles pulling the model, but you need Ollama installed and running first.

| Platform | Install |
|----------|---------|
| macOS | `brew install ollama` or download from [ollama.com/download](https://ollama.com/download) |
| Linux | `curl -fsSL https://ollama.com/install.sh \| sh` |
| Windows | Download from [ollama.com/download](https://ollama.com/download) |

Start it with `ollama serve`. It listens on `http://localhost:11434` by default.

### Manual Setup

If you prefer not to use the install script:

```sh
git clone https://github.com/yury-opolev/memory-mcp.git
cd memory-mcp
dotnet build

# Make sure Ollama is running with the model pulled
ollama serve &
ollama pull qwen3-embedding:0.6b

# Run the server (stdio transport)
dotnet run --project src/MemoryMcp
```

### Startup Health Check

On startup, the server checks whether Ollama is reachable and whether the configured embedding model is available. If either check fails, the server logs a warning but **does not crash** — it starts normally so that non-embedding tools (`get_memory`, `delete_memory`) remain available. Embedding-dependent tools (`ingest_memory`, `update_memory`, `search_memory`) return a clear error message if Ollama is unavailable at call time.

## MCP Tools

The server exposes five tools:

### `ingest_memory`

Store new content as a memory. The text is chunked, embedded, and indexed.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `content` | string | yes | The text to store |
| `title` | string | no | Short label for the memory |
| `tags` | string | no | JSON array of strings, e.g. `["project", "notes"]` |

Returns the assigned memory ID (GUID).

### `get_memory`

Retrieve a memory by ID, including full content, title, tags, and timestamps.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | string | yes | Memory ID (GUID) |

### `update_memory`

Update an existing memory. If content is provided, it is re-chunked and re-embedded. Title and tags can be updated independently without re-embedding.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | string | yes | Memory ID (GUID) |
| `content` | string | no | New text (triggers re-chunking and re-embedding) |
| `title` | string | no | New title |
| `tags` | string | no | New tags as JSON array |

### `delete_memory`

Permanently delete a memory and all its chunks.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | string | yes | Memory ID (GUID) |

### `search_memory`

Semantic search across all stored memories. Returns results ranked by cosine similarity.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | string | yes | Natural-language search text |
| `limit` | int | no | Max results (default: 5) |
| `minScore` | float | no | Minimum similarity threshold, 0.0--1.0 |
| `tags` | string | no | Filter: only return memories with at least one matching tag |

Long results are truncated at 1800 characters with a hint to use `get_memory` for the full text.

## Architecture

The solution has two projects:

- **`MemoryMcp.Core`** -- library containing all business logic (chunking, embedding, storage, orchestration). No dependency on MCP.
- **`MemoryMcp`** -- thin MCP host that wires up DI and exposes Core services as MCP tools over stdio.

### Storage Model

Data is stored in three places:

| What | Where | Why |
|------|-------|-----|
| Chunk metadata | SQLite `chunks` table | Queryable structured data (title, tags, timestamps, offsets) |
| Embedding vectors | SQLite `chunks_vec` virtual table (sqlite-vec) | Fast cosine-similarity nearest-neighbor search |
| Full content | `{DataDirectory}/memories/{id}.memory.data` files | Avoids storing large text blobs in the database |

**SQLite schema:**

```sql
-- Chunk metadata
CREATE TABLE chunks (
    MemoryId    TEXT    NOT NULL,
    ChunkIndex  INTEGER NOT NULL,
    StartOffset INTEGER NOT NULL,
    Length      INTEGER NOT NULL,
    Title       TEXT,
    Tags        TEXT    DEFAULT '[]',   -- JSON array
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL,
    PRIMARY KEY (MemoryId, ChunkIndex)
);

-- Vector index (sqlite-vec)
CREATE VIRTUAL TABLE chunks_vec USING vec0(
    ChunkKey TEXT PRIMARY KEY,          -- "{memoryId}:{chunkIndex}"
    Vector   float[1024]                -- dimension from config
);
```

Memory-level metadata (title, tags, timestamps) is duplicated across all chunks of the same memory. This avoids joins during vector search and keeps queries simple.

### Chunking

Text is split by whitespace into words, then grouped into overlapping windows. Default: 512 words per chunk, 64 words overlap. This uses a rough ~1.3 tokens/word heuristic rather than a model-specific tokenizer, making it model-agnostic.

### Search Pipeline

1. Embed the query text with Ollama.
2. Query sqlite-vec for the nearest `limit * 10` chunk vectors (overfetch to account for grouping and filtering).
3. Convert cosine distance to similarity: `score = 1 - distance`.
4. Apply `minScore` and tag filters.
5. Group by memory ID, keeping the best chunk score per memory.
6. Sort by score descending, return top `limit` results with content loaded from files.

## Configuration

All settings live in `src/MemoryMcp/appsettings.json` and can be overridden with environment variables using the `__` separator (e.g. `MemoryMcp__Ollama__Model`).

By default, data is stored in the platform-specific local application data directory:

| Platform | Default Data Directory |
|----------|----------------------|
| Windows | `%LOCALAPPDATA%\memory-mcp` (e.g. `C:\Users\<user>\AppData\Local\memory-mcp`) |
| Linux | `~/.local/share/memory-mcp` |
| macOS | `~/.local/share/memory-mcp` |

You can override this with the `DataDirectory` setting or the `MemoryMcp__DataDirectory` environment variable.

```json
{
  "MemoryMcp": {
    "MemoriesSubdirectory": "memories",
    "DatabaseFileName": "memory.db",
    "ChunkSizeWords": 512,
    "ChunkOverlapWords": 64,
    "SearchMaxContentLength": 1800,
    "Encryption": {
      "Enabled": false
    },
    "Ollama": {
      "Endpoint": "http://localhost:11434",
      "Model": "qwen3-embedding:0.6b",
      "Dimensions": 1024
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `DataDirectory` | `<LocalAppData>/memory-mcp` | Root directory for database and content files |
| `MemoriesSubdirectory` | `memories` | Subdirectory for `.memory.data` files |
| `DatabaseFileName` | `memory.db` | SQLite database filename |
| `ChunkSizeWords` | `512` | Words per chunk |
| `ChunkOverlapWords` | `64` | Overlapping words between consecutive chunks |
| `SearchMaxContentLength` | `1800` | Max characters per result in search output |
| `Encryption.Enabled` | `false` | Enable AES-256-GCM encryption for stored content (DPAPI key store on Windows, command-line key on Linux/macOS) |
| `Ollama.Endpoint` | `http://localhost:11434` | Ollama API URL |
| `Ollama.Model` | `qwen3-embedding:0.6b` | Embedding model name (must be pulled in Ollama) |
| `Ollama.Dimensions` | `1024` | Vector dimensions (must match model output) |

### Using a Different Embedding Model

Any Ollama model that supports embeddings will work. Update the model name and dimensions:

```sh
ollama pull nomic-embed-text
```

```json
{
  "MemoryMcp": {
    "Ollama": {
      "Model": "nomic-embed-text",
      "Dimensions": 768
    }
  }
}
```

Or via environment variables:

```sh
MemoryMcp__Ollama__Model=nomic-embed-text MemoryMcp__Ollama__Dimensions=768 dotnet run --project src/MemoryMcp
```

## Tests

```sh
# Unit tests (no external dependencies)
dotnet test tests/MemoryMcp.Core.Tests
dotnet test tests/MemoryMcp.Tests

# Integration tests (requires Ollama running with the model pulled)
dotnet test tests/MemoryMcp.Core.IntegrationTests
```

The integration tests skip automatically if Ollama is not available. They cover embedding quality (cosine similarity assertions), search relevance (precision@k, recall@k against a golden test set), and performance benchmarks.

| Test Project | Tests | What It Covers |
|---|---|---|
| `MemoryMcp.Core.Tests` | 49 | Chunking, MemoryService orchestration, SqliteVec store CRUD |
| `MemoryMcp.Tests` | 21 | MCP tool response formatting, tag parsing, content truncation |
| `MemoryMcp.Core.IntegrationTests` | 27 | Embedding quality, search relevance, latency (requires Ollama) |

## Project Structure

```
memory-mcp/
  Directory.Build.props              .NET 10, nullable, implicit usings
  MemoryMcp.slnx                     Solution file

  scripts/
    install.sh / install.ps1         One-step install (build, publish, configure)
    setup.sh / setup.ps1             Setup scripts (restore, build, pull model)
    run.sh / run.ps1                 Publish + start server (--rebuild to force)

  src/
    MemoryMcp.Core/                  Core library (no MCP dependency)
      Configuration/
        MemoryMcpOptions.cs          Strongly-typed options
      Security/
        IContentEncryptor.cs         Encryption interface
        AesGcmContentEncryptor.cs    AES-256-GCM implementation
        IKeyStore.cs                 Key storage interface
        DpapiKeyStore.cs             Windows DPAPI key store
        CommandLineKeyStore.cs       Linux/macOS key store
        KeyStoreFactory.cs           Platform-aware key store selection
      Models/
        ChunkRecord.cs               ChunkRecord, ChunkInfo, MemoryResult, SearchResult
      Services/
        IChunkingService.cs          Chunking interface
        WordChunkingService.cs       Word-based overlapping chunker
        IEmbeddingService.cs         Embedding interface
        OllamaEmbeddingService.cs    Ollama via OllamaSharp
        IMemoryService.cs            High-level orchestration interface
        MemoryService.cs             Ingest/get/update/delete/search orchestrator
        ServiceCollectionExtensions.cs  DI registration
      Storage/
        IMemoryStore.cs              Persistence interface
        SqliteVecMemoryStore.cs      SQLite + sqlite-vec + file storage

    MemoryMcp/                       MCP host (executable)
      Program.cs                     Entry point, DI, stdio transport
      Tools/
        MemoryTools.cs               5 MCP tool definitions
      appsettings.json               Default configuration

  tests/
    MemoryMcp.Core.Tests/            Unit tests for Core
    MemoryMcp.Core.IntegrationTests/ Integration tests (requires Ollama)
    MemoryMcp.Tests/                 Unit tests for MCP tools
```

## License

BSD 3-Clause. See [LICENSE](LICENSE).
