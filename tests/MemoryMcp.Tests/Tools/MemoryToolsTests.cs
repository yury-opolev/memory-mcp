using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace MemoryMcp.Tests.Tools;

/// <summary>
/// Tests for the MCP tool layer. Verifies response formatting, tag parsing,
/// content truncation, and correct delegation to IMemoryService and IHybridMemorySearch.
/// </summary>
public class MemoryToolsTests
{
    private readonly IMemoryService memoryService = Substitute.For<IMemoryService>();
    private readonly IHybridMemorySearch hybridSearch = Substitute.For<IHybridMemorySearch>();
    private readonly MemoryTools tools;

    public MemoryToolsTests()
    {
        var options = Options.Create(new MemoryMcpOptions
        {
            SearchMaxContentLength = 100, // Small for testing truncation
        });
        var logger = Substitute.For<ILogger<MemoryTools>>();
        this.tools = new MemoryTools(this.memoryService, this.hybridSearch, options, logger);
    }

    private static IngestResult SuccessResult(string? memoryId = null) => new()
    {
        Success = true,
        MemoryId = memoryId ?? Guid.NewGuid().ToString(),
    };

    private static IngestResult RejectionResult(params SimilarMemory[] similarMemories) => new()
    {
        Success = false,
        RejectionReason = $"Found {similarMemories.Length} existing memories above similarity threshold (0.90).",
        SimilarMemories = [.. similarMemories],
    };

    // --- IngestMemory Tests ---

    [Fact]
    public async Task IngestMemory_ReturnsSuccessWithId()
    {
        var expectedId = Guid.NewGuid().ToString();
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult(expectedId));

        var result = await this.tools.IngestMemory("Test content", "Title");

