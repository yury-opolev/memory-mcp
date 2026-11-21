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
}
