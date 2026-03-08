namespace MemoryMcp.Core.Configuration;

/// <summary>
/// Root configuration for the Memory MCP service.
/// Bound from the "MemoryMcp" section of appsettings.json.
/// All values are overridable via environment variables (e.g., MemoryMcp__Ollama__Model).
/// </summary>
public class MemoryMcpOptions
{
    public const string SectionName = "MemoryMcp";

    /// <summary>
    /// Root directory for all data (SQLite database and memory content files).
    /// Defaults to the platform-specific local application data directory:
    ///   Windows: %LOCALAPPDATA%\memory-mcp
    ///   Linux:   ~/.local/share/memory-mcp
    ///   macOS:   ~/.local/share/memory-mcp
    /// </summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory;

    private static string DefaultDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "memory-mcp");

    /// <summary>
    /// Subdirectory under DataDirectory for memory content files.
    /// </summary>
    public string MemoriesSubdirectory { get; set; } = "memories";

    /// <summary>
    /// SQLite database file name (created inside DataDirectory).
    /// </summary>
    public string DatabaseFileName { get; set; } = "memory.db";

    /// <summary>
    /// Number of words per chunk when splitting content for embedding.
    /// </summary>
    public int ChunkSizeWords { get; set; } = 512;

    /// <summary>
    /// Number of overlapping words between consecutive chunks.
    /// </summary>
    public int ChunkOverlapWords { get; set; } = 64;

    /// <summary>
    /// Maximum number of characters to return per memory in search results.
    /// Content exceeding this length is truncated with a message to use get_memory.
    /// </summary>
    public int SearchMaxContentLength { get; set; } = 1800;

    /// <summary>
    /// Minimum cosine similarity score (0.0-1.0) at which a new memory is
    /// considered a duplicate of an existing one. Set to 0 to disable the
    /// duplicate guard entirely. Default: 0.90.
    /// </summary>
    public float DuplicateThreshold { get; set; } = 0.90f;

    /// <summary>
    /// Maximum number of similar memories to return when the duplicate guard
    /// rejects an ingest. Default: 5.
    /// </summary>
    public int DuplicateSearchLimit { get; set; } = 5;

    /// <summary>
    /// Ollama embedding provider configuration.
    /// </summary>
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>
    /// Full path to the SQLite database file.
    /// </summary>
    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>
    /// Full path to the memories content directory.
    /// </summary>
    public string MemoriesDirectory => Path.Combine(DataDirectory, MemoriesSubdirectory);
}

/// <summary>
/// Configuration for the Ollama embedding provider.
/// </summary>
public class OllamaOptions
{
    /// <summary>
    /// Ollama server endpoint URL.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Embedding model name. Must be pulled in Ollama before use.
    /// </summary>
    public string Model { get; set; } = "qwen3-embedding:0.6b";

    /// <summary>
    /// Number of dimensions for the embedding vectors.
    /// Must match the model's output dimensions (or a supported MRL dimension).
    /// </summary>
    public int Dimensions { get; set; } = 1024;
}