        Assert.Contains("Memory stored successfully", result);
        Assert.Contains(expectedId, result);
    }

    [Fact]
    public async Task IngestMemory_WithJsonTags_ParsesTagsCorrectly()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await this.tools.IngestMemory("Content", tags: "[\"tag1\",\"tag2\"]");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            Arg.Is<List<string>?>(t => t != null && t.Count == 2 && t[0] == "tag1" && t[1] == "tag2"),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithInvalidJsonTags_TreatsAsingleTag()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await this.tools.IngestMemory("Content", tags: "simple-tag");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            Arg.Is<List<string>?>(t => t != null && t.Count == 1 && t[0] == "simple-tag"),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithNullTags_PassesNull()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await this.tools.IngestMemory("Content");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            null,
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithEmptyTags_PassesNull()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await this.tools.IngestMemory("Content", tags: "  ");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            null,
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_OllamaError_ReturnsCleanErrorMessage()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Failed to connect to Ollama at http://localhost:11434.",
                new HttpRequestException("Connection refused")));

        var result = await this.tools.IngestMemory("Content");

        Assert.Contains("Error:", result);
        Assert.Contains("Failed to connect to Ollama", result);
    }

    [Fact]
    public async Task IngestMemory_DuplicateDetected_ReturnsRejectionWithSimilarMemories()
    {
        var similarId = Guid.NewGuid().ToString();
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(RejectionResult(
                new SimilarMemory { MemoryId = similarId, Content = "Existing content", Title = "Existing", Score = 0.95f }));

        var result = await this.tools.IngestMemory("New content");

        Assert.Contains("existing memories above similarity threshold", result);
        Assert.Contains(similarId, result);
        Assert.Contains("Existing content", result);
        Assert.Contains("0.9500", result);
        Assert.DoesNotContain("Memory stored successfully", result);
    }

    [Fact]
    public async Task IngestMemory_WithForceTrue_PassesForceToService()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await this.tools.IngestMemory("Content", force: true);

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            null,
            true,
            Arg.Any<CancellationToken>());
    }

    // --- GetMemory Tests ---

    [Fact]
    public async Task GetMemory_Found_ReturnsFormattedResult()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.memoryService.GetAsync(memoryId, Arg.Any<CancellationToken>())
            .Returns(new MemoryResult
            {
                MemoryId = memoryId,
                Content = "Test content",
                Title = "Test Title",
                Tags = ["tag1", "tag2"],
                CreatedAt = DateTimeOffset.Parse("2025-01-15T10:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2025-01-15T12:00:00Z"),
            });

        var result = await this.tools.GetMemory(memoryId);

        Assert.Contains($"ID: {memoryId}", result);
        Assert.Contains("Title: Test Title", result);
        Assert.Contains("tag1", result);
        Assert.Contains("tag2", result);
        Assert.Contains("Test content", result);
        Assert.Contains("Created:", result);
        Assert.Contains("Updated:", result);
    }

    [Fact]
    public async Task GetMemory_NotFound_ReturnsNotFoundMessage()
    {
        this.memoryService.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((MemoryResult?)null);

        var result = await this.tools.GetMemory("nonexistent-id");

        Assert.Contains("Memory not found", result);
        Assert.Contains("nonexistent-id", result);
    }

    // --- UpdateMemory Tests ---

    [Fact]
    public async Task UpdateMemory_NoFieldsProvided_ReturnsErrorMessage()
    {
        var result = await this.tools.UpdateMemory("some-id");

        Assert.Contains("No updates provided", result);
    }

    [Fact]
    public async Task UpdateMemory_Success_ReturnsUpdatedResult()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.memoryService.UpdateAsync(
            memoryId, Arg.Any<string?>(), "New Title", Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryResult
            {
                MemoryId = memoryId,
                Content = "Content",
                Title = "New Title",
            });

        var result = await this.tools.UpdateMemory(memoryId, title: "New Title");

        Assert.Contains("Memory updated successfully", result);
        Assert.Contains("New Title", result);
    }

    [Fact]
    public async Task UpdateMemory_NotFound_ReturnsNotFoundMessage()
    {
        this.memoryService.UpdateAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns((MemoryResult?)null);

        var result = await this.tools.UpdateMemory("nonexistent", title: "Title");

        Assert.Contains("Memory not found", result);
    }

    [Fact]
    public async Task UpdateMemory_WithTags_ParsesAndPassesTags()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.memoryService.UpdateAsync(
            memoryId, null, null, Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryResult { MemoryId = memoryId, Content = "c" });

        await this.tools.UpdateMemory(memoryId, tags: "[\"new-tag\"]");

        await this.memoryService.Received(1).UpdateAsync(
            memoryId,
            null,
            null,
            Arg.Is<List<string>?>(t => t != null && t.Count == 1 && t[0] == "new-tag"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemory_OllamaError_ReturnsCleanErrorMessage()
    {
        this.memoryService.UpdateAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Ollama returned an error for model 'test'.",
                new Exception("model not found")));

        var result = await this.tools.UpdateMemory("id", content: "new content");

        Assert.Contains("Error:", result);
        Assert.Contains("Ollama returned an error", result);
    }

    // --- DeleteMemory Tests ---

    [Fact]
    public async Task DeleteMemory_Success_ReturnsDeletedMessage()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.memoryService.DeleteAsync(memoryId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await this.tools.DeleteMemory(memoryId);

        Assert.Contains("deleted successfully", result);
        Assert.Contains(memoryId, result);
    }

    [Fact]
    public async Task DeleteMemory_NotFound_ReturnsNotFoundMessage()
    {
        this.memoryService.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await this.tools.DeleteMemory("nonexistent");

        Assert.Contains("Memory not found", result);
    }

    // --- SearchMemory Tests ---

    private static SearchResult Memory(string id, string content = "Content", float score = 0.8f, string? title = null, List<string>? tags = null) => new()
    {
        MemoryId = id,
        Content = content,
        Title = title,
        Tags = tags ?? [],
        Score = score,
        CreatedAt = DateTimeOffset.Parse("2025-01-15T10:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2025-01-15T12:00:00Z"),
    };

    private static HybridSearchHit SemanticHit(SearchResult memory, int rank = 1) =>
        new(memory, MemoryMatchKind.Semantic, memory.Score, null, rank, memory.Score);

    private void SearchReturns(MemorySearchMode effectiveMode, string? fallback, params HybridSearchHit[] hits) =>
        this.hybridSearch.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HybridSearchResult(hits, effectiveMode, fallback, hits.Length > 0 ? hits[0].Similarity : null));

    [Fact]
    public async Task SearchMemory_NoResults_ReturnsNoMatchMessage()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null);

        var result = await this.tools.SearchMemory("some query");

        Assert.Contains("No matching memories found", result);
    }

    [Fact]
    public async Task SearchMemory_WithResults_FormatsCorrectly()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id1", "Short content", 0.9123f, "Result Title", ["tag1"])));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Found 1 matching memories", result);
        Assert.Contains("id1", result);
        Assert.Contains("0.9123", result);
        Assert.Contains("Result Title", result);
        Assert.Contains("tag1", result);
        Assert.Contains("Short content", result);
    }

    [Fact]
    public async Task SearchMemory_LongContent_IsTruncated()
    {
        // SearchMaxContentLength is set to 100 in test setup
        var longContent = new string('A', 200);
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id-truncated", longContent)));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("[truncated]", result);
        Assert.Contains("get_memory", result);
        Assert.Contains("id-truncated", result);
        // Should NOT contain the full 200-char content
        Assert.DoesNotContain(longContent, result);
    }

    [Fact]
    public async Task SearchMemory_ShortContent_IsNotTruncated()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id-short", "Short", 0.7f)));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Short", result);
        Assert.DoesNotContain("[truncated]", result);
    }

    [Fact]
    public async Task SearchMemory_MultipleResults_FormatsAllInRankOrder()
    {
        this.SearchReturns(
            MemorySearchMode.Hybrid,
            null,
            SemanticHit(Memory("id1", "First", 0.9f), 1),
            SemanticHit(Memory("id2", "Second", 0.8f), 2),
            SemanticHit(Memory("id3", "Third", 0.7f), 3));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Found 3 matching memories", result);
        Assert.True(result.IndexOf("id1", StringComparison.Ordinal) < result.IndexOf("id2", StringComparison.Ordinal));
        Assert.True(result.IndexOf("id2", StringComparison.Ordinal) < result.IndexOf("id3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchMemory_PassesParametersCorrectly()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null);

        await this.tools.SearchMemory("test query", limit: 10, minScore: 0.5f, tags: "[\"tag1\",\"tag2\"]");

        await this.hybridSearch.Received(1).SearchAsync(
            Arg.Is<HybridSearchRequest>(r =>
                r.Query == "test query"
                && r.Limit == 10
                && r.MinScore == 0.5f
                && r.Filter.Tags != null && r.Filter.Tags.Count == 2 && r.Filter.Tags[0] == "tag1" && r.Filter.Tags[1] == "tag2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemory_NoTags_SearchesWithoutATagFilter()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null);

        await this.tools.SearchMemory("test query");

        await this.hybridSearch.Received(1).SearchAsync(
            Arg.Is<HybridSearchRequest>(r => !r.Filter.HasTags && r.MinScore == null && r.Limit == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemory_ByDefault_RequestsHybridMode()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null);

        await this.tools.SearchMemory("query");

        await this.hybridSearch.Received(1).SearchAsync(
            Arg.Is<HybridSearchRequest>(r => r.Mode == MemorySearchMode.Hybrid), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(MemorySearchMode.Dense)]
    [InlineData(MemorySearchMode.Keyword)]
    public async Task SearchMemory_ConfiguredDefaultMode_IsTheModeRequested(MemorySearchMode configured)
    {
        var options = Options.Create(new MemoryMcpOptions { Search = { DefaultMode = configured } });
        var configuredTools = new MemoryTools(this.memoryService, this.hybridSearch, options, Substitute.For<ILogger<MemoryTools>>());
        this.SearchReturns(configured, null);

        await configuredTools.SearchMemory("query");

        await this.hybridSearch.Received(1).SearchAsync(
            Arg.Is<HybridSearchRequest>(r => r.Mode == configured), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemory_ReportsTheModeThatRan()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id1")));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Found 1 matching memories (hybrid search):", result);
    }

    [Fact]
    public async Task SearchMemory_Fallback_ReportsTheModeThatRanAndWhy()
    {
        var keywordOnly = new HybridSearchHit(Memory("id1", score: 0f), MemoryMatchKind.Keyword, 0.016, 1, null, null);
        this.SearchReturns(MemorySearchMode.Keyword, "the query embedding failed", keywordOnly);

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Found 1 matching memories (keyword search; hybrid was not possible: the query embedding failed):", result);
    }

    [Fact]
    public async Task SearchMemory_FallbackWithNoResults_StillSaysWhy()
    {
        this.SearchReturns(MemorySearchMode.Keyword, "the query embedding failed");

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("No matching memories found", result);
        Assert.Contains("keyword search; hybrid was not possible: the query embedding failed", result);
    }

    [Fact]
    public async Task SearchMemory_KeywordOnlyHit_ShowsTheMatchKindInsteadOfAZeroSimilarity()
    {
        var keywordOnly = new HybridSearchHit(Memory("id-keyword", score: 0f), MemoryMatchKind.Keyword, 0.016, 1, null, null);
        var both = new HybridSearchHit(Memory("id-both", score: 0.7241f), MemoryMatchKind.Both, 0.032, 2, 1, 0.7241f);
        this.SearchReturns(MemorySearchMode.Hybrid, null, both, keywordOnly);

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("--- Memory: id-both (Score: 0.7241, Match: semantic+keyword) ---", result);
        Assert.Contains("--- Memory: id-keyword (Score: n/a, Match: keyword) ---", result);
        Assert.DoesNotContain("0.0000", result);
    }

    [Fact]
    public async Task SearchMemory_SemanticHit_KeepsTheScoreHeader()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id1", score: 0.9123f)));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("--- Memory: id1 (Score: 0.9123, Match: semantic) ---", result);
    }

    [Fact]
    public async Task SearchMemory_NoTitle_DoesNotShowTitleLine()
    {
        this.SearchReturns(MemorySearchMode.Hybrid, null, SemanticHit(Memory("id-no-title", "Content without title", 0.6f)));

        var result = await this.tools.SearchMemory("query");

        Assert.DoesNotContain("Title:", result);
        Assert.DoesNotContain("Tags:", result);
    }

    [Fact]
    public async Task SearchMemory_OllamaError_ReturnsCleanErrorMessage()
    {
        this.hybridSearch.SearchAsync(Arg.Any<HybridSearchRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Failed to connect to Ollama at http://localhost:11434.",
                new HttpRequestException("Connection refused")));

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Error:", result);
        Assert.Contains("Failed to connect to Ollama", result);
    }

    [Fact]
    public async Task GetMemory_NoTitle_DoesNotShowTitleLine()
    {
        var memoryId = Guid.NewGuid().ToString();
        this.memoryService.GetAsync(memoryId, Arg.Any<CancellationToken>())
            .Returns(new MemoryResult
            {
                MemoryId = memoryId,
                Content = "Content",
                Title = null,
                Tags = [],
            });

        var result = await this.tools.GetMemory(memoryId);

        Assert.DoesNotContain("Title:", result);
        Assert.DoesNotContain("Tags:", result);
        Assert.Contains("Content", result);
    }
}
