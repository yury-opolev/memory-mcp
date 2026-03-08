using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

public class MemoryServiceTests
{
    private readonly IChunkingService chunking = Substitute.For<IChunkingService>();
    private readonly IEmbeddingService embedding = Substitute.For<IEmbeddingService>();
    private readonly IMemoryStore store = Substitute.For<IMemoryStore>();
    private readonly ILogger<MemoryService> logger = Substitute.For<ILogger<MemoryService>>();
    private readonly MemoryMcpOptions optionsValue = new();
    private readonly MemoryService service;

    public MemoryServiceTests()
    {
        var monitor = CreateMonitor(this.optionsValue);
        this.service = new MemoryService(this.chunking, this.embedding, this.store, monitor, this.logger);
    }

    private static IOptionsMonitor<MemoryMcpOptions> CreateMonitor(MemoryMcpOptions value)
    {
        var monitor = Substitute.For<IOptionsMonitor<MemoryMcpOptions>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static float[] FakeVector(int dims = 4) => new float[dims];

    /// <summary>
    /// Sets up mocks so the duplicate guard passes (no similar memories found).
    /// Call this in tests that need IngestAsync to succeed without duplicates.
    /// </summary>
    private void SetupDuplicateGuardPass()
    {
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(FakeVector());
        this.store.SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());
    }

    // --- Existing tests (updated for IngestResult return type) ---

