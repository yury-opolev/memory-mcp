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
/// content truncation, and correct delegation to IMemoryService.
/// </summary>
public class MemoryToolsTests
{
    private readonly IMemoryService memoryService = Substitute.For<IMemoryService>();
    private readonly MemoryTools tools;

    public MemoryToolsTests()
    {
        var options = Options.Create(new MemoryMcpOptions
        {
            SearchMaxContentLength = 100, // Small for testing truncation
        });
        var logger = Substitute.For<ILogger<MemoryTools>>();
        this.tools = new MemoryTools(this.memoryService, options, logger);
    }

    // --- IngestMemory Tests ---

    [Fact]
    public async Task IngestMemory_ReturnsSuccessWithId()
    {
        var expectedId = Guid.NewGuid().ToString();
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(expectedId);

        var result = await this.tools.IngestMemory("Test content", "Title");

        Assert.Contains("Memory stored successfully", result);
        Assert.Contains(expectedId, result);
    }

    [Fact]
    public async Task IngestMemory_WithJsonTags_ParsesTagsCorrectly()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns("id");

        await this.tools.IngestMemory("Content", tags: "[\"tag1\",\"tag2\"]");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            Arg.Is<List<string>?>(t => t != null && t.Count == 2 && t[0] == "tag1" && t[1] == "tag2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithInvalidJsonTags_TreatsAsingleTag()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns("id");

        await this.tools.IngestMemory("Content", tags: "simple-tag");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            Arg.Is<List<string>?>(t => t != null && t.Count == 1 && t[0] == "simple-tag"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithNullTags_PassesNull()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns("id");

        await this.tools.IngestMemory("Content");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_WithEmptyTags_PassesNull()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns("id");

        await this.tools.IngestMemory("Content", tags: "  ");

        await this.memoryService.Received(1).IngestAsync(
            "Content",
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestMemory_OllamaError_ReturnsCleanErrorMessage()
    {
        this.memoryService.IngestAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Failed to connect to Ollama at http://localhost:11434.",
                new HttpRequestException("Connection refused")));

        var result = await this.tools.IngestMemory("Content");

        Assert.Contains("Error:", result);
        Assert.Contains("Failed to connect to Ollama", result);
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

    [Fact]
    public async Task SearchMemory_NoResults_ReturnsNoMatchMessage()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var result = await this.tools.SearchMemory("some query");

        Assert.Contains("No matching memories found", result);
    }

    [Fact]
    public async Task SearchMemory_WithResults_FormatsCorrectly()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    MemoryId = "id1",
                    Content = "Short content",
                    Title = "Result Title",
                    Tags = ["tag1"],
                    Score = 0.9123f,
                    CreatedAt = DateTimeOffset.Parse("2025-01-15T10:00:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2025-01-15T12:00:00Z"),
                },
            });

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

        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    MemoryId = "id-truncated",
                    Content = longContent,
                    Score = 0.8f,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            });

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
        var shortContent = "Short";

        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    MemoryId = "id-short",
                    Content = shortContent,
                    Score = 0.7f,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            });

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Short", result);
        Assert.DoesNotContain("[truncated]", result);
    }

    [Fact]
    public async Task SearchMemory_MultipleResults_FormatsAll()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new() { MemoryId = "id1", Content = "First", Score = 0.9f, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
                new() { MemoryId = "id2", Content = "Second", Score = 0.8f, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
                new() { MemoryId = "id3", Content = "Third", Score = 0.7f, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            });

        var result = await this.tools.SearchMemory("query");

        Assert.Contains("Found 3 matching memories", result);
        Assert.Contains("id1", result);
        Assert.Contains("id2", result);
        Assert.Contains("id3", result);
    }

    [Fact]
    public async Task SearchMemory_PassesParametersCorrectly()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        await this.tools.SearchMemory("test query", limit: 10, minScore: 0.5f, tags: "[\"tag1\",\"tag2\"]");

        await this.memoryService.Received(1).SearchAsync(
            "test query",
            10,
            0.5f,
            Arg.Is<List<string>?>(t => t != null && t.Count == 2 && t[0] == "tag1" && t[1] == "tag2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchMemory_NoTitle_DoesNotShowTitleLine()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>
            {
                new()
                {
                    MemoryId = "id-no-title",
                    Content = "Content without title",
                    Title = null,
                    Tags = [],
                    Score = 0.6f,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            });

        var result = await this.tools.SearchMemory("query");

        Assert.DoesNotContain("Title:", result);
        Assert.DoesNotContain("Tags:", result);
    }

    [Fact]
    public async Task SearchMemory_OllamaError_ReturnsCleanErrorMessage()
    {
        this.memoryService.SearchAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float?>(), Arg.Any<List<string>?>(), Arg.Any<CancellationToken>())
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
