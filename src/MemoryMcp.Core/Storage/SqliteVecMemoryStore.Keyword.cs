using System.Text.Json;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Retrieval;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Core.Storage;

/// <summary>
/// The keyword (BM25) index of curated memories: one contentless FTS5 row per memory holding its
/// folded title and content (<c>body</c>) and their Snowball stems (<c>stem</c>). The text itself is
/// never stored in the index (the content stays in the memory file); rows are written in the same
/// transaction as the chunks and vectors. A delete or edit removes the row (<c>contentless_delete</c>)
/// and then rewrites the index with <c>optimize</c> under <c>PRAGMA secure_delete</c>, so a deleted or
/// edited memory leaves no indexed tokens behind. Needs SQLite 3.43+ (SQLite3 Multiple Ciphers in
/// Cortex); on older engines the index is reported unavailable and every other store operation works
/// unchanged.
/// </summary>
public partial class SqliteVecMemoryStore
{
    /// <summary>Changes whenever indexed rows would change; a mismatch rebuilds the index at startup.</summary>
    internal static readonly string KeywordIndexFingerprint =
        $"schema=1;fold={TextFolder.Version};stem={SnowballStemmer.Version};tokenize=unicode61-rd2";

    private const string KeywordFingerprintKey = "keyword_index";

    private string? keywordIndexUnavailableReason = "The memory store is not initialized.";

    /// <summary>Number of full keyword-index rebuilds run by this instance (observed by tests).</summary>
    internal int KeywordIndexRebuildCount { get; private set; }

    /// <summary>Number of keyword-index optimize passes run after deletes or replacements (observed by tests).</summary>
    internal int KeywordOptimizeCount { get; private set; }

    /// <inheritdoc />
    public string? KeywordIndexUnavailableReason => this.keywordIndexUnavailableReason;

    private bool KeywordIndexAvailable => this.keywordIndexUnavailableReason is null;