    [Fact]
    public async Task IngestAsync_EmptyContent_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.service.IngestAsync(""));
    }

    [Fact]
    public async Task IngestAsync_WhitespaceContent_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.service.IngestAsync("   "));
    }

    [Fact]
    public async Task IngestAsync_ValidContent_ChunksEmbedsAndStores()
    {
        var content = "Hello world test content";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = content, StartOffset = 0, Length = content.Length }
        };

        SetupDuplicateGuardPass();
        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        var result = await this.service.IngestAsync(content, "Test Title", ["tag1", "tag2"]);

        Assert.True(result.Success);
        Assert.NotNull(result.MemoryId);
        Assert.True(Guid.TryParse(result.MemoryId, out _));

        this.chunking.Received(1).Chunk(content);
        await this.store.Received(1).StoreMemoryAsync(
            result.MemoryId,
            content,
            Arg.Is<List<ChunkRecord>>(chunks =>
                chunks.Count == 1 &&
                chunks[0].Title == "Test Title" &&
                chunks[0].Tags.Contains("tag1") &&
                chunks[0].Tags.Contains("tag2")),
            Arg.Any<List<float[]>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_MultipleChunks_EmbedsAllChunks()
    {
        var content = "word1 word2 word3 word4 word5 word6";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = "word1 word2 word3", StartOffset = 0, Length = 17 },
            new() { ChunkIndex = 1, Text = "word4 word5 word6", StartOffset = 18, Length = 17 },
        };

        SetupDuplicateGuardPass();
        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector(), FakeVector()]);

        var result = await this.service.IngestAsync(content);

        Assert.True(result.Success);

        await this.embedding.Received(1).EmbedBatchAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.Count() == 2),
            Arg.Any<CancellationToken>());

        await this.store.Received(1).StoreMemoryAsync(
            Arg.Any<string>(),
            content,
            Arg.Is<List<ChunkRecord>>(chunks => chunks.Count == 2),
            Arg.Is<List<float[]>>(vecs => vecs.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_DelegatesToStore()
    {
        var memoryId = Guid.NewGuid().ToString();
        var expected = new MemoryResult
        {
            MemoryId = memoryId,
            Content = "test",
            Title = "title",
        };

        this.store.GetMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await this.service.GetAsync(memoryId);

        Assert.Equal(expected, result);
        await this.store.Received(1).GetMemoryAsync(memoryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        this.store.GetMemoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MemoryResult?)null);

        var result = await this.service.GetAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_DelegatesToStore()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.store.DeleteMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await this.service.DeleteAsync(memoryId);

        Assert.True(result);
        await this.store.Received(1).DeleteMemoryAsync(memoryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NotFound_ReturnsFalse()
    {
        this.store.DeleteMemoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await this.service.DeleteAsync("nonexistent");
        Assert.False(result);
    }

    [Fact]
    public async Task UpdateAsync_NotFound_ReturnsNull()
    {
        this.store.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await this.service.UpdateAsync("nonexistent", content: "new content");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_MetadataOnly_UpdatesWithoutReChunking()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.store.ExistsAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);
        this.store.GetMemoryAsync(memoryId, Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = memoryId, Content = "original", Title = "updated" });

        await this.service.UpdateAsync(memoryId, title: "updated");

        // Should NOT re-chunk or re-embed
        this.chunking.DidNotReceive().Chunk(Arg.Any<string>());
        await this.embedding.DidNotReceive().EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());

        // Should update metadata directly
        await this.store.Received(1).UpdateMetadataAsync(
            memoryId, "updated", null, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ContentChanged_ReChunksAndReEmbeds()
    {
        var memoryId = Guid.NewGuid().ToString();
        var existing = new MemoryResult
        {
            MemoryId = memoryId,
            Content = "original content",
            Title = "Title",
            Tags = ["tag1"],
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        this.store.ExistsAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);
        this.store.GetMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(existing);
        this.store.DeleteMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);

        var newContent = "new content here";
        this.chunking.Chunk(newContent).Returns([
            new ChunkInfo { ChunkIndex = 0, Text = newContent, StartOffset = 0, Length = newContent.Length }
        ]);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        await this.service.UpdateAsync(memoryId, content: newContent);

        // Should delete old, re-chunk, re-embed, store new
        await this.store.Received(1).DeleteMemoryAsync(memoryId, Arg.Any<CancellationToken>());
        this.chunking.Received(1).Chunk(newContent);
        await this.embedding.Received(1).EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await this.store.Received(1).StoreMemoryAsync(
            memoryId, newContent,
            Arg.Is<List<ChunkRecord>>(chunks =>
                chunks[0].Title == "Title" &&  // preserves existing title
                chunks[0].Tags.Contains("tag1") && // preserves existing tags
                chunks[0].CreatedAt == existing.CreatedAt), // preserves original creation time
            Arg.Any<List<float[]>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ContentAndTitle_UsesNewTitle()
    {
        var memoryId = Guid.NewGuid().ToString();
        var existing = new MemoryResult
        {
            MemoryId = memoryId,
            Content = "old",
            Title = "Old Title",
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        this.store.ExistsAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);
        this.store.GetMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(existing);
        this.store.DeleteMemoryAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);

        this.chunking.Chunk(Arg.Any<string>()).Returns([
            new ChunkInfo { ChunkIndex = 0, Text = "new", StartOffset = 0, Length = 3 }
        ]);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        await this.service.UpdateAsync(memoryId, content: "new", title: "New Title");

        await this.store.Received(1).StoreMemoryAsync(
            memoryId, "new",
            Arg.Is<List<ChunkRecord>>(chunks => chunks[0].Title == "New Title"),
            Arg.Any<List<float[]>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.service.SearchAsync(""));
    }

    [Fact]
    public async Task SearchAsync_ValidQuery_EmbedsQueryAndSearchesStore()
    {
        var queryVector = FakeVector();
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(queryVector);

        var expectedResults = new List<SearchResult>
        {
            new() { MemoryId = "id1", Content = "result", Score = 0.9f }
        };
        this.store.SearchAsync(queryVector, 5, null, null, Arg.Any<CancellationToken>())
            .Returns(expectedResults);

        var results = await this.service.SearchAsync("test query", limit: 5);

        Assert.Single(results);
        Assert.Equal("id1", results[0].MemoryId);
        await this.embedding.Received(1).EmbedAsync("test query", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_WithTagsAndMinScore_PassesParametersToStore()
    {
        var queryVector = FakeVector();
        this.embedding.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(queryVector);
        this.store.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var tags = new List<string> { "project" };
        await this.service.SearchAsync("query", limit: 10, minScore: 0.5f, tags: tags);

        await this.store.Received(1).SearchAsync(queryVector, 10, 0.5f, tags, Arg.Any<CancellationToken>());
    }

    // --- New duplicate guard tests ---

    [Fact]
    public async Task IngestAsync_DuplicateDetected_ReturnsRejection()
    {
        var content = "Some content that already exists";
        var existingId = Guid.NewGuid().ToString();

        this.embedding.EmbedAsync(content, Arg.Any<CancellationToken>())
            .Returns(FakeVector());
        this.store.SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = existingId, Content = "Existing content", Title = "Existing Title", Score = 0.92f },
            });

        var result = await this.service.IngestAsync(content);

        Assert.False(result.Success);
        Assert.Null(result.MemoryId);
        Assert.NotNull(result.RejectionReason);
        Assert.Single(result.SimilarMemories);
        Assert.Equal(existingId, result.SimilarMemories[0].MemoryId);
        Assert.Equal("Existing Title", result.SimilarMemories[0].Title);
        Assert.Equal("Existing content", result.SimilarMemories[0].Content);
        Assert.Equal(0.92f, result.SimilarMemories[0].Score);

        // Should NOT have stored anything
        await this.store.DidNotReceive().StoreMemoryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<ChunkRecord>>(),
            Arg.Any<List<float[]>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_MultipleDuplicates_ReturnsAll()
    {
        var content = "Content with multiple near-duplicates";

        this.embedding.EmbedAsync(content, Arg.Any<CancellationToken>())
            .Returns(FakeVector());
        this.store.SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "id1", Content = "First duplicate", Score = 0.93f },
                new() { MemoryId = "id2", Content = "Second duplicate", Score = 0.91f },
                new() { MemoryId = "id3", Content = "Third duplicate", Score = 0.90f },
            });

        var result = await this.service.IngestAsync(content);

        Assert.False(result.Success);
        Assert.Equal(3, result.SimilarMemories.Count);
        Assert.Equal("id1", result.SimilarMemories[0].MemoryId);
        Assert.Equal("id2", result.SimilarMemories[1].MemoryId);
        Assert.Equal("id3", result.SimilarMemories[2].MemoryId);

        // Should NOT have stored anything
        await this.store.DidNotReceive().StoreMemoryAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<ChunkRecord>>(),
            Arg.Any<List<float[]>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_ForceTrue_BypassesDuplicateCheck()
    {
        var content = "Force-ingested content";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = content, StartOffset = 0, Length = content.Length }
        };

        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        var result = await this.service.IngestAsync(content, force: true);

        Assert.True(result.Success);
        Assert.NotNull(result.MemoryId);

        // Should NOT have called EmbedAsync (single) for dedup check
        await this.embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Should NOT have called SearchAsync for dedup check
        await this.store.DidNotReceive().SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_ThresholdZero_SkipsDuplicateCheck()
    {
        // Create a new service with threshold=0
        var zeroThresholdMonitor = CreateMonitor(new MemoryMcpOptions { DuplicateThreshold = 0 });
        var svc = new MemoryService(this.chunking, this.embedding, this.store, zeroThresholdMonitor, this.logger);

        var content = "Content with disabled dedup";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = content, StartOffset = 0, Length = content.Length }
        };

        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        var result = await svc.IngestAsync(content);

        Assert.True(result.Success);
        Assert.NotNull(result.MemoryId);

        // Should NOT have called EmbedAsync (single) for dedup check
        await this.embedding.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Should NOT have called SearchAsync for dedup check
        await this.store.DidNotReceive().SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_NoDuplicate_Succeeds()
    {
        var content = "Unique content that has no duplicates";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = content, StartOffset = 0, Length = content.Length }
        };

        SetupDuplicateGuardPass();
        this.chunking.Chunk(content).Returns(chunkInfos);

        var result = await this.service.IngestAsync(content);

        Assert.True(result.Success);
        Assert.NotNull(result.MemoryId);
        Assert.True(Guid.TryParse(result.MemoryId, out _));
        Assert.Empty(result.SimilarMemories);
    }

    [Fact]
    public async Task IngestAsync_SingleChunk_ReusesEmbedding()
    {
        var content = "Single chunk content";
        var contentVector = new float[] { 1.0f, 2.0f, 3.0f, 4.0f };
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = content, StartOffset = 0, Length = content.Length }
        };

        // Dedup check returns no duplicates
        this.embedding.EmbedAsync(content, Arg.Any<CancellationToken>())
            .Returns(contentVector);
        this.store.SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        this.chunking.Chunk(content).Returns(chunkInfos);

        var result = await this.service.IngestAsync(content);

        Assert.True(result.Success);

        // EmbedAsync called once (for dedup check)
        await this.embedding.Received(1).EmbedAsync(content, Arg.Any<CancellationToken>());
        // EmbedBatchAsync should NOT be called (reuse the dedup embedding)
        await this.embedding.DidNotReceive().EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        // The vector passed to store should be the same one from EmbedAsync
        await this.store.Received(1).StoreMemoryAsync(
            Arg.Any<string>(),
            content,
            Arg.Any<List<ChunkRecord>>(),
            Arg.Is<List<float[]>>(vecs => vecs.Count == 1 && ReferenceEquals(vecs[0], contentVector)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_MultiChunk_EmbedsChunksSeparately()
    {
        var content = "Multi chunk content that is long enough to be split";
        var chunkInfos = new List<ChunkInfo>
        {
            new() { ChunkIndex = 0, Text = "Multi chunk content", StartOffset = 0, Length = 19 },
            new() { ChunkIndex = 1, Text = "long enough to be split", StartOffset = 20, Length = 23 },
        };

        // Dedup check returns no duplicates
        this.embedding.EmbedAsync(content, Arg.Any<CancellationToken>())
            .Returns(FakeVector());
        this.store.SearchAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector(), FakeVector()]);

        var result = await this.service.IngestAsync(content);

        Assert.True(result.Success);

        // EmbedAsync called once (dedup check on full content)
        await this.embedding.Received(1).EmbedAsync(content, Arg.Any<CancellationToken>());
        // EmbedBatchAsync also called once (for the 2 chunks)
        await this.embedding.Received(1).EmbedBatchAsync(
            Arg.Is<IEnumerable<string>>(texts => texts.Count() == 2),
            Arg.Any<CancellationToken>());
    }
}
