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
/// Tests for SqliteVecMemoryStore.
/// Note: These tests require the sqlite-vec native extension to be loadable.
/// They use file-based SQLite databases in temp directories to match production behavior.
/// </summary>
public class SqliteVecMemoryStoreTests : IDisposable
{
    private readonly string tempDir;
    private readonly SqliteVecMemoryStore store;

    public SqliteVecMemoryStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memorymcp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.tempDir);

        var options = Options.Create(new MemoryMcpOptions
        {
            DataDirectory = this.tempDir,
            MemoriesSubdirectory = "memories",
            DatabaseFileName = "test.db",
            Ollama = new OllamaOptions { Dimensions = 4 }, // Small dims for testing
        });
        var logger = Substitute.For<ILogger<SqliteVecMemoryStore>>();
        this.store = new SqliteVecMemoryStore(options, new NullContentEncryptor(), logger);
    }

    public void Dispose()
    {
        this.store.Dispose();
        if (Directory.Exists(this.tempDir))
        {
            try
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
            catch
            {
                /* best effort cleanup */
            }
        }
        GC.SuppressFinalize(this);
    }

    private static float[] RandomVector(int dims = 4)
    {
        var rng = Random.Shared;
        var vec = new float[dims];
        for (int i = 0; i < dims; i++)
        {
            vec[i] = (float)rng.NextDouble();
        }
        return vec;
    }

    private static (List<ChunkRecord> chunks, List<float[]> vectors) CreateTestChunks(
        string memoryId, string? title = null, List<string>? tags = null, int count = 1, int dims = 4)
    {
        var now = DateTimeOffset.UtcNow;
        var chunks = new List<ChunkRecord>();
        var vectors = new List<float[]>();

        for (int i = 0; i < count; i++)
        {
            chunks.Add(new ChunkRecord
            {
                MemoryId = memoryId,
                ChunkIndex = i,
                StartOffset = i * 100,
                Length = 100,
                Title = title,
                Tags = tags ?? [],
                CreatedAt = now,
                UpdatedAt = now,
            });
            vectors.Add(RandomVector(dims));
        }

        return (chunks, vectors);
    }

    [Fact]
    public async Task InitializeAsync_CreatesSchemaAndDirectories()
    {
        await this.store.InitializeAsync();

        Assert.True(Directory.Exists(Path.Combine(this.tempDir, "memories")));
        Assert.True(File.Exists(Path.Combine(this.tempDir, "test.db")));
    }

    [Fact]
    public async Task StoreAndGetMemory_RoundTrips()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var content = "This is test content for round-trip verification.";
        var (chunks, vectors) = CreateTestChunks(memoryId, title: "Test Title", tags: ["tag1", "tag2"]);

        await this.store.StoreMemoryAsync(memoryId, content, chunks, vectors);

        var result = await this.store.GetMemoryAsync(memoryId);

        Assert.NotNull(result);
        Assert.Equal(memoryId, result.MemoryId);
        Assert.Equal("Test Title", result.Title);
        Assert.Equal(content, result.Content);
        Assert.Contains("tag1", result.Tags);
        Assert.Contains("tag2", result.Tags);
    }

    [Fact]
    public async Task GetMemoryAsync_NotFound_ReturnsNull()
    {
        await this.store.InitializeAsync();

        var result = await this.store.GetMemoryAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueForExisting()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);
        await this.store.StoreMemoryAsync(memoryId, "content", chunks, vectors);

        Assert.True(await this.store.ExistsAsync(memoryId));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForNonExisting()
    {
        await this.store.InitializeAsync();
        Assert.False(await this.store.ExistsAsync("nonexistent"));
    }

    [Fact]
    public async Task DeleteMemoryAsync_RemovesChunksAndFile()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId);
        await this.store.StoreMemoryAsync(memoryId, "to be deleted", chunks, vectors);

        Assert.True(await this.store.ExistsAsync(memoryId));

        var deleted = await this.store.DeleteMemoryAsync(memoryId);

        Assert.True(deleted);
        Assert.False(await this.store.ExistsAsync(memoryId));
        Assert.Null(await this.store.GetMemoryAsync(memoryId));
    }

    [Fact]
    public async Task DeleteMemoryAsync_NotFound_ReturnsFalse()
    {
        await this.store.InitializeAsync();
        Assert.False(await this.store.DeleteMemoryAsync("nonexistent"));
    }

    [Fact]
    public async Task UpdateMetadataAsync_UpdatesTitleAndTags()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId, title: "Old Title", tags: ["old"]);
        await this.store.StoreMemoryAsync(memoryId, "content", chunks, vectors);

        await this.store.UpdateMetadataAsync(memoryId, "New Title", ["new1", "new2"], DateTimeOffset.UtcNow);

        var result = await this.store.GetMemoryAsync(memoryId);
        Assert.NotNull(result);
        Assert.Equal("New Title", result.Title);
        Assert.Contains("new1", result.Tags);
        Assert.Contains("new2", result.Tags);
        Assert.DoesNotContain("old", result.Tags);
    }

    [Fact]
    public async Task UpdateMetadataAsync_TitleOnly_PreservesTags()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId, title: "Old Title", tags: ["keep"]);
        await this.store.StoreMemoryAsync(memoryId, "content", chunks, vectors);

        await this.store.UpdateMetadataAsync(memoryId, "New Title", null, DateTimeOffset.UtcNow);

        var result = await this.store.GetMemoryAsync(memoryId);
        Assert.NotNull(result);
        Assert.Equal("New Title", result.Title);
        Assert.Contains("keep", result.Tags);
    }

    [Fact]
    public async Task StoreMemoryAsync_MultipleChunks_StoresAll()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, vectors) = CreateTestChunks(memoryId, title: "Multi", count: 3);
        await this.store.StoreMemoryAsync(memoryId, "long content here", chunks, vectors);

        Assert.True(await this.store.ExistsAsync(memoryId));
        var result = await this.store.GetMemoryAsync(memoryId);
        Assert.NotNull(result);
        Assert.Equal("Multi", result.Title);
    }

    [Fact]
    public async Task SearchAsync_FindsSimilarVectors()
    {
        await this.store.InitializeAsync();

        // Store a memory with a known vector
        var memoryId = Guid.NewGuid().ToString();
        var targetVector = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var chunks = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = memoryId, ChunkIndex = 0,
                StartOffset = 0, Length = 5,
                Title = "Target", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };
        await this.store.StoreMemoryAsync(memoryId, "target", chunks, [targetVector]);

        // Store another memory with an orthogonal vector
        var otherId = Guid.NewGuid().ToString();
        var otherVector = new float[] { 0.0f, 0.0f, 0.0f, 1.0f };
        var otherChunks = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = otherId, ChunkIndex = 0,
                StartOffset = 0, Length = 5,
                Title = "Other", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };
        await this.store.StoreMemoryAsync(otherId, "other", otherChunks, [otherVector]);

        // Search with a vector similar to target
        var queryVector = new float[] { 0.9f, 0.1f, 0.0f, 0.0f };
        var results = await this.store.SearchAsync(queryVector, limit: 2);

        Assert.Equal(2, results.Count);
        // The target should be the first result (most similar)
        Assert.Equal(memoryId, results[0].MemoryId);
        Assert.Equal("Target", results[0].Title);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_WithTagFilter_FiltersResults()
    {
        await this.store.InitializeAsync();

        // Store memory with tag "project"
        var id1 = Guid.NewGuid().ToString();
        var vec = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var chunks1 = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = id1, ChunkIndex = 0,
                StartOffset = 0, Length = 5,
                Title = "With Tag", Tags = ["project"],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };
        await this.store.StoreMemoryAsync(id1, "tagged content", chunks1, [vec]);

        // Store memory without tag
        var id2 = Guid.NewGuid().ToString();
        var vec2 = new float[] { 0.95f, 0.05f, 0.0f, 0.0f };
        var chunks2 = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = id2, ChunkIndex = 0,
                StartOffset = 0, Length = 5,
                Title = "No Tag", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };
        await this.store.StoreMemoryAsync(id2, "untagged content", chunks2, [vec2]);

        // Search with tag filter
        var results = await this.store.SearchAsync(vec, limit: 10, tags: ["project"]);

        Assert.Single(results);
        Assert.Equal(id1, results[0].MemoryId);
    }

    [Fact]
    public async Task SearchAsync_WithMinScore_FiltersLowScores()
    {
        await this.store.InitializeAsync();

        // Store memory
        var id1 = Guid.NewGuid().ToString();
        var vec1 = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var chunks1 = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = id1, ChunkIndex = 0,
                StartOffset = 0, Length = 5,
                Title = "Similar", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }
        };
        await this.store.StoreMemoryAsync(id1, "similar", chunks1, [vec1]);

        // Search with high min score using an orthogonal vector (should get filtered)
        var queryVector = new float[] { 0.0f, 1.0f, 0.0f, 0.0f };
        var results = await this.store.SearchAsync(queryVector, limit: 10, minScore: 0.99f);

        // Orthogonal vectors should have low similarity and be filtered
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MultipleChunksSameMemory_GroupsByMemory()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var vec1 = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var vec2 = new float[] { 0.5f, 0.5f, 0.0f, 0.0f };
        var chunks = new List<ChunkRecord>
        {
            new()
            {
                MemoryId = memoryId, ChunkIndex = 0,
                StartOffset = 0, Length = 50,
                Title = "Doc", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                MemoryId = memoryId, ChunkIndex = 1,
                StartOffset = 50, Length = 50,
                Title = "Doc", Tags = [],
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        await this.store.StoreMemoryAsync(memoryId, "long document content here", chunks, [vec1, vec2]);

        var queryVector = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var results = await this.store.SearchAsync(queryVector, limit: 10);

        // Even though there are 2 chunks, should return 1 memory
        Assert.Single(results);
        Assert.Equal(memoryId, results[0].MemoryId);
    }

    [Fact]
    public async Task SearchAsync_LimitRespected()
    {
        await this.store.InitializeAsync();

        // Store 5 memories
        for (int i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid().ToString();
            var vec = RandomVector();
            var chunks = new List<ChunkRecord>
            {
                new()
                {
                    MemoryId = id, ChunkIndex = 0,
                    StartOffset = 0, Length = 5,
                    Title = $"Memory {i}", Tags = [],
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                }
            };
            await this.store.StoreMemoryAsync(id, $"content {i}", chunks, [vec]);
        }

        var results = await this.store.SearchAsync(RandomVector(), limit: 3);
        Assert.True(results.Count <= 3);
    }

    [Fact]
    public async Task StoreMemoryAsync_MismatchedCountsThrows()
    {
        await this.store.InitializeAsync();

        var memoryId = Guid.NewGuid().ToString();
        var (chunks, _) = CreateTestChunks(memoryId, count: 2);
        var vectors = new List<float[]> { RandomVector() }; // Only 1 vector for 2 chunks

        await Assert.ThrowsAsync<ArgumentException>(
            () => this.store.StoreMemoryAsync(memoryId, "content", chunks, vectors));
    }
}
