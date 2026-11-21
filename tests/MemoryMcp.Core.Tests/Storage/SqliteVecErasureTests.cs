using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Storage;

/// <summary>
/// Gate tests for physical erasure: after a delete, no byte of the deleted memory may remain
/// in the database file (or its journal/WAL). Each test plants a unique marker and scans the raw file.
/// </summary>
public sealed class SqliteVecErasureTests : IDisposable
{
    private const int Dimensions = 8;

    // Distinctive float32 values: their 32-byte little-endian pattern does not occur by chance.
    private static readonly float[] MarkerVector =
        [1234.5678f, -8765.4321f, 3.14159f, 2.71828f, 1.41421f, -0.57721f, 6.62607f, 299.792f];

    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly SqliteVecMemoryStore store;

    public SqliteVecErasureTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_erasure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);
        this.options = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "erasure.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options), new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
    }

    public void Dispose()
    {
        this.store.Dispose();
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a pooled handle may still be closing
        }
    }

    private static List<ChunkRecord> SingleChunk(string memoryId, string title) =>
    [
        new()
        {
            MemoryId = memoryId, ChunkIndex = 0, StartOffset = 0, Length = 10,
            Title = title, Tags = ["erasure"],
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        },
    ];

    private static float[] Filler(int seed)
    {
        var rng = new Random(seed);
        var vector = new float[Dimensions];
        for (int i = 0; i < Dimensions; i++)
        {
            vector[i] = (float)rng.NextDouble();
        }

        return vector;
    }

    [Fact]
    public async Task DeleteMemoryAsync_VectorBytesAreErasedFromDatabaseFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);

        // Neighbours share the victim's vector chunk, so only an in-place wipe can remove it.
        for (int i = 0; i < 3; i++)
        {
            await this.store.StoreMemoryAsync($"keep-{i}", "keep", SingleChunk($"keep-{i}", "keep"), [Filler(i)], ct);
        }

        await this.store.StoreMemoryAsync("victim", "victim", SingleChunk("victim", "victim"), [MarkerVector], ct);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, marker), "sanity: marker must be visible before the delete");

        Assert.True(await this.store.DeleteMemoryAsync("victim", ct));

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "deleted vector bytes are still in the database file");
    }

    [Fact]
    public async Task DeleteMemoryAsync_MetadataBytesAreErasedFromDatabaseFile()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        await this.store.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);

        var victimId = $"victim-{Guid.NewGuid():N}";
        var title = $"TITLE-MARKER-{Guid.NewGuid():N}";
        await this.store.StoreMemoryAsync(victimId, "victim", SingleChunk(victimId, title), [Filler(2)], ct);
        var titleBytes = System.Text.Encoding.UTF8.GetBytes(title);
        var idBytes = System.Text.Encoding.UTF8.GetBytes(victimId);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, titleBytes), "sanity: title must be visible before the delete");

        Assert.True(await this.store.DeleteMemoryAsync(victimId, ct));

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, titleBytes), "deleted title bytes are still in the database file");
        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, idBytes), "deleted memory id bytes are still in the database file");
    }

    [Fact]
    public async Task DeleteMemoryAsync_WalMode_LeavesNoCopyInWal()
    {
        var ct = TestContext.Current.CancellationToken;
        var walOptions = new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            DatabaseFileName = "erasure-wal.db",
            Ollama = new OllamaOptions { Dimensions = Dimensions },
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={walOptions.DatabasePath}");
        connection.Open();
        using (var walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            walCmd.ExecuteNonQuery();
        }

        using var walStore = new SqliteVecMemoryStore(
            connection, walOptions, new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());
        await walStore.InitializeAsync(ct);
        await walStore.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);
        await walStore.StoreMemoryAsync("victim", "victim", SingleChunk("victim", "victim"), [MarkerVector], ct);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(walOptions.DatabasePath, marker), "sanity: marker must be visible before the delete");

        Assert.True(await walStore.DeleteMemoryAsync("victim", ct));

        Assert.False(DatabaseBytes.Contains(walOptions.DatabasePath, marker), "deleted vector bytes survive in the database or its WAL");
    }

    [Fact]
    public async Task InitializeAsync_LegacyDatabase_RebuildsVectorTableWithoutUnreferencedVectors()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new float[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        CreateLegacyDatabase(this.options.DatabasePath, live);
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, marker), "sanity: legacy residue must be visible before the rebuild");

        await this.store.InitializeAsync(ct);

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "legacy vector residue survived the rebuild");
        Assert.Equal(1L, ReadUserVersion(this.options.DatabasePath));
        var results = await this.store.SearchAsync(live, limit: 5, cancellationToken: ct);
        Assert.Equal("live", Assert.Single(results).MemoryId);
    }

    [Fact]
    public async Task InitializeAsync_OldWriterDeletedSinceTheLastOpen_RebuildsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        await this.store.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);
        this.store.Dispose();

        // A rolled-back image running sqlite-vec 0.1.7 deletes without wiping; its delete stamps the writer version.
        PlantVectorResidue(this.options.DatabasePath);
        SetLastDeleteWriter(this.options.DatabasePath, "v0.1.7-alpha.2.1");
        var marker = DatabaseBytes.Of(MarkerVector);
        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, marker), "sanity: residue must be visible before the reopen");

        using var reopened = this.NewStore();
        await reopened.InitializeAsync(ct);

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "residue of the old writer survived the reopen");
        Assert.Equal("keep", Assert.Single(await reopened.SearchAsync(Filler(1), limit: 5, cancellationToken: ct)).MemoryId);
        Assert.Equal(CurrentVecVersion(this.options.DatabasePath), ReadLastDeleteWriter(this.options.DatabasePath));
    }

    [Fact]
    public async Task InitializeAsync_DatabaseWithoutWriterMeta_RebuildsOnceAndRecordsTheWriter()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        this.store.Dispose();

        // Rebuilt by a release that did not record the writer version: an old image may have written since.
        DropWriterMeta(this.options.DatabasePath);
        PlantVectorResidue(this.options.DatabasePath);
        var marker = DatabaseBytes.Of(MarkerVector);

        using var reopened = this.NewStore();
        await reopened.InitializeAsync(ct);

        Assert.False(DatabaseBytes.Contains(this.options.DatabasePath, marker), "residue survived although the writer was unknown");
        Assert.Equal(CurrentVecVersion(this.options.DatabasePath), ReadLastDeleteWriter(this.options.DatabasePath));
    }

    [Fact]
    public async Task InitializeAsync_OnlyCurrentWritersDeleted_DoesNotRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        await this.store.InitializeAsync(ct);
        await this.store.StoreMemoryAsync("keep", "keep", SingleChunk("keep", "keep"), [Filler(1)], ct);
        await this.store.StoreMemoryAsync("gone", "gone", SingleChunk("gone", "gone"), [Filler(2)], ct);
        Assert.True(await this.store.DeleteMemoryAsync("gone", ct));
        this.store.Dispose();
        PlantVectorResidue(this.options.DatabasePath);

        using var reopened = this.NewStore();
        await reopened.InitializeAsync(ct);

        Assert.True(DatabaseBytes.Contains(this.options.DatabasePath, DatabaseBytes.Of(MarkerVector)), "a needless rebuild ran on a database only current writers touched");
    }

    private SqliteVecMemoryStore NewStore() =>
        new(Options.Create(this.options), new NullContentEncryptor(), Substitute.For<ILogger<SqliteVecMemoryStore>>());

    private static Microsoft.Data.Sqlite.SqliteConnection OpenRaw(string databasePath)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        SqliteVecExtension.Load(connection);
        return connection;
    }

    private static void Execute(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenRaw(databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
    }

    private static void PlantVectorResidue(string databasePath) =>
        Execute(databasePath, "INSERT INTO chunks_vec (ChunkKey, Vector) VALUES ('ghost:0', @v);", ("@v", DatabaseBytes.Of(MarkerVector)));

    private static void SetLastDeleteWriter(string databasePath, string version) =>
        Execute(databasePath, "UPDATE vec_writer_meta SET LastDeleteVecVersion = @v;", ("@v", version));

    private static void DropWriterMeta(string databasePath) =>
        Execute(databasePath, "DROP TRIGGER chunks_record_delete_writer; DROP TABLE vec_writer_meta;");

    private static string? ReadLastDeleteWriter(string databasePath)
    {
        using var connection = OpenRaw(databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT LastDeleteVecVersion FROM vec_writer_meta;";
        return cmd.ExecuteScalar() as string;
    }

    private static string CurrentVecVersion(string databasePath)
    {
        using var connection = OpenRaw(databasePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vec_version();";
        return (string)cmd.ExecuteScalar()!;
    }
    /// <summary>
    /// Builds a schema-version-0 database: one live memory plus one vector with no chunk row.
    /// The unreferenced vector stands in for residue left behind by sqlite-vec &lt; 0.1.9 deletes.
    /// </summary>
    private static void CreateLegacyDatabase(string databasePath, float[] liveVector)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        SqliteVecExtension.Load(connection);

        void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            cmd.ExecuteNonQuery();
        }

        Execute("""
            CREATE TABLE chunks (
                MemoryId TEXT NOT NULL, ChunkIndex INTEGER NOT NULL, StartOffset INTEGER NOT NULL, Length INTEGER NOT NULL,
                Title TEXT, Tags TEXT DEFAULT '[]', CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (MemoryId, ChunkIndex));
            """);
        Execute($"CREATE VIRTUAL TABLE chunks_vec USING vec0(ChunkKey TEXT PRIMARY KEY, Vector float[{Dimensions}] distance_metric=cosine);");
        var now = DateTimeOffset.UtcNow.ToString("o");
        Execute(
            "INSERT INTO chunks VALUES ('live', 0, 0, 4, 'live', '[]', @now, @now);",
            ("@now", now));
        Execute("INSERT INTO chunks_vec (ChunkKey, Vector) VALUES ('live:0', @v);", ("@v", DatabaseBytes.Of(liveVector)));
        Execute("INSERT INTO chunks_vec (ChunkKey, Vector) VALUES ('ghost:0', @v);", ("@v", DatabaseBytes.Of(MarkerVector)));
    }

    private static long ReadUserVersion(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)cmd.ExecuteScalar()!;
    }
}
