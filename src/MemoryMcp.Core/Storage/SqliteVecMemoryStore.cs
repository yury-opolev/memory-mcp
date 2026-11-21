using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// SQLite + sqlite-vec implementation of IMemoryStore.
/// Stores chunk metadata in a regular table, vectors in a sqlite-vec virtual table,
/// and full content in files on disk.
/// When encryption is enabled, the database is encrypted via SQLCipher and
/// content files are encrypted via AES-256-GCM.
/// </summary>
public partial class SqliteVecMemoryStore : IMemoryStore, IDisposable
{
    private readonly MemoryMcpOptions options;
    private readonly ILogger<SqliteVecMemoryStore> logger;
    private readonly IContentEncryptor contentEncryptor;
    private readonly SqliteConnection connection;
    private bool disposed;

    private const string TagsMatchFunction = "memory_tags_match";

    /// <summary>vec0's maximum <c>k</c> for a KNN query.</summary>
    internal const int VecMaxK = 4096;

    /// <summary>Largest KNN window used by search; lowered in tests to exercise the beyond-cap paths.</summary>
    internal int KnnMaxK { get; set; } = VecMaxK;

    private int slowPathLogged;

    public SqliteVecMemoryStore(
        IOptions<MemoryMcpOptions> options,
        IContentEncryptor contentEncryptor,
        ILogger<SqliteVecMemoryStore> logger,
        string? databaseKey = null)
    {
        this.options = options.Value;
        this.contentEncryptor = contentEncryptor;
        this.logger = logger;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = this.options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        if (databaseKey is not null)
        {
            builder.Password = databaseKey;
        }

        this.connection = new SqliteConnection(builder.ToString());
    }

