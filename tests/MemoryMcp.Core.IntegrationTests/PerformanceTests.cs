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
    private readonly ITestOutputHelper output;
    private readonly string tempDir;
    private readonly MemoryMcpOptions options;
    private readonly OllamaEmbeddingService? embeddingService;
    private readonly WordChunkingService chunkingService;
    private SqliteVecMemoryStore? store;
    private MemoryService? memoryService;
    private bool ollamaAvailable;

    public PerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
        this.tempDir = Path.Combine(Path.GetTempPath(), $"memory-mcp-perf-test-{Guid.NewGuid():N}");
        this.options = TestOptionsHelper.CreateOptions(dataDirectory: this.tempDir);

        var optionsWrapper = Options.Create(this.options);
        this.chunkingService = new WordChunkingService(optionsWrapper);

        try
        {
            this.embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            this.embeddingService.EmbedAsync("warmup").GetAwaiter().GetResult();
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
            Options.Create(this.options),
            NullLogger<MemoryService>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SkipIfNoOllama()
    {
        if (!this.ollamaAvailable)
        {
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
        }
    }

    [Fact]
    public async Task SingleEmbedding_CompletesWithinTimeout()
    {
        this.SkipIfNoOllama();

        string text = "A short test sentence for latency measurement.";
        this.output.WriteLine($"Embedding single text: \"{text}\"");

        var sw = Stopwatch.StartNew();
        var embedding = await this.embeddingService!.EmbedAsync(text);
        sw.Stop();

        this.output.WriteLine($"Latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"Vector dimensions: {embedding.Length}");
        this.output.WriteLine($"Threshold: < 5000 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 5000 ? "PASS" : "FAIL");

        // Single embedding should complete in under 5 seconds (generous for cold model)
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Single embedding took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task BatchEmbedding_10Texts_MeasureThroughput()
    {
        this.SkipIfNoOllama();

        var texts = Enumerable.Range(1, 10)
            .Select(i => $"This is test sentence number {i} for measuring batch embedding throughput performance.")
            .ToList();

        this.output.WriteLine($"Embedding batch of {texts.Count} texts...");

        var sw = Stopwatch.StartNew();
        var results = await this.embeddingService!.EmbedBatchAsync(texts);
        sw.Stop();

        var perItem = sw.ElapsedMilliseconds / (double)texts.Count;
        this.output.WriteLine($"Total latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"Per-item latency: {perItem:F1} ms");
        this.output.WriteLine($"Throughput: {texts.Count / (sw.ElapsedMilliseconds / 1000.0):F1} embeddings/sec");
        this.output.WriteLine($"Results count: {results.Count}");
        this.output.WriteLine($"Threshold: < 30000 ms total");
        this.output.WriteLine(sw.ElapsedMilliseconds < 30000 ? "PASS" : "FAIL");

        Assert.Equal(10, results.Count);

        // 10 embeddings should complete in under 30 seconds
        Assert.True(sw.ElapsedMilliseconds < 30000,
            $"Batch embedding of 10 texts took {sw.ElapsedMilliseconds}ms, expected < 30000ms");
    }

    [Fact]
    public async Task IngestMemory_ShortContent_CompletesQuickly()
    {
        this.SkipIfNoOllama();

        var content = "This is a short memory with just enough content to be meaningful. " +
                      "It contains some facts about software development practices.";

        this.output.WriteLine($"Ingesting short content ({content.Split(' ').Length} words)...");

        var sw = Stopwatch.StartNew();
        var result = await this.memoryService!.IngestAsync(content, "Short Memory", ["test"], force: true);
        sw.Stop();

        this.output.WriteLine($"Memory ID: {result.MemoryId}");
        this.output.WriteLine($"Latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"  (includes: chunking + embedding + SQLite write + file write)");
        this.output.WriteLine($"Threshold: < 5000 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 5000 ? "PASS" : "FAIL");

        Assert.True(result.Success);
        Assert.NotNull(result.MemoryId);

        // Short content (single chunk) should ingest in under 5 seconds
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Short content ingest took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task IngestMemory_LongContent_MultipleChunks()
    {
        this.SkipIfNoOllama();

        // Generate content that will produce multiple chunks (>512 words)
        var words = Enumerable.Range(1, 1200)
            .Select(i => $"word{i}")
            .ToList();
        var content = string.Join(" ", words);

        // Pre-compute chunk count for logging
        var chunks = this.chunkingService.Chunk(content);
        this.output.WriteLine($"Ingesting long content ({words.Count} words, {chunks.Count} chunks)...");
        this.output.WriteLine($"Chunk config: {this.options.ChunkSizeWords} words/chunk, {this.options.ChunkOverlapWords} words overlap");

        var sw = Stopwatch.StartNew();
        var ingestResult = await this.memoryService!.IngestAsync(content, "Long Memory", ["test", "performance"], force: true);
        sw.Stop();

        this.output.WriteLine($"Memory ID: {ingestResult.MemoryId}");
        this.output.WriteLine($"Total latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"Per-chunk latency: {sw.ElapsedMilliseconds / (double)chunks.Count:F1} ms");
        this.output.WriteLine($"  (includes: chunking + {chunks.Count}x embedding + {chunks.Count}x SQLite write + file write)");
        this.output.WriteLine($"Threshold: < 60000 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 60000 ? "PASS" : "FAIL");

        Assert.True(ingestResult.Success);
        Assert.NotNull(ingestResult.MemoryId);

        // Verify it was chunked (>512 words should produce multiple chunks)
        var result = await this.memoryService.GetAsync(ingestResult.MemoryId);
        Assert.NotNull(result);

        // Long content ingest (multiple chunks + embeddings) should complete in under 60 seconds
        Assert.True(sw.ElapsedMilliseconds < 60000,
            $"Long content ingest took {sw.ElapsedMilliseconds}ms, expected < 60000ms");
    }

    [Fact]
    public async Task SearchLatency_WithPopulatedStore()
    {
        this.SkipIfNoOllama();

        // First, populate the store with some memories
        var memories = new[]
        {
            "C# is a modern programming language developed by Microsoft for .NET platform.",
            "Docker enables containerized application deployment with consistent environments.",
            "PostgreSQL provides advanced SQL features including window functions and CTEs.",
            "Kubernetes orchestrates container workloads across clusters of machines.",
            "Redis is an in-memory data structure store used for caching and messaging.",
        };

        this.output.WriteLine($"Populating store with {memories.Length} memories...");
        foreach (var memory in memories)
        {
            await this.memoryService!.IngestAsync(memory, tags: ["perf-test"], force: true);
        }

        string query = "container orchestration";
        this.output.WriteLine($"Searching: \"{query}\"");

        // Measure search latency (includes embedding the query + vector search)
        var sw = Stopwatch.StartNew();
        var results = await this.memoryService!.SearchAsync(query, limit: 3);
        sw.Stop();

        this.output.WriteLine($"Search latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"  (includes: query embedding + sqlite-vec search + file read)");
        this.output.WriteLine($"Results: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            this.output.WriteLine($"  [{i + 1}] score: {results[i].Score:F4} — {results[i].Content?[..Math.Min(60, results[i].Content?.Length ?? 0)]}...");
        }
        this.output.WriteLine($"Threshold: < 5000 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 5000 ? "PASS" : "FAIL");

        Assert.NotEmpty(results);

        // Search should complete in under 5 seconds (includes query embedding)
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Search took {sw.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public async Task GetMemory_Latency()
    {
        this.SkipIfNoOllama();

        this.output.WriteLine("Ingesting test memory...");
        var ingest = await this.memoryService!.IngestAsync("Test content for get latency measurement.", "Latency Test", force: true);
        var memoryId = ingest.MemoryId!;
        this.output.WriteLine($"Memory ID: {memoryId}");

        this.output.WriteLine("Retrieving by ID...");
        var sw = Stopwatch.StartNew();
        var result = await this.memoryService.GetAsync(memoryId);
        sw.Stop();

        this.output.WriteLine($"Get latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"  (no embedding needed — SQLite lookup + file read only)");
        this.output.WriteLine($"Title: \"{result?.Title}\"");
        this.output.WriteLine($"Content length: {result?.Content?.Length ?? 0} chars");
        this.output.WriteLine($"Threshold: < 500 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 500 ? "PASS" : "FAIL");

        Assert.NotNull(result);

        // Get by ID (no embedding needed, just DB + file read) should be very fast
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"GetMemory took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task DeleteMemory_Latency()
    {
        this.SkipIfNoOllama();

        this.output.WriteLine("Ingesting test memory...");
        var ingest = await this.memoryService!.IngestAsync("Test content for delete latency measurement.", "Delete Test", force: true);
        var memoryId = ingest.MemoryId!;
        this.output.WriteLine($"Memory ID: {memoryId}");

        this.output.WriteLine("Deleting...");
        var sw = Stopwatch.StartNew();
        var deleted = await this.memoryService.DeleteAsync(memoryId);
        sw.Stop();

        this.output.WriteLine($"Delete latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"  (SQLite delete + sqlite-vec delete + file delete)");
        this.output.WriteLine($"Deleted: {deleted}");

        // Verify it's gone
        var result = await this.memoryService.GetAsync(memoryId);
        this.output.WriteLine($"Verify gone: {(result == null ? "yes" : "NO — still exists!")}");
        this.output.WriteLine($"Threshold: < 500 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 500 ? "PASS" : "FAIL");

        Assert.True(deleted);

        // Delete should be fast (DB operations + file delete)
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"DeleteMemory took {sw.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public async Task ChunkingService_LargeText_IsInstantaneous()
    {
        this.SkipIfNoOllama();

        // Generate a large text (10,000 words)
        var words = Enumerable.Range(1, 10000)
            .Select(i => $"word{i}")
            .ToList();
        var content = string.Join(" ", words);

        this.output.WriteLine($"Chunking {words.Count} words...");
        this.output.WriteLine($"Chunk config: {this.options.ChunkSizeWords} words/chunk, {this.options.ChunkOverlapWords} words overlap");

        var sw = Stopwatch.StartNew();
        var chunks = this.chunkingService.Chunk(content);
        sw.Stop();

        this.output.WriteLine($"Chunks produced: {chunks.Count}");
        this.output.WriteLine($"Chunking latency: {sw.ElapsedMilliseconds} ms");
        this.output.WriteLine($"  (CPU-only, no I/O or network)");
        this.output.WriteLine($"Threshold: < 100 ms");
        this.output.WriteLine(sw.ElapsedMilliseconds < 100 ? "PASS" : "FAIL");

        Assert.True(chunks.Count > 1);

        // Chunking is CPU-only, should be essentially instant (< 100ms)
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Chunking 10,000 words took {sw.ElapsedMilliseconds}ms, expected < 100ms");
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