    /// <inheritdoc />
    public async Task<List<KeywordHit>> KeywordSearchAsync(string matchExpression, int limit, MemorySearchFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchExpression);
        ArgumentNullException.ThrowIfNull(filter);
        var hits = new List<KeywordHit>();
        if (!this.KeywordIndexAvailable || limit <= 0)
        {
            return hits;
        }

        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.MemoryId, bm25(memories_fts) AS rank
            FROM memories_fts
            JOIN memories_fts_map m ON m.FtsRowId = memories_fts.rowid
            JOIN chunks c ON c.MemoryId = m.MemoryId AND c.ChunkIndex = 0
            WHERE memories_fts MATCH @match
            AND (@tagFilter IS NULL OR {TagsMatchFunction}(c.Tags, @tagFilter))
            AND (@excluded IS NULL OR m.MemoryId NOT IN (SELECT value FROM json_each(@excluded)))
            ORDER BY rank, m.MemoryId
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@match", matchExpression);
        cmd.Parameters.AddWithValue("@tagFilter", filter.HasTags ? JsonSerializer.Serialize(filter.Tags) : DBNull.Value);
        cmd.Parameters.AddWithValue("@excluded", filter.HasExclusions ? JsonSerializer.Serialize(filter.ExcludeIds) : DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new KeywordHit(reader.GetString(0), -reader.GetDouble(1)));
        }

        return hits;
    }

    /// <inheritdoc />
    public async Task<int> RebuildKeywordIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!this.KeywordIndexAvailable)
        {
            return 0;
        }

        var memories = new List<(string MemoryId, string? Title)>();
        using (var listCmd = this.connection.CreateCommand())
        {
            listCmd.CommandText = "SELECT MemoryId, Title FROM chunks WHERE ChunkIndex = 0 ORDER BY MemoryId";
            using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                memories.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        var indexed = 0;
        var missing = 0;
        using (var transaction = this.connection.BeginTransaction())
        {
            await this.ExecuteAsync(transaction, "INSERT INTO memories_fts(memories_fts) VALUES('delete-all'); DELETE FROM memories_fts_map;", cancellationToken);
            foreach (var (memoryId, title) in memories)
            {
                var contentPath = this.GetContentPath(memoryId);
                if (!File.Exists(contentPath))
                {
                    missing++;
                    continue;
                }

                var content = await this.ReadContentFileAsync(contentPath, cancellationToken);
                await this.IndexKeywordDocumentAsync(transaction, memoryId, title, content, cancellationToken);
                indexed++;
            }

            await this.ExecuteAsync(
                transaction,
                "INSERT INTO memory_meta (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
                cancellationToken,
                ("@key", KeywordFingerprintKey),
                ("@value", KeywordIndexFingerprint));
            transaction.Commit();
        }

        await this.ExecuteAsync(null, "INSERT INTO memories_fts(memories_fts) VALUES('optimize'); PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        this.KeywordIndexRebuildCount++;
        this.logger.LogKeywordIndexRebuilt(indexed, missing);
        return indexed;
    }

    /// <summary>
    /// Probes FTS5 <c>contentless_delete</c> and <c>secure-delete</c> on a temporary table, creates the
    /// index tables and rebuilds them when the stored fingerprint differs (first start after the upgrade:
    /// backfill of every existing memory).
    /// </summary>
    private async Task InitializeKeywordIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.ExecuteAsync(null, """
                CREATE VIRTUAL TABLE temp.memories_fts_probe USING fts5(body, content='', contentless_delete=1);
                INSERT INTO temp.memories_fts_probe(memories_fts_probe, rank) VALUES('secure-delete', 1);
                DROP TABLE temp.memories_fts_probe;
                """, cancellationToken);
        }
        catch (SqliteException ex)
        {
            this.keywordIndexUnavailableReason = $"The SQLite engine cannot host the keyword index: {ex.Message}";
            this.logger.LogKeywordIndexUnavailable(ex.Message);
            return;
        }

        await this.ExecuteAsync(null, """
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                body, stem, content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2');
            INSERT INTO memories_fts(memories_fts, rank) VALUES('secure-delete', 1);
            CREATE TABLE IF NOT EXISTS memories_fts_map (
                FtsRowId INTEGER PRIMARY KEY,
                MemoryId TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS memory_meta (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """, cancellationToken);
        this.keywordIndexUnavailableReason = null;

        using var cmd = this.connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM memory_meta WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", KeywordFingerprintKey);
        var stored = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(stored, KeywordIndexFingerprint, StringComparison.Ordinal)
            || await this.KeywordIndexDriftedAsync(cancellationToken))
        {
            await this.RebuildKeywordIndexAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Cheap startup consistency check. Writes made by code that does not maintain the index (an older
    /// image after a rollback, a run on an engine without FTS5) leave map rows of deleted memories, memories
    /// without a row, and FTS rows or tokens nobody maps to. A memory whose content file is missing can never
    /// be indexed, so it does not count as drift.
    /// </summary>
    private async Task<bool> KeywordIndexDriftedAsync(CancellationToken cancellationToken)
    {
        async Task<long> Scalar(string sql)
        {
            using var cmd = this.connection.CreateCommand();
            cmd.CommandText = sql;
            return (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        }

        const string OrphanMapSql = """
            SELECT COUNT(*) FROM memories_fts_map m
            WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.MemoryId = m.MemoryId AND c.ChunkIndex = 0)
            """;
        if (await Scalar(OrphanMapSql) > 0)
        {
            return true;
        }

        if (await Scalar("SELECT COUNT(*) FROM memories_fts") != await Scalar("SELECT COUNT(*) FROM memories_fts_map"))
        {
            return true;
        }

        using var unmappedCmd = this.connection.CreateCommand();
        unmappedCmd.CommandText = """
            SELECT c.MemoryId FROM chunks c
            WHERE c.ChunkIndex = 0 AND NOT EXISTS (SELECT 1 FROM memories_fts_map m WHERE m.MemoryId = c.MemoryId)
            """;
        using var reader = await unmappedCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (File.Exists(this.GetContentPath(reader.GetString(0))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces the keyword row of <paramref name="memoryId"/> inside <paramref name="transaction"/>.
    /// Returns true when an older row was replaced (its entries need <see cref="PurgeDeletedKeywordEntriesAsync"/>).
    /// </summary>
    private async Task<bool> IndexKeywordDocumentAsync(SqliteTransaction transaction, string memoryId, string? title, string content, CancellationToken cancellationToken)
    {
        if (!this.KeywordIndexAvailable)
        {
            return false;
        }

        var replaced = await this.RemoveKeywordDocumentAsync(transaction, memoryId, cancellationToken);
        using var mapCmd = this.connection.CreateCommand();
        mapCmd.Transaction = transaction;
        mapCmd.CommandText = "INSERT INTO memories_fts_map (MemoryId) VALUES (@memoryId) RETURNING FtsRowId;";
        mapCmd.Parameters.AddWithValue("@memoryId", memoryId);
        var rowId = (long)(await mapCmd.ExecuteScalarAsync(cancellationToken))!;

        var body = TextFolder.Fold(string.IsNullOrWhiteSpace(title) ? content : title + "\n" + content);
        await this.ExecuteAsync(
            transaction,
            "INSERT INTO memories_fts (rowid, body, stem) VALUES (@rowId, @body, @stem);",
            cancellationToken,
            ("@rowId", rowId),
            ("@body", body),
            ("@stem", SnowballStemmer.StemBody(body)));
        return replaced;
    }

    /// <summary>
    /// Deletes the keyword row of <paramref name="memoryId"/> (if any) inside <paramref name="transaction"/>.
    /// Returns true when a row was deleted.
    /// </summary>
    private async Task<bool> RemoveKeywordDocumentAsync(SqliteTransaction transaction, string memoryId, CancellationToken cancellationToken)
    {
        if (!this.KeywordIndexAvailable)
        {
            return false;
        }

        var rows = await this.ExecuteAsync(
            transaction,
            """
            DELETE FROM memories_fts WHERE rowid IN (SELECT FtsRowId FROM memories_fts_map WHERE MemoryId = @memoryId);
            DELETE FROM memories_fts_map WHERE MemoryId = @memoryId;
            """,
            cancellationToken,
            ("@memoryId", memoryId));
        return rows > 0;
    }

    /// <summary>
    /// A contentless-delete FTS5 table first records deletions as tombstones; the deleted entries stay
    /// in the segment until it is rewritten. <c>optimize</c> rewrites the (small) index into one segment
    /// without them, and with <c>PRAGMA secure_delete</c> the freed pages are zeroed. Called after every
    /// delete or replacement, outside the write transaction.
    /// </summary>
    private async Task PurgeDeletedKeywordEntriesAsync(CancellationToken cancellationToken)
    {
        if (this.KeywordIndexAvailable)
        {
            this.KeywordOptimizeCount++;
            await this.ExecuteAsync(null, "INSERT INTO memories_fts(memories_fts) VALUES('optimize');", cancellationToken);
        }
    }

    private async Task<int> ExecuteAsync(SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        using var cmd = this.connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static partial class SqliteVecMemoryStoreKeywordLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt the memory keyword index: {Indexed} memories indexed, {Missing} without a content file.")]
    internal static partial void LogKeywordIndexRebuilt(this ILogger logger, int indexed, int missing);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The memory keyword index is unavailable; keyword and hybrid memory search fall back to semantic search: {Reason}")]
    internal static partial void LogKeywordIndexUnavailable(this ILogger logger, string reason);
}