    /// <summary>
    /// Constructor for testing with a pre-opened in-memory connection.
    /// </summary>
    internal SqliteVecMemoryStore(
        SqliteConnection connection,
        MemoryMcpOptions options,
        IContentEncryptor contentEncryptor,
        ILogger<SqliteVecMemoryStore> logger)
    {
        this.connection = connection;
        this.options = options;
        this.contentEncryptor = contentEncryptor;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Ensure directories exist
        Directory.CreateDirectory(this.options.DataDirectory);
        Directory.CreateDirectory(this.options.MemoriesDirectory);

        if (this.connection.State != System.Data.ConnectionState.Open)
        {
            await this.connection.OpenAsync(cancellationToken);
        }

        var vecVersion = SqliteVecExtension.Load(this.connection);

        // Overwrite deleted content with zeros so a deleted memory leaves no bytes in the file.
        // The pragma is per connection, so it is set on every open.
        using (var secureDeleteCmd = this.connection.CreateCommand())
        {
            secureDeleteCmd.CommandText = "PRAGMA secure_delete = ON;";
            await secureDeleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        this.connection.CreateFunction<string?, string, bool>(TagsMatchFunction, TagsMatch, isDeterministic: true);

        // Create chunks metadata table
        using var createChunksCmd = this.connection.CreateCommand();
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
            CREATE INDEX IF NOT EXISTS idx_chunks_updated_at ON chunks(UpdatedAt);
            """;
        await createChunksCmd.ExecuteNonQueryAsync(cancellationToken);

        // Create sqlite-vec virtual table for vectors
        using var createVecCmd = this.connection.CreateCommand();
        createVecCmd.CommandText = this.CreateVectorTableSql;
        await createVecCmd.ExecuteNonQueryAsync(cancellationToken);

        await this.RebuildLegacyVectorTableAsync(cancellationToken);
        await this.InitializeKeywordIndexAsync(cancellationToken);

        this.logger.LogInformation("Memory store initialized. Database: {DatabasePath}, Dimensions: {Dimensions}, sqlite-vec: {VecVersion}",
            this.options.DatabasePath, this.options.Ollama.Dimensions, vecVersion);
    }

    private string CreateVectorTableSql => $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_vec USING vec0(
            ChunkKey TEXT PRIMARY KEY,
            Vector   float[{this.options.Ollama.Dimensions}] distance_metric=cosine
        );
        """;

    /// <summary>
    /// Schema version 1 means the vector table was rebuilt by sqlite-vec &gt;= 0.1.9. Older releases
    /// only cleared a validity bit on DELETE, so the vectors of deleted memories may still sit in
    /// the old table. Copying the live vectors into a fresh table and vacuuming removes them.
    /// Vectors without a chunk row are unreachable by search and are dropped too.
    /// </summary>
    private async Task RebuildLegacyVectorTableAsync(CancellationToken cancellationToken)
    {
        const int VectorsRebuiltVersion = 1;

        using (var versionCmd = this.connection.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version;";
            var userVersion = (long)(await versionCmd.ExecuteScalarAsync(cancellationToken))!;
            if (userVersion >= VectorsRebuiltVersion)
            {
                return;
            }
        }

        using (var transaction = this.connection.BeginTransaction())
        {
            string[] steps =
            [
                """
                CREATE TEMP TABLE chunks_vec_rebuild AS
                SELECT v.ChunkKey, v.Vector
                FROM chunks c
                CROSS JOIN chunks_vec v
                WHERE v.ChunkKey = c.MemoryId || ':' || c.ChunkIndex;
                """,
                "DROP TABLE chunks_vec;",
                this.CreateVectorTableSql,
                "INSERT INTO chunks_vec (ChunkKey, Vector) SELECT ChunkKey, Vector FROM chunks_vec_rebuild;",
                "DROP TABLE chunks_vec_rebuild;",
                $"PRAGMA user_version = {VectorsRebuiltVersion};",
            ];

            foreach (var sql in steps)
            {
                using var cmd = this.connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }

        using (var vacuumCmd = this.connection.CreateCommand())
        {
            vacuumCmd.CommandText = "VACUUM;";
            await vacuumCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        this.logger.LogInformation("Rebuilt the vector table with sqlite-vec >= 0.1.9 and vacuumed {DatabasePath}.", this.options.DatabasePath);
    }

    public async Task StoreMemoryAsync(string memoryId, string content, List<ChunkRecord> chunks, List<float[]> vectors, CancellationToken cancellationToken = default)
    {
        if (chunks.Count != vectors.Count)
        {
            throw new ArgumentException("Chunks and vectors must have the same count.");
        }

        // Write content file (encrypted if enabled)
        var contentPath = this.GetContentPath(memoryId);
        await this.WriteContentFileAsync(contentPath, content, cancellationToken);

        using var transaction = this.connection.BeginTransaction();
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var vector = vectors[i];

                // Insert chunk metadata
                using var insertChunkCmd = this.connection.CreateCommand();
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
                using var insertVecCmd = this.connection.CreateCommand();
                insertVecCmd.Transaction = transaction;
                insertVecCmd.CommandText = """
                    INSERT INTO chunks_vec (ChunkKey, Vector)
                    VALUES (@chunkKey, @vector)
                    """;
                insertVecCmd.Parameters.AddWithValue("@chunkKey", FormatChunkKey(chunk.MemoryId, chunk.ChunkIndex));
                insertVecCmd.Parameters.AddWithValue("@vector", VectorToBlob(vector));
                await insertVecCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await this.IndexKeywordDocumentAsync(transaction, memoryId, chunks.Count > 0 ? chunks[0].Title : null, content, cancellationToken);

            transaction.Commit();
            this.logger.LogDebug("Stored memory {MemoryId} with {ChunkCount} chunks.", memoryId, chunks.Count);
        }
        catch
        {
            transaction.Rollback();
            // Clean up the content file if DB insert failed
            if (File.Exists(contentPath))
            {
                File.Delete(contentPath);
            }
            throw;
        }
    }

    public async Task<MemoryResult?> GetMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        // Get metadata from first chunk
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = """
            SELECT Title, Tags, CreatedAt, UpdatedAt
            FROM chunks
            WHERE MemoryId = @memoryId AND ChunkIndex = 0
            """;
        cmd.Parameters.AddWithValue("@memoryId", memoryId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = reader.IsDBNull(0) ? null : reader.GetString(0);
        var tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
        var createdAt = DateTimeOffset.Parse(reader.GetString(2));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(3));

        // Read content from file (decrypted if enabled)
        var contentPath = this.GetContentPath(memoryId);
        if (!File.Exists(contentPath))
        {
            this.logger.LogWarning("Content file missing for memory {MemoryId}.", memoryId);
            return null;
        }

        var content = await this.ReadContentFileAsync(contentPath, cancellationToken);

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
        using var cmd = this.connection.CreateCommand();
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

        // The title is part of the keyword document: re-index it in the same transaction.
        var contentPath = this.GetContentPath(memoryId);
        var content = title is not null && this.KeywordIndexAvailable && File.Exists(contentPath)
            ? await this.ReadContentFileAsync(contentPath, cancellationToken)
            : null;
        using var transaction = this.connection.BeginTransaction();
        cmd.Transaction = transaction;
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        var replaced = content is not null && updated > 0
            && await this.IndexKeywordDocumentAsync(transaction, memoryId, title, content, cancellationToken);
        transaction.Commit();
        if (replaced)
        {
            await this.PurgeDeletedKeywordEntriesAsync(cancellationToken);
        }
    }

    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        // Check if exists
        if (!await this.ExistsAsync(memoryId, cancellationToken))
        {
            return false;
        }

        using var transaction = this.connection.BeginTransaction();
        try
        {
            // Delete vectors
            using var deleteVecCmd = this.connection.CreateCommand();
            deleteVecCmd.Transaction = transaction;
            deleteVecCmd.CommandText = """
                DELETE FROM chunks_vec WHERE ChunkKey LIKE @pattern
                """;
            deleteVecCmd.Parameters.AddWithValue("@pattern", $"{memoryId}:%");
            await deleteVecCmd.ExecuteNonQueryAsync(cancellationToken);

            // Delete chunk metadata
            using var deleteChunksCmd = this.connection.CreateCommand();
            deleteChunksCmd.Transaction = transaction;
            deleteChunksCmd.CommandText = "DELETE FROM chunks WHERE MemoryId = @memoryId";
            deleteChunksCmd.Parameters.AddWithValue("@memoryId", memoryId);
            await deleteChunksCmd.ExecuteNonQueryAsync(cancellationToken);

            await this.RemoveKeywordDocumentAsync(transaction, memoryId, cancellationToken);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // Rewrite the keyword index without the deleted entries, then (in WAL mode) move the wiped pages
        // into the database file and truncate the WAL so no pre-delete copy survives there. The checkpoint
        // is a no-op in the default rollback-journal mode.
        await this.PurgeDeletedKeywordEntriesAsync(cancellationToken);
        using (var checkpointCmd = this.connection.CreateCommand())
        {
            checkpointCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpointCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete content file
        var contentPath = this.GetContentPath(memoryId);
        if (File.Exists(contentPath))
        {
            File.Delete(contentPath);
        }

        this.logger.LogDebug("Deleted memory {MemoryId}.", memoryId);
        return true;
    }

    public Task<List<SearchResult>> SearchAsync(float[] queryVector, int limit, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default) =>
        this.SearchDenseAsync(queryVector, limit, minScore, new MemorySearchFilter { Tags = tags }, cancellationToken);

    public async Task<List<SearchResult>> SearchDenseAsync(float[] queryVector, int limit, float? minScore, MemorySearchFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var chunkCount = await this.CountChunksAsync(cancellationToken);
        if (chunkCount == 0)
        {
            return [];
        }

        List<SearchResult> topResults;
        if ((filter.HasTags || filter.HasExclusions) && chunkCount > this.KnnMaxK)
        {
            if (Interlocked.Exchange(ref this.slowPathLogged, 1) == 0)
            {
                this.logger.LogExactJoinInUse(chunkCount, this.KnnMaxK);
            }

            topResults = await this.ExactJoinSearchAsync(queryVector, limit, minScore, filter, cancellationToken);
        }
        else
        {
            topResults = await this.KnnSearchAsync(queryVector, Math.Min(chunkCount, this.KnnMaxK), limit, minScore, filter, cancellationToken);
        }

        // Load content for each result (decrypted if enabled)
        foreach (var result in topResults)
        {
            var contentPath = this.GetContentPath(result.MemoryId);
            if (File.Exists(contentPath))
            {
                result.Content = await this.ReadContentFileAsync(contentPath, cancellationToken);
            }
        }

        return topResults;
    }

    private async Task<int> CountChunksAsync(CancellationToken cancellationToken)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// vec0 KNN is a flat scan, so with k = chunk count every chunk is ranked and the filters and
    /// per-memory grouping done afterwards are exact. With k capped below the chunk count (unfiltered
    /// only) the result is the true top-k chunks, so the top memories are exact as long as their best
    /// chunks fall inside that window.
    /// </summary>
    private async Task<List<SearchResult>> KnnSearchAsync(float[] queryVector, int k, int limit, float? minScore, MemorySearchFilter filter, CancellationToken cancellationToken)
    {
        // Rows arrive best-first, so the first row of a memory is its best chunk.
        var ranked = new List<(string MemoryId, float Distance)>();
        var seen = new HashSet<string>();

        using (var knnCmd = this.connection.CreateCommand())
        {
            knnCmd.CommandText = """
                SELECT v.ChunkKey, v.distance
                FROM chunks_vec v
                WHERE v.Vector MATCH @queryVector AND k = @k
                ORDER BY v.distance
                """;
            knnCmd.Parameters.AddWithValue("@queryVector", VectorToBlob(queryVector));
            knnCmd.Parameters.AddWithValue("@k", k);

            using var knnReader = await knnCmd.ExecuteReaderAsync(cancellationToken);
            while (await knnReader.ReadAsync(cancellationToken))
            {
                // A NULL distance means an unscorable (zero) vector.
                if (knnReader.IsDBNull(1))
                {
                    continue;
                }

                var chunkKey = knnReader.GetString(0);
                var memoryId = chunkKey[..chunkKey.LastIndexOf(':')];
                if (filter.HasExclusions && filter.ExcludeIds!.Contains(memoryId))
                {
                    continue;
                }

                if (seen.Add(memoryId))
                {
                    ranked.Add((memoryId, knnReader.GetFloat(1)));
                }
            }
        }

        var tagFilter = filter.HasTags ? JsonSerializer.Serialize(filter.Tags) : null;
        var results = new List<SearchResult>();

        using var metaCmd = this.connection.CreateCommand();
        metaCmd.CommandText = """
            SELECT Title, Tags, CreatedAt, UpdatedAt
            FROM chunks
            WHERE MemoryId = @memoryId AND ChunkIndex = 0
            """;
        var memoryIdParameter = metaCmd.Parameters.Add("@memoryId", SqliteType.Text);

        foreach (var (memoryId, distance) in ranked)
        {
            if (results.Count >= limit)
            {
                break;
            }

            // Cosine distance is in [0, 2]; similarity = 1 - distance is in [-1, 1].
            float score = 1.0f - distance;
            if (minScore.HasValue && score < minScore.Value)
            {
                break; // ranked is best-first
            }

            memoryIdParameter.Value = memoryId;
            using var metaReader = await metaCmd.ExecuteReaderAsync(cancellationToken);
            if (!await metaReader.ReadAsync(cancellationToken))
            {
                continue;
            }

            var tagsJson = metaReader.IsDBNull(1) ? null : metaReader.GetString(1);
            if (tagFilter is not null && !TagsMatch(tagsJson, tagFilter))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                MemoryId = memoryId,
                Title = metaReader.IsDBNull(0) ? null : metaReader.GetString(0),
                Content = string.Empty, // Will be filled by the caller
                Tags = JsonSerializer.Deserialize<List<string>>(tagsJson ?? "[]") ?? [],
                CreatedAt = DateTimeOffset.Parse(metaReader.GetString(2)),
                UpdatedAt = DateTimeOffset.Parse(metaReader.GetString(3)),
                Score = score,
            });
        }

        return results;
    }

    /// <summary>
    /// Exact filtered search for corpora beyond the vec0 KNN cap: scores only the chunks that pass the
    /// tag filter and are not excluded. Correct but ~16-20 µs per chunk, because every chunk costs a
    /// vec0 point lookup.
    /// </summary>
    private async Task<List<SearchResult>> ExactJoinSearchAsync(float[] queryVector, int limit, float? minScore, MemorySearchFilter filter, CancellationToken cancellationToken)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.MemoryId, vec_distance_cosine(v.Vector, @queryVector) AS distance,
                   c.Title, c.Tags, c.CreatedAt, c.UpdatedAt
            FROM chunks c
            CROSS JOIN chunks_vec v
            WHERE v.ChunkKey = c.MemoryId || ':' || c.ChunkIndex
            AND (@tagFilter IS NULL OR {TagsMatchFunction}(c.Tags, @tagFilter))
            AND (@excluded IS NULL OR c.MemoryId NOT IN (SELECT value FROM json_each(@excluded)))
            ORDER BY distance
            """;
        cmd.Parameters.AddWithValue("@queryVector", VectorToBlob(queryVector));
        cmd.Parameters.AddWithValue("@tagFilter", filter.HasTags ? JsonSerializer.Serialize(filter.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("@excluded", filter.HasExclusions ? JsonSerializer.Serialize(filter.ExcludeIds) : DBNull.Value);

        var resultsByMemory = new Dictionary<string, SearchResult>();

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memoryId = reader.GetString(0);

            // Rows arrive best-first, so the first row of a memory is its best chunk.
            // A NULL distance means an unscorable (zero) vector.
            if (resultsByMemory.ContainsKey(memoryId) || reader.IsDBNull(1))
            {
                continue;
            }

            float score = 1.0f - reader.GetFloat(1);
            if (minScore.HasValue && score < minScore.Value)
            {
                continue;
            }

            resultsByMemory[memoryId] = new SearchResult
            {
                MemoryId = memoryId,
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                Content = string.Empty, // Will be filled by the caller
                Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(5)),
                Score = score,
            };
        }

        return resultsByMemory.Values
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> ExistsAsync(string memoryId, CancellationToken cancellationToken = default)
    {
        using var cmd = this.connection.CreateCommand();
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

    /// <summary>
    /// SQL function <c>memory_tags_match(chunkTagsJson, filterJson)</c>: true when the chunk shares at
    /// least one tag with the filter. Case-insensitive like <see cref="StringComparer.OrdinalIgnoreCase"/>,
    /// which SQLite's ASCII-only NOCASE cannot provide for non-Latin tags.
    /// </summary>
    private static bool TagsMatch(string? chunkTagsJson, string filterJson)
    {
        if (string.IsNullOrEmpty(chunkTagsJson))
        {
            return false;
        }

        var chunkTags = JsonSerializer.Deserialize<List<string>>(chunkTagsJson) ?? [];
        var filter = JsonSerializer.Deserialize<List<string>>(filterJson) ?? [];
        return filter.Any(t => chunkTags.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    private string GetContentPath(string memoryId)
        => Path.Combine(this.options.MemoriesDirectory, $"{memoryId}.memory.data");

    private async Task WriteContentFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var plainBytes = Encoding.UTF8.GetBytes(content);
        var outputBytes = this.contentEncryptor.Encrypt(plainBytes);
        await File.WriteAllBytesAsync(path, outputBytes, cancellationToken);
    }

    private async Task<string> ReadContentFileAsync(string path, CancellationToken cancellationToken)
    {
        var fileBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var plainBytes = this.contentEncryptor.Decrypt(fileBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.connection.Dispose();
            this.disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

internal static partial class SqliteVecMemoryStoreLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Filtered search is using the slower exact join path: {ChunkCount} chunks exceed the vec0 KNN window of {MaxK}.")]
    internal static partial void LogExactJoinInUse(
        this ILogger logger, int chunkCount, int maxK);
}
