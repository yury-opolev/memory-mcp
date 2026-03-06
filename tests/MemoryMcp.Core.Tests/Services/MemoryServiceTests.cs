using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

public class MemoryServiceTests
{
    private readonly IChunkingService chunking = Substitute.For<IChunkingService>();
    private readonly IEmbeddingService embedding = Substitute.For<IEmbeddingService>();
    private readonly IMemoryStore store = Substitute.For<IMemoryStore>();
    private readonly ILogger<MemoryService> logger = Substitute.For<ILogger<MemoryService>>();
    private readonly MemoryService service;

    public MemoryServiceTests()
    {
        this.service = new MemoryService(this.chunking, this.embedding, this.store, this.logger);
    }

    private static float[] FakeVector(int dims = 4) => new float[dims];

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

        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector()]);

        var memoryId = await this.service.IngestAsync(content, "Test Title", ["tag1", "tag2"]);

        Assert.NotNull(memoryId);
        Assert.True(Guid.TryParse(memoryId, out _));

        this.chunking.Received(1).Chunk(content);
        await this.embedding.Received(1).EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        await this.store.Received(1).StoreMemoryAsync(
            memoryId,
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

        this.chunking.Chunk(content).Returns(chunkInfos);
        this.embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns([FakeVector(), FakeVector()]);

        await this.service.IngestAsync(content);

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
}
