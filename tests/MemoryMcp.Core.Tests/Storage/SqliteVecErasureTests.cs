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
}
