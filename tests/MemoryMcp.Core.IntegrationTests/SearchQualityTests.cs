using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.IntegrationTests;

/// <summary>
/// End-to-end search quality tests using the full stack (chunking, embedding, storage).
/// Verifies that semantic search returns relevant results and ranks them appropriately.
/// Requires Ollama running locally with the configured embedding model.
/// </summary>
[Trait("Category", "Integration")]
public class SearchQualityTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryMcpOptions _options;
    private readonly OllamaEmbeddingService? _embeddingService;
    private readonly WordChunkingService _chunkingService;
    private SqliteVecMemoryStore? _store;
    private MemoryService? _memoryService;
    private bool _ollamaAvailable;

    // Golden test set: memories to ingest
    private static readonly (string Content, string Title, List<string> Tags)[] GoldenMemories =
    [
        (
            "C# supports async/await for asynchronous programming. Use Task and Task<T> for async operations. " +
            "The async keyword marks a method as asynchronous, and await pauses execution until the awaited task completes. " +
            "ConfigureAwait(false) can be used to avoid capturing the synchronization context.",
            "C# Async Programming",
            ["csharp", "async", "programming"]
        ),
        (
            "Docker containers package applications with their dependencies for consistent deployment. " +
            "Use Dockerfile to define the image, docker-compose for multi-container setups. " +
            "Containers are lightweight compared to virtual machines and share the host OS kernel.",
            "Docker Containers Guide",
            ["docker", "devops", "containers"]
        ),
        (
            "PostgreSQL is a powerful open-source relational database. It supports JSONB columns for semi-structured data, " +
            "full-text search with tsvector and tsquery, and advanced indexing with GIN and GiST indexes. " +
            "Connection pooling with PgBouncer is recommended for production workloads.",
            "PostgreSQL Database Notes",
            ["database", "postgresql", "sql"]
        ),
        (
            "Machine learning models can be trained using supervised, unsupervised, or reinforcement learning approaches. " +
            "Common algorithms include linear regression, decision trees, random forests, and neural networks. " +
            "Feature engineering and data preprocessing are critical steps in the ML pipeline.",
            "Machine Learning Fundamentals",
            ["ml", "ai", "programming"]
        ),
        (
            "Git branching strategies include GitFlow, trunk-based development, and GitHub Flow. " +
            "Feature branches isolate work in progress, while main/master holds production-ready code. " +
            "Rebasing provides a cleaner history compared to merge commits.",
            "Git Branching Strategies",
            ["git", "devops", "version-control"]
        ),
    ];

    public SearchQualityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memory-mcp-search-test-{Guid.NewGuid():N}");
        _options = new MemoryMcpOptions
        {
            DataDirectory = _tempDir,
        };

        var optionsWrapper = Options.Create(_options);
        _chunkingService = new WordChunkingService(optionsWrapper);

        try
        {
            _embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            _embeddingService.EmbedAsync("test").GetAwaiter().GetResult();
            _ollamaAvailable = true;
        }
        catch
        {
            _ollamaAvailable = false;
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (!_ollamaAvailable)
            return;

        _store = new SqliteVecMemoryStore(
            Options.Create(_options),
            NullLogger<SqliteVecMemoryStore>.Instance);
        await _store.InitializeAsync();

        _memoryService = new MemoryService(
            _chunkingService,
            _embeddingService!,
            _store,
            NullLogger<MemoryService>.Instance);

        // Ingest all golden memories
        foreach (var (content, title, tags) in GoldenMemories)
        {
            await _memoryService.IngestAsync(content, title, tags);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SkipIfNoOllama()
    {
        if (!_ollamaAvailable)
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
    }

    [Fact]
    public async Task Search_AsyncProgramming_FindsCSharpMemory()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("How do I use async await in C#?", limit: 3);

        Assert.NotEmpty(results);
        // The top result should be the C# async programming memory
        Assert.Equal("C# Async Programming", results[0].Title);
    }

    [Fact]
    public async Task Search_ContainerDeployment_FindsDockerMemory()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("container deployment and packaging applications", limit: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Docker Containers Guide", results[0].Title);
    }

    [Fact]
    public async Task Search_DatabaseQuery_FindsPostgreSQLMemory()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("relational database with JSON support and full text search", limit: 3);

        Assert.NotEmpty(results);
        Assert.Equal("PostgreSQL Database Notes", results[0].Title);
    }

    [Fact]
    public async Task Search_AIModels_FindsMachineLearningMemory()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("training neural networks and AI models", limit: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Machine Learning Fundamentals", results[0].Title);
    }

    [Fact]
    public async Task Search_VersionControl_FindsGitMemory()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("version control branching and merging code", limit: 3);

        Assert.NotEmpty(results);
        Assert.Equal("Git Branching Strategies", results[0].Title);
    }

    [Fact]
    public async Task Search_WithTagFilter_OnlyReturnsMatchingTags()
    {
        SkipIfNoOllama();

        // Search with a broad query but filter to only "devops" tagged memories
        var results = await _memoryService!.SearchAsync(
            "best practices for software development",
            limit: 10,
            tags: ["devops"]);

        Assert.NotEmpty(results);
        // All results should have the "devops" tag
        Assert.All(results, r => Assert.Contains("devops", r.Tags));
    }

    [Fact]
    public async Task Search_WithMinScore_FiltersLowSimilarity()
    {
        SkipIfNoOllama();

        // Very specific query, high threshold
        var results = await _memoryService!.SearchAsync(
            "async await Task ConfigureAwait C#",
            limit: 10,
            minScore: 0.5f);

        // Should get at least the C# memory, but possibly not all 5
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Score >= 0.5f,
            $"Result '{r.Title}' has score {r.Score:F4} below minimum 0.5"));
    }

    [Fact]
    public async Task Search_ReturnsResultsInDescendingScoreOrder()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("programming concepts", limit: 5);

        Assert.NotEmpty(results);
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].Score >= results[i].Score,
                $"Results not in descending order: [{i - 1}]={results[i - 1].Score:F4} < [{i}]={results[i].Score:F4}");
        }
    }

    [Fact]
    public async Task Search_LimitRespected()
    {
        SkipIfNoOllama();

        var results = await _memoryService!.SearchAsync("software development", limit: 2);

        Assert.True(results.Count <= 2, $"Expected at most 2 results, got {results.Count}");
    }

    [Fact]
    public async Task Search_PrecisionAtK_TopResultIsRelevant()
    {
        SkipIfNoOllama();

        // Run multiple queries and check precision@1
        var queries = new (string Query, string ExpectedTitle)[]
        {
            ("asynchronous programming patterns", "C# Async Programming"),
            ("containerization technology", "Docker Containers Guide"),
            ("SQL database indexing", "PostgreSQL Database Notes"),
            ("supervised learning algorithms", "Machine Learning Fundamentals"),
            ("git rebase vs merge", "Git Branching Strategies"),
        };

        int correctAtK1 = 0;
        foreach (var (query, expectedTitle) in queries)
        {
            var results = await _memoryService!.SearchAsync(query, limit: 1);
            if (results.Count > 0 && results[0].Title == expectedTitle)
                correctAtK1++;
        }

        float precision = (float)correctAtK1 / queries.Length;
        Assert.True(precision >= 0.8f,
            $"Precision@1 = {precision:F2} ({correctAtK1}/{queries.Length}). Expected >= 0.80");
    }

    [Fact]
    public async Task Search_RecallAtK3_AllRelevantInTopK()
    {
        SkipIfNoOllama();

        // For each query, the relevant memory should appear in top 3
        var queries = new (string Query, string ExpectedTitle)[]
        {
            ("asynchronous programming patterns", "C# Async Programming"),
            ("containerization technology", "Docker Containers Guide"),
            ("SQL database indexing", "PostgreSQL Database Notes"),
            ("supervised learning algorithms", "Machine Learning Fundamentals"),
            ("git rebase vs merge", "Git Branching Strategies"),
        };

        int foundInTopK = 0;
        foreach (var (query, expectedTitle) in queries)
        {
            var results = await _memoryService!.SearchAsync(query, limit: 3);
            if (results.Any(r => r.Title == expectedTitle))
                foundInTopK++;
        }

        float recall = (float)foundInTopK / queries.Length;
        Assert.True(recall >= 0.8f,
            $"Recall@3 = {recall:F2} ({foundInTopK}/{queries.Length}). Expected >= 0.80");
    }

    public void Dispose()
    {
        _store?.Dispose();
        _embeddingService?.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
