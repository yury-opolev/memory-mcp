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
    private readonly ITestOutputHelper output;
    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly OllamaEmbeddingService? embeddingService;
    private readonly WordChunkingService chunkingService;
    private SqliteVecMemoryStore? store;
    private MemoryService? memoryService;
    private bool ollamaAvailable;

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

    public SearchQualityTests(ITestOutputHelper output)
    {
        this.output = output;
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memory-mcp-search-test-{Guid.NewGuid():N}");
        this.options = TestOptionsHelper.CreateOptions(dataDirectory: this.tempDir);

        var optionsWrapper = Options.Create(this.options);
        this.chunkingService = new WordChunkingService(optionsWrapper);

        try
        {
            this.embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            this.embeddingService.EmbedAsync("test").GetAwaiter().GetResult();
            this.ollamaAvailable = true;
            this.output.WriteLine($"Ollama connected at {this.options.Ollama.Endpoint}");
            this.output.WriteLine($"Embedding model: {this.options.Ollama.Model} ({this.options.Ollama.Dimensions} dimensions)");
            this.output.WriteLine("");
        }
        catch (Exception ex)
        {
            this.ollamaAvailable = false;
            this.output.WriteLine($"Ollama not available: {ex.Message}");
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (!this.ollamaAvailable)
        {
            return;
        }

        this.store = new SqliteVecMemoryStore(
            Options.Create(this.options),
            NullLogger<SqliteVecMemoryStore>.Instance);
        await this.store.InitializeAsync();

        this.memoryService = new MemoryService(
            this.chunkingService,
            this.embeddingService!,
            this.store,
            NullLogger<MemoryService>.Instance);

        // Ingest all golden memories
        this.output.WriteLine($"Ingesting {GoldenMemories.Length} golden memories...");
        foreach (var (content, title, tags) in GoldenMemories)
        {
            var id = await this.memoryService.IngestAsync(content, title, tags);
            this.output.WriteLine($"  Ingested \"{title}\" (id: {id}, tags: [{string.Join(", ", tags)}])");
        }
        this.output.WriteLine("");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SkipIfNoOllama()
    {
        if (!this.ollamaAvailable)
        {
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
        }
    }

    private void LogSearchResults(string query, IReadOnlyList<SearchResult> results)
    {
        this.output.WriteLine($"Query: \"{query}\"");
        this.output.WriteLine($"Results: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            this.output.WriteLine($"  [{i + 1}] \"{r.Title}\" (score: {r.Score:F4}, tags: [{string.Join(", ", r.Tags)}])");
        }
    }

    [Fact]
    public async Task Search_AsyncProgramming_FindsCSharpMemory()
    {
        this.SkipIfNoOllama();

        string query = "How do I use async await in C#?";
        var results = await this.memoryService!.SearchAsync(query, limit: 3);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Expected top result: \"C# Async Programming\"");
        this.output.WriteLine(results.Count > 0 && results[0].Title == "C# Async Programming" ? "PASS" : "FAIL");

        Assert.NotEmpty(results);
        Assert.Equal("C# Async Programming", results[0].Title);
    }

    [Fact]
    public async Task Search_ContainerDeployment_FindsDockerMemory()
    {
        this.SkipIfNoOllama();

        string query = "container deployment and packaging applications";
        var results = await this.memoryService!.SearchAsync(query, limit: 3);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Expected top result: \"Docker Containers Guide\"");
        this.output.WriteLine(results.Count > 0 && results[0].Title == "Docker Containers Guide" ? "PASS" : "FAIL");

        Assert.NotEmpty(results);
        Assert.Equal("Docker Containers Guide", results[0].Title);
    }

    [Fact]
    public async Task Search_DatabaseQuery_FindsPostgreSQLMemory()
    {
        this.SkipIfNoOllama();

        string query = "relational database with JSON support and full text search";
        var results = await this.memoryService!.SearchAsync(query, limit: 3);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Expected top result: \"PostgreSQL Database Notes\"");
        this.output.WriteLine(results.Count > 0 && results[0].Title == "PostgreSQL Database Notes" ? "PASS" : "FAIL");

        Assert.NotEmpty(results);
        Assert.Equal("PostgreSQL Database Notes", results[0].Title);
    }

    [Fact]
    public async Task Search_AIModels_FindsMachineLearningMemory()
    {
        this.SkipIfNoOllama();

        string query = "training neural networks and AI models";
        var results = await this.memoryService!.SearchAsync(query, limit: 3);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Expected top result: \"Machine Learning Fundamentals\"");
        this.output.WriteLine(results.Count > 0 && results[0].Title == "Machine Learning Fundamentals" ? "PASS" : "FAIL");

        Assert.NotEmpty(results);
        Assert.Equal("Machine Learning Fundamentals", results[0].Title);
    }

    [Fact]
    public async Task Search_VersionControl_FindsGitMemory()
    {
        this.SkipIfNoOllama();

        string query = "version control branching and merging code";
        var results = await this.memoryService!.SearchAsync(query, limit: 3);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Expected top result: \"Git Branching Strategies\"");
        this.output.WriteLine(results.Count > 0 && results[0].Title == "Git Branching Strategies" ? "PASS" : "FAIL");

        Assert.NotEmpty(results);
        Assert.Equal("Git Branching Strategies", results[0].Title);
    }

    [Fact]
    public async Task Search_WithTagFilter_OnlyReturnsMatchingTags()
    {
        this.SkipIfNoOllama();

        string query = "best practices for software development";
        this.output.WriteLine($"Query: \"{query}\"");
        this.output.WriteLine($"Tag filter: [\"devops\"]");

        // Search with a broad query but filter to only "devops" tagged memories
        var results = await this.memoryService!.SearchAsync(query, limit: 10, tags: ["devops"]);

        this.output.WriteLine($"Results: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            bool hasTag = r.Tags.Contains("devops");
            this.output.WriteLine($"  [{i + 1}] \"{r.Title}\" (score: {r.Score:F4}, tags: [{string.Join(", ", r.Tags)}]) {(hasTag ? "OK" : "MISSING TAG")}");
        }

        Assert.NotEmpty(results);
        // All results should have the "devops" tag
        Assert.All(results, r => Assert.Contains("devops", r.Tags));
    }

    [Fact]
    public async Task Search_WithMinScore_FiltersLowSimilarity()
    {
        this.SkipIfNoOllama();

        string query = "How do I write asynchronous code in C# using async and await?";
        float minScore = 0.3f;
        this.output.WriteLine($"Query: \"{query}\"");
        this.output.WriteLine($"Min score threshold: {minScore:F2}");

        // Natural-language query with moderate threshold (0.3 is realistic for small models)
        var results = await this.memoryService!.SearchAsync(query, limit: 10, minScore: minScore);

        this.output.WriteLine($"Results above threshold: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            this.output.WriteLine($"  [{i + 1}] \"{r.Title}\" (score: {r.Score:F4}) {(r.Score >= minScore ? "ABOVE" : "BELOW")} threshold");
        }

        // Also show what would have been returned without threshold
        var allResults = await this.memoryService!.SearchAsync(query, limit: 10);
        int filteredOut = allResults.Count - results.Count;
        if (filteredOut > 0)
        {
            this.output.WriteLine($"Filtered out {filteredOut} results below {minScore:F2}:");
            foreach (var r in allResults.Where(r => r.Score < minScore))
            {
                this.output.WriteLine($"  [-] \"{r.Title}\" (score: {r.Score:F4})");
            }
        }

        // Should get at least the C# memory, but possibly not all 5
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Score >= minScore,
            $"Result '{r.Title}' has score {r.Score:F4} below minimum {minScore}"));
    }

    [Fact]
    public async Task Search_ReturnsResultsInDescendingScoreOrder()
    {
        this.SkipIfNoOllama();

        string query = "programming concepts";
        var results = await this.memoryService!.SearchAsync(query, limit: 5);

        this.LogSearchResults(query, results);

        bool inOrder = true;
        for (int i = 1; i < results.Count; i++)
        {
            if (results[i - 1].Score < results[i].Score)
            {
                inOrder = false;
                this.output.WriteLine($"Order violation at position {i}: {results[i - 1].Score:F4} < {results[i].Score:F4}");
            }
        }
        this.output.WriteLine(inOrder ? "PASS: Results are in descending score order" : "FAIL: Results are NOT in descending score order");

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
        this.SkipIfNoOllama();

        string query = "software development";
        int limit = 2;
        var results = await this.memoryService!.SearchAsync(query, limit: limit);

        this.LogSearchResults(query, results);
        this.output.WriteLine($"Limit: {limit}, Returned: {results.Count}");
        this.output.WriteLine(results.Count <= limit ? "PASS: Limit respected" : "FAIL: Too many results returned");

        Assert.True(results.Count <= limit, $"Expected at most {limit} results, got {results.Count}");
    }

    [Fact]
    public async Task Search_PrecisionAtK_TopResultIsRelevant()
    {
        this.SkipIfNoOllama();

        // Run multiple queries and check precision@1
        var queries = new (string Query, string ExpectedTitle)[]
        {
            ("asynchronous programming patterns", "C# Async Programming"),
            ("containerization technology", "Docker Containers Guide"),
            ("SQL database indexing", "PostgreSQL Database Notes"),
            ("supervised learning algorithms", "Machine Learning Fundamentals"),
            ("git rebase vs merge", "Git Branching Strategies"),
        };

        this.output.WriteLine("Precision@1 evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int correctAtK1 = 0;
        foreach (var (query, expectedTitle) in queries)
        {
            var results = await this.memoryService!.SearchAsync(query, limit: 1);
            string actualTitle = results.Count > 0 ? results[0].Title ?? "(no title)" : "(no results)";
            float score = results.Count > 0 ? results[0].Score : 0f;
            bool correct = results.Count > 0 && results[0].Title == expectedTitle;
            if (correct)
            {
                correctAtK1++;
            }

            this.output.WriteLine($"  Query: \"{query}\"");
            this.output.WriteLine($"    Expected: \"{expectedTitle}\"");
            this.output.WriteLine($"    Actual:   \"{actualTitle}\" (score: {score:F4}) {(correct ? "CORRECT" : "WRONG")}");
        }

        float precision = (float)correctAtK1 / queries.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Precision@1: {precision:F2} ({correctAtK1}/{queries.Length}) — threshold: >= 0.80");
        this.output.WriteLine(precision >= 0.8f ? "PASS" : "FAIL");

        Assert.True(precision >= 0.8f,
            $"Precision@1 = {precision:F2} ({correctAtK1}/{queries.Length}). Expected >= 0.80");
    }

    [Fact]
    public async Task Search_RecallAtK3_AllRelevantInTopK()
    {
        this.SkipIfNoOllama();

        // For each query, the relevant memory should appear in top 3
        var queries = new (string Query, string ExpectedTitle)[]
        {
            ("asynchronous programming patterns", "C# Async Programming"),
            ("containerization technology", "Docker Containers Guide"),
            ("SQL database indexing", "PostgreSQL Database Notes"),
            ("supervised learning algorithms", "Machine Learning Fundamentals"),
            ("git rebase vs merge", "Git Branching Strategies"),
        };

        this.output.WriteLine("Recall@3 evaluation:");
        this.output.WriteLine("─────────────────────────────────────────────────────────────");

        int foundInTopK = 0;
        foreach (var (query, expectedTitle) in queries)
        {
            var results = await this.memoryService!.SearchAsync(query, limit: 3);
            bool found = results.Any(r => r.Title == expectedTitle);
            if (found)
            {
                foundInTopK++;
            }

            this.output.WriteLine($"  Query: \"{query}\"");
            this.output.WriteLine($"    Looking for: \"{expectedTitle}\"");
            for (int i = 0; i < results.Count; i++)
            {
                string marker = results[i].Title == expectedTitle ? " <-- TARGET" : "";
                this.output.WriteLine($"    [{i + 1}] \"{results[i].Title}\" (score: {results[i].Score:F4}){marker}");
            }
            this.output.WriteLine($"    {(found ? "FOUND in top 3" : "NOT FOUND in top 3")}");
        }

        float recall = (float)foundInTopK / queries.Length;
        this.output.WriteLine("─────────────────────────────────────────────────────────────");
        this.output.WriteLine($"Recall@3: {recall:F2} ({foundInTopK}/{queries.Length}) — threshold: >= 0.80");
        this.output.WriteLine(recall >= 0.8f ? "PASS" : "FAIL");

        Assert.True(recall >= 0.8f,
            $"Recall@3 = {recall:F2} ({foundInTopK}/{queries.Length}). Expected >= 0.80");
    }

    public void Dispose()
    {
        this.store?.Dispose();
        this.embeddingService?.Dispose();

        try
        {
            if (Directory.Exists(this.tempDir))
            {
                Directory.Delete(this.tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
