using System.Text.Json;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// SQLite + sqlite-vec implementation of IMemoryStore.
/// Stores chunk metadata in a regular table, vectors in a sqlite-vec virtual table,
/// and full content in files on disk.
/// </summary>
public class SqliteVecMemoryStore : IMemoryStore, IDisposable
{
    private readonly MemoryMcpOptions _options;
    private readonly ILogger<SqliteVecMemoryStore> _logger;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SqliteVecMemoryStore(IOptions<MemoryMcpOptions> options, ILogger<SqliteVecMemoryStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
    }

    /// <summary>
    /// Constructor for testing with a pre-opened in-memory connection.
    /// </summary>
    internal SqliteVecMemoryStore(SqliteConnection connection, MemoryMcpOptions options, ILogger<SqliteVecMemoryStore> logger)
    {
        _connection = connection;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Ensure directories exist
        Directory.CreateDirectory(_options.DataDirectory);
        Directory.CreateDirectory(_options.MemoriesDirectory);

        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken);

        // Load sqlite-vec extension
        _connection.LoadExtension("vec0");

        // Create chunks metadata table
        using var createChunksCmd = _connection.CreateCommand();
        createChunksCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS chunks (
                MemoryId    TEXT NOT NULL,
                ChunkIndex  INTEGER NOT NULL,
                StartOffset INTEGER NOT NULL,
                Length      INTEGER NOT NULL,
                Title       TEXT,
                Tags        TEXT DEFAULT '[]',
                CreatedAt   TEXT NOT NULL,
                UpdatedAt   TEXT NOT NULL,
                PRIMARY KEY (MemoryId, ChunkIndex)
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_memory_id ON chunks(MemoryId);
            CREATE INDEX IF NOT EXISTS idx_chunks_tags ON chunks(Tags);
            """;
        await createChunksCmd.ExecuteNonQueryAsync(cancellationToken);

        // Create sqlite-vec virtual table for vectors
        using var createVecCmd = _connection.CreateCommand();
        createVecCmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_vec USING vec0(
                ChunkKey TEXT PRIMARY KEY,
                Vector   float[{_options.Ollama.Dimensions}]
            );
            """;
        await createVecCmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Memory store initialized. Database: {DatabasePath}, Dimensions: {Dimensions}",
            _options.DatabasePath, _options.Ollama.Dimensions);
    }

    public async Task StoreMemoryAsync(string memoryId, string content, List<ChunkRecord> chunks, List<float[]> vectors, CancellationToken cancellationToken = default)
    {
        if (chunks.Count != vectors.Count)
            throw new ArgumentException("Chunks and vectors must have the same count.");

        // Write content file
        var contentPath = GetContentPath(memoryId);
        await File.WriteAllTextAsync(contentPath, content, cancellationToken);

        using var transaction = _connection.BeginTransaction();
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var vector = vectors[i];

                // Insert chunk metadata
                using var insertChunkCmd = _connection.CreateCommand();
                insertChunkCmd.Transaction = transaction;
                insertChunkCmd.CommandText = """
                    INSERT INTO chunks (MemoryId, ChunkIndex, StartOffset, Length, Title, Tags, CreatedAt, UpdatedAt)
                    VALUES (@memoryId, @chunkIndex, @startOffset, @length, @title, @tags, @createdAt, @updatedAt)
                    """;
                insertChunkCmd.Parameters.AddWithValue("@memoryId", chunk.MemoryId);
                insertChunkCmd.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);
                insertChunkCmd.Parameters.AddWithValue("@startOffset", chunk.StartOffset);
                insertChunkCmd.Parameters.AddWithValue("@length", chunk.Length);
                insertChunkCmd.Parameters.AddWithValue("@title", (object?)chunk.Title ?? DBNull.Value);
                insertChunkCmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(chunk.Tags));
                insertChunkCmd.Parameters.AddWithValue("@createdAt", chunk.CreatedAt.ToString("o"));
                insertChunkCmd.Parameters.AddWithValue("@updatedAt", chunk.UpdatedAt.ToString("o"));
                await insertChunkCmd.ExecuteNonQueryAsync(cancellationToken);

                // Insert vector
                using var insertVecCmd = _connection.CreateCommand();
                insertVecCmd.Transaction = transaction;
                insertVecCmd.CommandText = """
                    INSERT INTO chunks_vec (ChunkKey, Vector)
                    VALUES (@chunkKey, @vector)
                    """;
                insertVecCmd.Parameters.AddWithValue("@chunkKey", FormatChunkKey(chunk.MemoryId, chunk.ChunkIndex));
                insertVecCmd.Parameters.AddWithValue("@vector", VectorToBlob(vector));
                await insertVecCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
            _logger.LogDebug("Stored memory {MemoryId} with {ChunkCount} chunks.", memoryId, chunks.Count);
        }
        catch
        {
            transaction.Rollback();
            // Clean up the content file if DB insert failed
            if (File.Exists(contentPath))
                File.Delete(contentPath);
            throw;
        }
    }

    public async Task<MemoryResult?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        // Get metadata from first chunk
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT Title, Tags, CreatedAt, UpdatedAt
            FROM chunks
            WHERE MemoryId = @memoryId AND ChunkIndex = 0
            """;
        cmd.Parameters.AddWithValue("@memoryId", memoryId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var title = reader.IsDBNull(0) ? null : reader.GetString(0);
        var tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        var createdAt = DateTimeOffset.Parse(reader.GetString(2));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

        // Read content from file
        var contentPath = GetContentPath(memoryId);
        if (!File.Exists(contentPath))
        {
            _logger.LogWarning("Content file missing for memory {MemoryId}.", memoryId);
            return null;
        }

        var content = await File.ReadAllTextAsync(contentPath, cancellationToken);

        return new MemoryResult
        {
            MemoryId = memoryId,
            Title = title,
            Content = content,
            Tags = tags,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
    }

    public async Task UpdateMetadataAsync(string memoryId, string? title, List<string>? tags, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        var setClauses = new List<string> { "UpdatedAt = @updatedAt" };
        using var cmd = _connection.CreateCommand();
        cmd.Parameters.AddWithValue("@memoryId", memoryId);
        cmd.Parameters.AddWithValue("@updatedAt", updatedAt.ToString("o"));

        if (title is not null)
        {
            setClauses.Add("Title = @title");
            cmd.Parameters.AddWithValue("@title", title);
        }

        if (tags is not null)
        {
            setClauses.Add("Tags = @tags");
            cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(tags));
        }

        cmd.CommandText = $"UPDATE chunks SET {string.Join(", ", setClauses)} WHERE MemoryId = @memoryId";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        // Check if exists
        if (!await ExistsAsync(memoryId, cancellationToken))
            return false;

        using var transaction = _connection.BeginTransaction();
        try
        {
            // Delete vectors
            using var deleteVecCmd = _connection.CreateCommand();
            deleteVecCmd.Transaction = transaction;
            deleteVecCmd.CommandText = """
                DELETE FROM chunks_vec WHERE ChunkKey LIKE @pattern
                """;
            deleteVecCmd.Parameters.AddWithValue("@pattern", $"{memoryId}:%");
            await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken);

            // Delete chunk metadata
            using var deleteChunksCmd = _connection.CreateCommand();
            deleteChunksCmd.Transaction = transaction;
            deleteChunksCmd.CommandText = "DELETE FROM chunks WHERE MemoryId = @memoryId";
            deleteChunksCmd.Parameters.AddWithValue("@memoryId", memoryId);
            await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // Delete content file
        var contentPath = GetContentPath(memoryId);
        if (File.Exists(contentPath))
            File.Delete(contentPath);

        _logger.LogDebug("Deleted memory {MemoryId}.", memoryId);
        return true;
    }

    public async Task<List<SearchResult>> SearchAsync(float[] queryVector, int limit, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default)
    {
        // Query sqlite-vec for nearest neighbors
        // Fetch more than limit to account for grouping by MemoryId and filtering
        int fetchLimit = limit * 10;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.ChunkKey, v.distance, c.Title, c.Tags, c.CreatedAt, c.UpdatedAt
            FROM chunks_vec v
            INNER JOIN chunks c ON c.MemoryId || ':' || c.ChunkIndex = v.ChunkKey
            WHERE v.Vector MATCH @queryVector
            AND k = @fetchLimit
            ORDER BY v.distance
            """;
        cmd.Parameters.AddWithValue("@queryVector", VectorToBlob(queryVector));
        cmd.Parameters.AddWithValue("@fetchLimit", fetchLimit);

        var resultsByMemory = new Dictionary<string, SearchResult>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunkKey = reader.GetString(0);
            var distance = reader.GetFloat(1);
            var title = reader.IsDBNull(2) ? null : reader.GetString(2);
            var chunkTags = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
            var createdAt = DateTimeOffset.Parse(reader.GetString(4));
            var updatedAt = DateTimeOffset.Parse(reader.GetString(5));

            var (memoryId, _) = ParseChunkKey(chunkKey);

            // sqlite-vec returns cosine distance (0 = identical, 2 = opposite)
            // Convert to similarity score: similarity = 1 - distance
            float score = 1.0f - distance;

            // Apply minimum score filter
            if (minScore.HasValue && score < minScore.Value)
                continue;

            // Apply tag filter: memory must have at least one of the requested tags
            if (tags is { Count: > 0 } && !tags.Any(t => chunkTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                continue;

            // Group by memory: keep the best score per memory
            if (!resultsByMemory.TryGetValue(memoryId, out var existing) || score > existing.Score)
            {
                resultsByMemory[memoryId] = new SearchResult
                {
                    MemoryId = memoryId,
                    Title = title,
                    Content = string.Empty, // Will be filled below
                    Tags = chunkTags,
                    CreatedAt = createdAt,
                    UpdatedAt = updatedAt,
                    Score = score,
                };
            }
        }

        // Sort by score descending, take top limit
        var topResults = resultsByMemory.Values
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();

        // Load content for each result
        foreach (var result in topResults)
        {
            var contentPath = GetContentPath(result.MemoryId);
            if (File.Exists(contentPath))
            {
                result.Content = await File.ReadAllTextAsync(contentPath, cancellationToken);
            }
        }

        return topResults;
    }

    public async Task<bool> ExistsAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM chunks WHERE MemoryId = @memoryId LIMIT 1";
        cmd.Parameters.AddWithValue("@memoryId", memoryId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <summary>
    /// Converts a float array to a byte blob for sqlite-vec storage.
    /// sqlite-vec expects vectors as little-endian float32 blobs.
    /// </summary>
    private static byte[] VectorToBlob(float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        return blob;
    }

    private static string FormatChunkKey(string memoryId, int chunkIndex)
        => $"{memoryId}:{chunkIndex}";

    private static (string MemoryId, int ChunkIndex) ParseChunkKey(string chunkKey)
    {
        var lastColon = chunkKey.LastIndexOf(':');
        return (chunkKey[..lastColon], int.Parse(chunkKey[(lastColon + 1)..]));
    }

    private string GetContentPath(string memoryId)
        => Path.Combine(_options.MemoriesDirectory, $"{memoryId}.memory.data");

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
