using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.IntegrationTests;

/// <summary>
/// Tests embedding quality by verifying cosine similarity relationships.
/// Requires Ollama running locally with the configured embedding model.
/// </summary>
[Trait("Category", "Integration")]
public class EmbeddingQualityTests : IDisposable
{
    private readonly OllamaEmbeddingService? embeddingService;
    private readonly bool ollamaAvailable;

    public EmbeddingQualityTests()
    {
        var options = Options.Create(new MemoryMcpOptions());

        try
        {
            this.embeddingService = new OllamaEmbeddingService(options, NullLogger<OllamaEmbeddingService>.Instance);
            // Quick connectivity check
            this.embeddingService.EmbedAsync("test").GetAwaiter().GetResult();
            this.ollamaAvailable = true;
        }
        catch
        {
            this.ollamaAvailable = false;
        }
    }

    private void SkipIfNoOllama()
    {
        if (!this.ollamaAvailable)
        {
            Assert.Skip("Ollama is not available. Install Ollama and pull the embedding model to run integration tests.");
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    [Fact]
    public async Task SimilarTexts_HaveHighCosineSimilarity()
    {
        this.SkipIfNoOllama();

        var embeddings = await this.embeddingService!.EmbedBatchAsync([
            "The cat sat on the mat.",
            "A cat was sitting on a mat.",
        ]);

        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        // Similar sentences should have very high similarity (> 0.85)
        Assert.True(similarity > 0.85f, $"Expected similarity > 0.85, got {similarity:F4}");
    }

    [Fact]
    public async Task DissimilarTexts_HaveLowCosineSimilarity()
    {
        this.SkipIfNoOllama();

        var embeddings = await this.embeddingService!.EmbedBatchAsync([
            "The stock market crashed today with major losses in technology sectors.",
            "I made a delicious chocolate cake for my daughter's birthday party.",
        ]);

        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        // Dissimilar sentences should have low similarity (< 0.5)
        Assert.True(similarity < 0.5f, $"Expected similarity < 0.5, got {similarity:F4}");
    }

    [Fact]
    public async Task SameMeaning_DifferentPhrasing_HighSimilarity()
    {
        this.SkipIfNoOllama();

        var embeddings = await this.embeddingService!.EmbedBatchAsync([
            "How do I fix a null reference exception in C#?",
            "Resolving NullReferenceException errors in dotnet applications",
        ]);

        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        // Same meaning with different phrasing should be similar (> 0.7)
        Assert.True(similarity > 0.7f, $"Expected similarity > 0.7, got {similarity:F4}");
    }

    [Fact]
    public async Task SimilarTexts_MoreSimilarThan_DissimilarTexts()
    {
        this.SkipIfNoOllama();

        var embeddings = await this.embeddingService!.EmbedBatchAsync([
            "Python is a popular programming language for machine learning.",  // A
            "Machine learning frameworks in Python include TensorFlow and PyTorch.",  // B (similar to A)
            "The recipe calls for two cups of flour and one egg.",  // C (dissimilar)
        ]);

        var simAB = CosineSimilarity(embeddings[0], embeddings[1]);
        var simAC = CosineSimilarity(embeddings[0], embeddings[2]);

        Assert.True(simAB > simAC,
            $"Similar texts (A-B: {simAB:F4}) should be more similar than dissimilar texts (A-C: {simAC:F4})");
    }

    [Fact]
    public async Task IdenticalTexts_HaveMaxSimilarity()
    {
        this.SkipIfNoOllama();

        var text = "The quick brown fox jumps over the lazy dog.";
        var embeddings = await this.embeddingService!.EmbedBatchAsync([text, text]);

        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        // Identical texts should have near-perfect similarity (> 0.99)
        Assert.True(similarity > 0.99f, $"Expected similarity > 0.99, got {similarity:F4}");
    }

    [Fact]
    public async Task EmbeddingVector_HasExpectedDimensions()
    {
        this.SkipIfNoOllama();

        var embedding = await this.embeddingService!.EmbedAsync("Test text");

        // Default model produces 1024-dimensional vectors
        Assert.Equal(1024, embedding.Length);
    }

    [Fact]
    public async Task EmbeddingVector_IsNormalized()
    {
        this.SkipIfNoOllama();

        var embedding = await this.embeddingService!.EmbedAsync("Test text for normalization check");

        // Compute L2 norm
        float norm = MathF.Sqrt(embedding.Sum(x => x * x));

        // Normalized vectors should have L2 norm close to 1.0
        Assert.True(MathF.Abs(norm - 1.0f) < 0.05f,
            $"Expected normalized vector (L2 norm ~1.0), got {norm:F4}");
    }

    [Fact]
    public async Task BatchEmbedding_ProducesConsistentResults()
    {
        this.SkipIfNoOllama();

        var texts = new[] { "First sentence.", "Second sentence.", "Third sentence." };

        // Embed as batch
        var batchResults = await this.embeddingService!.EmbedBatchAsync(texts);

        // Embed individually
        var individual1 = await this.embeddingService.EmbedAsync(texts[0]);
        var individual2 = await this.embeddingService.EmbedAsync(texts[1]);
        var individual3 = await this.embeddingService.EmbedAsync(texts[2]);

        // Batch and individual results should be very similar (> 0.99)
        Assert.True(CosineSimilarity(batchResults[0], individual1) > 0.99f);
        Assert.True(CosineSimilarity(batchResults[1], individual2) > 0.99f);
        Assert.True(CosineSimilarity(batchResults[2], individual3) > 0.99f);
    }

    public void Dispose()
    {
        this.embeddingService?.Dispose();
    }
}
