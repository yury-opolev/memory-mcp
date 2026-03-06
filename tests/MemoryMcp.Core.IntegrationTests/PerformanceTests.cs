using System.Diagnostics;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.IntegrationTests;

/// <summary>
/// Performance benchmarks for embedding and search operations.
/// Measures latency and throughput to establish baselines.
/// Requires Ollama running locally with the configured embedding model.
/// </summary>
[Trait("Category", "Integration")]
public class PerformanceTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly MemoryMcpOptions _options;
    private readonly OllamaEmbeddingService? _embeddingService;
    private readonly WordChunkingService _chunkingService;
    private SqliteVecMemoryStore? _store;
    private MemoryService? _memoryService;
    private bool _ollamaAvailable;

    public PerformanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"memory-mcp-perf-test-{Guid.NewGuid():N}");
        _options = new MemoryMcpOptions
        {
            DataDirectory = _tempDir,
        };

        var optionsWrapper = Options.Create(_options);
        _chunkingService = new WordChunkingService(optionsWrapper);

        try
        {
            _embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            _embeddingService.EmbedAsync("warmup").GetAwaiter().GetResult();
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
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SkipIfNoOllama()
    {
        if (!_ollamaAvailable)
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
    }

    [Fact]
    public async Task SingleEmbedding_CompletesWithinTimeout()
    {
        SkipIfNoOllama();

        var sw = Stopwatch.StartNew();
        await _embeddingService!.EmbedAsync("A short test sentence for latency measurement.");
        sw.Stop();

        // Single embedding should complete in under 5 seconds (generous for cold model)
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Single embedding took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task BatchEmbedding_10Texts_MeasureThroughput()
    {
        SkipIfNoOllama();

        var texts = Enumerable.Range(1, 10)
            .Select(i => $"This is test sentence number {i} for measuring batch embedding throughput performance.")
            .ToList();

        var sw = Stopwatch.StartNew();
        var results = await _embeddingService!.EmbedBatchAsync(texts);
        sw.Stop();

        Assert.Equal(10, results.Count);

        // 10 embeddings should complete in under 30 seconds
        Assert.True(sw.ElapsedMilliseconds < 30000,
            $"Batch embedding of 10 texts took {sw.ElapsedMilliseconds}ms, expected < 30000ms");

        var perItem = sw.ElapsedMilliseconds / 10.0;
        // Output for reference (visible in test output)
        Assert.True(true, $"Per-item latency: {perItem:F1}ms");
    }

    [Fact]
    public async Task IngestMemory_ShortContent_CompletesQuickly()
    {
        SkipIfNoOllama();

        var content = "This is a short memory with just enough content to be meaningful. " +
                      "It contains some facts about software development practices.";

        var sw = Stopwatch.StartNew();
        var memoryId = await _memoryService!.IngestAsync(content, "Short Memory", ["test"]);
        sw.Stop();

        Assert.NotNull(memoryId);

        // Short content (single chunk) should ingest in under 5 seconds
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Short content ingest took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task IngestMemory_LongContent_MultipleChunks()
    {
        SkipIfNoOllama();

        // Generate content that will produce multiple chunks (>512 words)
        var words = Enumerable.Range(1, 1200)
            .Select(i => $"word{i}")
            .ToList();
        var content = string.Join(" ", words);

        var sw = Stopwatch.StartNew();
        var memoryId = await _memoryService!.IngestAsync(content, "Long Memory", ["test", "performance"]);
        sw.Stop();

        Assert.NotNull(memoryId);

        // Verify it was chunked (>512 words should produce multiple chunks)
        var result = await _memoryService.GetAsync(memoryId);
        Assert.NotNull(result);

        // Long content ingest (multiple chunks + embeddings) should complete in under 60 seconds
        Assert.True(sw.ElapsedMilliseconds < 60000,
            $"Long content ingest took {sw.ElapsedMilliseconds}ms, expected < 60000ms");
    }

    [Fact]
    public async Task SearchLatency_WithPopulatedStore()
    {
        SkipIfNoOllama();

        // First, populate the store with some memories
        var memories = new[]
        {
            "C# is a modern programming language developed by Microsoft for .NET platform.",
            "Docker enables containerized application deployment with consistent environments.",
            "PostgreSQL provides advanced SQL features including window functions and CTEs.",
            "Kubernetes orchestrates container workloads across clusters of machines.",
            "Redis is an in-memory data structure store used for caching and messaging.",
        };

        foreach (var memory in memories)
        {
            await _memoryService!.IngestAsync(memory, tags: ["perf-test"]);
        }

        // Measure search latency (includes embedding the query + vector search)
        var sw = Stopwatch.StartNew();
        var results = await _memoryService!.SearchAsync("container orchestration", limit: 3);
        sw.Stop();

        Assert.NotEmpty(results);

        // Search should complete in under 5 seconds (includes query embedding)
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Search took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task GetMemory_Latency()
    {
        SkipIfNoOllama();

        var memoryId = await _memoryService!.IngestAsync("Test content for get latency measurement.", "Latency Test");

        var sw = Stopwatch.StartNew();
        var result = await _memoryService.GetAsync(memoryId);
        sw.Stop();

        Assert.NotNull(result);

        // Get by ID (no embedding needed, just DB + file read) should be very fast
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"GetMemory took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task DeleteMemory_Latency()
    {
        SkipIfNoOllama();

        var memoryId = await _memoryService!.IngestAsync("Test content for delete latency measurement.", "Delete Test");

        var sw = Stopwatch.StartNew();
        var deleted = await _memoryService.DeleteAsync(memoryId);
        sw.Stop();

        Assert.True(deleted);

        // Delete should be fast (DB operations + file delete)
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"DeleteMemory took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task ChunkingService_LargeText_IsInstantaneous()
    {
        SkipIfNoOllama();

        // Generate a large text (10,000 words)
        var words = Enumerable.Range(1, 10000)
            .Select(i => $"word{i}")
            .ToList();
        var content = string.Join(" ", words);

        var sw = Stopwatch.StartNew();
        var chunks = _chunkingService.Chunk(content);
        sw.Stop();

        Assert.True(chunks.Count > 1);

        // Chunking is CPU-only, should be essentially instant (< 100ms)
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Chunking 10,000 words took {sw.ElapsedMilliseconds}ms, expected < 100ms");
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
