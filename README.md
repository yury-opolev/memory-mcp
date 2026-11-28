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
| `--shared true\|false` / `-Shared $true\|$false` | Switch [shared mode](#shared-mode-one-process-for-all-sessions) on or off; when omitted it is left as it is |

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

## Shared mode: one process for all sessions

By default every agent session starts its own `MemoryMcp` process, and each one opens the database, loads the store and checks the embedder. In **shared mode** one long-lived memory service owns the database instead, and each session's `MemoryMcp` process is a thin relay that forwards the session's MCP traffic to it. Startup work happens once, tool calls from all sessions go through one place (queued, and measured), and the service can be upgraded or stopped for maintenance without closing any session.

Client configurations do not change: clients keep starting `bin/MemoryMcp` with `MemoryMcp__DataDirectory`, and that process becomes a relay when `MemoryMcp:Shared:Enabled` is `true` in the data directory's `settings.json`.

**Switch it on:**

```sh
./scripts/install.sh --shared true          # Linux / macOS
```

```powershell
.\scripts\install.ps1 -Shared $true         # Windows
```

This publishes the service to `<install root>/service/<yyyyMMdd-HHmmss>/`, writes that path to `<data>/service-path.txt`, registers a launcher that starts `serve` for the data directory at logon and on demand, (re)starts the service on the new version, waits until `status` answers, and then sets `MemoryMcp.Shared.Enabled` in `settings.json` (other keys, such as the encryption setting, are kept). Sessions started from then on are relays; sessions that were already open keep running standalone until they are restarted.

| OS | Launcher registered by the install script | Without it |
|----|-------------------------------------------|------------|
| Windows | Scheduled task `MemoryMcp-<hash>`: at logon of the current user, only while logged on, no elevation, no console window (the service copy is built as a GUI-subsystem program) | Detached process |
| Linux | systemd user unit `~/.config/systemd/user/memory-mcp-<hash>.service` (`Restart=on-failure`, `WantedBy=default.target`), when `systemctl --user` works | Detached process in its own session (`setsid`); not started at login |
| macOS | LaunchAgent `~/Library/LaunchAgents/io.github.memory-mcp.<hash>.plist` (`RunAtLoad`) | Detached process in its own session |

`<hash>` is derived from the data directory's full path (`MemoryMcp status --print-hash` prints it), so each data directory has its own service, pipe and launcher. A relay that finds no service running starts it through the registered launcher.

**Upgrades.** While shared mode is on, every install (with or without `--shared`) updates the service: it publishes a new service directory, re-registers the launcher on it, stops the old service, and starts the new one. Open sessions stay open: their relays reconnect and replay the MCP handshake, and a call that was in flight during the restart gets an error to retry. `bin/` is what client configurations point at; it is refreshed only when no open session runs from it, otherwise the script prints `bin is in use by open sessions and was left as is; the service was updated`. Service directories other than the newest two are deleted.

**Switch it off:**

```sh
./scripts/install.sh --shared false
```

```powershell
.\scripts\install.ps1 -Shared $false
```

This sets `MemoryMcp.Shared.Enabled` to `false`, stops the service, removes the launcher and `service-path.txt`. It is the one change that needs agent sessions restarted: open sessions keep reporting that shared mode was switched off until they are.

**Commands.** Pass the data directory the install script uses (`<install root>/data`), since the program's own default is the install root itself:

```sh
MemoryMcp status --MemoryMcp:DataDirectory=<data>   # running or not, version, sessions, call and queue measurements; exit code 3 when not running
MemoryMcp stop   --MemoryMcp:DataDirectory=<data>   # stop gracefully and keep it down: relays and the logon launcher do not restart it
MemoryMcp start  --MemoryMcp:DataDirectory=<data>   # clear the stop and start it through the launcher
```

While the service is stopped on purpose, tool calls in open sessions fail with a message that says so, and the sessions reconnect on their own after `start` (or the next install, which clears the stop). This is how to do maintenance on the database without closing sessions. The service logs to `<data>/logs/`.

**Parallel calls.** `MemoryMcp:Shared:MaxParallelCalls` (default `1`) is how many tool calls run at once. With `1` calls are queued in arrival order on one store. With N > 1 the service opens N independent store connections; SQLite still allows one writer at a time, and a call that meets a busy database fails with a retryable error. Values above 1 help only once the embedder serves more than one request at a time; `status` shows the wait and run times to measure it.

**Settings.** In shared mode the service reads its settings from `appsettings.json`, the data directory's `settings.json` and its own environment. Of a client's `MemoryMcp__*` environment variables only `MemoryMcp__DataDirectory` and `MemoryMcp__Shared__*` have an effect; a relay names any other one on stderr as ignored. Put such settings in `settings.json`.

**Security model.** Relays and the service talk over a .NET named pipe created with the current-user-only restriction: on Windows a named pipe whose ACL admits only the user, on Linux and macOS a Unix domain socket with the same restriction. The operating system vouches for the caller, so there is no token or secret anywhere, and nothing listens on TCP. If the pipe cannot be created with that restriction, the service refuses to start. Other OS users and other machines are excluded; processes running as the same user are trusted, as they are without shared mode (they can read the data directory and the key store). The service runs as the logged-in user, never as a system account, and nothing needs admin rights.

**Platform status.** Windows and Linux (systemd user unit and the detached fallback) are verified. macOS is implemented (launchd agent, detached fallback) but not yet verified on a Mac.

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

Searches all stored memories, best first. The default is a **hybrid** search: a keyword (BM25) list for exact words, names and identifiers fused with the semantic (cosine similarity) list. Set `MemoryMcp:Search:DefaultMode` to `Dense` or `Keyword` to change it. The first line of the result names the search that ran; when the query cannot be embedded (Ollama unreachable) a hybrid search falls back to keyword search and says so. Each result shows its similarity (`n/a` for a keyword-only match) and which list found it.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | string | yes | Natural-language search text |
| `limit` | int | no | Max results (default: 5) |
| `minScore` | float | no | Minimum similarity threshold, 0.0--1.0. Applies to semantic matches only; keyword matches are not thresholded |
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
| Keyword index | SQLite `memories_fts` (contentless FTS5) + `memories_fts_map` | BM25 keyword and hybrid search; holds tokens, not the text |
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
2. Select the chunks that pass the tag filter (case-insensitive, any tag matches) and score **every** one of them with `vec_distance_cosine` — an exact scan, so a selective filter never loses matches.
3. Convert cosine distance to similarity: `score = 1 - distance`, and drop scores below `minScore`.
4. Group by memory ID, keeping the best chunk score per memory.
5. Sort by score descending, return top `limit` results with content loaded from files.

`IMemoryService.FindSimilarAsync` is the dense-only variant for threshold checks (duplicate guard, consolidation): it never takes tags and will never fuse keyword ranking, so its threshold always means cosine similarity.

### Keyword and hybrid search

`IHybridMemorySearch` (registered by `AddMemoryMcpCore`) searches in three modes (`MemorySearchMode`):

- **Dense** — the search pipeline above, unchanged (`MemoryMcp:Search:DefaultMode=Dense` selects it).
- **Keyword** — BM25 over a contentless FTS5 index (`memories_fts`): one row per memory with its folded title and content (`body`, `unicode61 remove_diacritics 2`) and their Snowball stems (`stem`, ru/da/en). Queries become safe `MATCH` expressions (`KeywordQuery.Build`): quoted phrases stay phrases, `word*` stays a prefix, other words are OR-ed.
- **Hybrid** (`MemoryMcp:Search:DefaultMode`, the default) — both lists fused with reciprocal rank fusion (`k = 60`; weighted RRF and convex combination configurable), giving the keyword list priority for quoted phrases and identifier-like tokens. If the query cannot be embedded it falls back to keyword; if the keyword index is unavailable it falls back to dense, and says so.

Tag filters and excluded ids (`MemorySearchFilter`) are applied before scoring in both lists. The index rows are written in the same transaction as the chunks and vectors; deleting or editing a memory removes its row and rewrites the index (`optimize`) under `secure_delete`, so no indexed token of deleted text survives. Existing memories are indexed on the first start (and again whenever the folding/stemming fingerprint changes). The index needs FTS5 `contentless_delete` (SQLite 3.43+). The standalone server links SQLite3 Multiple Ciphers, which has it (see [SQLite engine and encryption](#sqlite-engine-and-encryption)); a host that uses `MemoryMcp.Core` with only its SQLCipher bundle (SQLite 3.39) gets an index that reports itself unavailable, and dense search works as before. Settings live under `MemoryMcp:Search` (`DefaultMode`, `KeywordVariant`, `Fusion`, `RrfK`, `KeywordWeight`, `DenseWeight`, `ExactIntentKeywordBoost`, `Candidates`, `UseQueryInstruction`, `QueryInstruction`, `QueryTimeoutMs`).

The engine-agnostic pieces (`MemoryMcp.Core.Retrieval`: fusion, exact-intent detection, vector math, folding, tokenizer, Snowball stemming, keyword queries; `MemoryMcp.Core.Evaluation`: ranking metrics, paired statistics, ablation reports, pre-registration) are shared with Cortex's episodic history search.

### Deletion

Deletes are physical: sqlite-vec ≥ 0.1.9 zeroes the deleted vector slot, `PRAGMA secure_delete = ON` zeroes deleted rows, the keyword index is rewritten without the deleted entries, and the WAL (if enabled) is truncated after every delete. On first start the store rebuilds vector tables written by older sqlite-vec versions and runs `VACUUM` (`PRAGMA user_version = 1` marks it done).

### SQLite engine and encryption

The standalone server links [SQLite3 Multiple Ciphers](https://utelle.github.io/SQLite3MultipleCiphers/) (`SQLite3MC.PCLRaw.bundle`, SQLite 3.43 or later), which the keyword index needs. `MemoryMcp.Core` itself still references `SQLitePCLRaw.bundle_e_sqlcipher` (SQLCipher 4.5, SQLite 3.39); a host picks the engine by also referencing the SQLite3MC bundle, whose `SQLitePCLRaw.batteries_v2` assembly takes precedence. The server project does that, and so does the Cortex agent.

With `Encryption.Enabled`, content files are encrypted with AES-256-GCM and the database by the engine, with the same key as passphrase. Releases before the engine change wrote the database in the SQLCipher format, which SQLite3 Multiple Ciphers does not open by default. The server therefore converts such a database once, at startup, before the store opens it (`SqlCipherDatabaseConverter`):

1. It reads the old file in the engine's SQLCipher compatibility mode (`cipher=sqlcipher`, `legacy=4`) and writes a copy in the current format with `VACUUM INTO`, so no unencrypted copy ever reaches the disk and the original is only read.
2. It verifies the copy (integrity check, row count of every table) and then replaces the original with it.
3. The original stays beside it as `memory.db.sqlcipher-backup`, encrypted with the same key. Delete it once the converted database has proven itself.

A database that already opens with the current engine is left alone, so later starts do nothing. If the key does not open the file in either format, the database is in use by another process, or a `-journal`/`-wal` file lies beside a SQLCipher database, the server stops with an error and leaves the original in place and unchanged. Close every other session that uses the memory server before the first start of the new version. To go back to an earlier release, reinstall it and rename `memory.db.sqlcipher-backup` to `memory.db` (memories written after the conversion are then not in that file).

### sqlite-vec binaries

The NuGet `sqlite-vec` package stops at 0.1.7-alpha.2.1, so the official release binaries are vendored in `src/MemoryMcp.Core/runtimes/<rid>/native/` (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64) with their licenses and `SQLITE-VEC-VERSION.txt`. To update: `pwsh scripts/update-sqlite-vec.ps1 -Version <x.y.z>` (needs `gh`); it verifies every archive against the release `checksums.txt` and GitHub's asset digest. The store refuses to start with a version older than 0.1.9.

## Configuration

All settings live in `src/MemoryMcp/appsettings.json` and can be overridden with environment variables using the `__` separator (e.g. `MemoryMcp__Ollama__Model`).

By default, data is stored in the platform-specific local application data directory:

| Platform | Default Data Directory |
|----------|----------------------|
| Windows | `%LOCALAPPDATA%\memory-mcp` (e.g. `C:\Users\<user>\AppData\Local\memory-mcp`) |
| Linux | `~/.local/share/memory-mcp` |
| macOS | `~/.local/share/memory-mcp` |

You can override this with the `DataDirectory` setting or the `MemoryMcp__DataDirectory` environment variable. The install scripts use `<install root>/data` and put it in the client configuration they print.

Settings that must survive a rebuild go in the data directory's `settings.json`, which is read last and wins over `appsettings.json`, environment variables and the command line. The install scripts write `Encryption.Enabled` and `Shared.Enabled` there and keep every other key.

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
| `Shared.Enabled` | `false` | Run as a relay to the shared memory service (set by `--shared` / `-Shared`; see [Shared mode](#shared-mode-one-process-for-all-sessions)) |
| `Shared.MaxParallelCalls` | `1` | Tool calls the shared service runs at once; values below 1 count as 1 |

In shared mode the service ignores a client's `MemoryMcp__*` environment variables other than `MemoryMcp__DataDirectory` and `MemoryMcp__Shared__*`; put other settings in `settings.json`.

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
| `MemoryMcp.Core.Tests` | 100 | Chunking, MemoryService orchestration, SqliteVec store CRUD, byte-level erasure, exact filtered search |
| `MemoryMcp.Tests` | 50 | MCP tool response formatting, tag parsing, content truncation, the server's SQLite engine, SQLCipher database conversion |
| `MemoryMcp.Core.IntegrationTests` | 27 | Embedding quality, search relevance, latency (requires Ollama) |

## Project Structure

```
memory-mcp/
  Directory.Build.props              .NET 10, nullable, implicit usings
  MemoryMcp.slnx                     Solution file

  scripts/
    install.sh / install.ps1         One-step install (build, publish, configure, shared mode)
    shared/                          Launcher templates install.sh renders in shared mode
      memory-mcp.service.template    systemd user unit (Linux)
      io.github.memory-mcp.plist.template  launchd agent (macOS)
    setup.sh / setup.ps1             Setup scripts (restore, build, pull model)
    run.sh / run.ps1                 Publish + start server (--rebuild to force)
    update-sqlite-vec.ps1            Vendor + verify sqlite-vec release binaries

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
        SqliteVecExtension.cs        Loads vendored vec0, enforces >= 0.1.9
      runtimes/                      Vendored sqlite-vec binaries per RID

    MemoryMcp/                       MCP host (executable)
      Program.cs                     Entry point: picks standalone, relay, serve, start, stop or status
      Hosting/                       Configuration, role selection, standalone server, file log
      Shared/                        Shared mode
        PipeNames.cs, SharedPaths.cs Pipe and launcher names, files in the data directory
        Relay/                       The stdio relay to the service (reconnect, handshake replay)
        Service/                     serve, start, stop, status; session and control pipes, store pool
        Launch/                      Per-OS launchers (scheduled task, systemd, launchd, detached)
      Storage/
        SqlCipherDatabaseConverter.cs  One-time conversion of a SQLCipher database
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
