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
    private readonly ITestOutputHelper output;
    private readonly OllamaEmbeddingService? embeddingService;
    private readonly bool ollamaAvailable;
    private readonly MemoryMcpOptions options;

    public EmbeddingQualityTests(ITestOutputHelper output)
    {
        this.output = output;
        this.options = TestOptionsHelper.CreateOptions();
        var optionsWrapper = Options.Create(this.options);

        try
        {
            this.embeddingService = new OllamaEmbeddingService(optionsWrapper, NullLogger<OllamaEmbeddingService>.Instance);
            // Quick connectivity check
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

        string textA = "The cat sat on the mat.";
        string textB = "A cat was sitting on a mat.";
        this.output.WriteLine($"Text A: \"{textA}\"");
        this.output.WriteLine($"Text B: \"{textB}\"");

        var embeddings = await this.embeddingService!.EmbedBatchAsync([textA, textB]);
        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        this.output.WriteLine($"Cosine similarity: {similarity:F4}");
        this.output.WriteLine($"Threshold: > 0.85");
        this.output.WriteLine(similarity > 0.85f ? "PASS: Similar sentences have high similarity" : "FAIL: Similarity too low");

        // Similar sentences should have very high similarity (> 0.85)
        Assert.True(similarity > 0.85f, $"Expected similarity > 0.85, got {similarity:F4}");
    }

    [Fact]
    public async Task DissimilarTexts_HaveLowCosineSimilarity()
    {
        this.SkipIfNoOllama();

        string textA = "The stock market crashed today with major losses in technology sectors.";
        string textB = "I made a delicious chocolate cake for my daughter's birthday party.";
        this.output.WriteLine($"Text A: \"{textA}\"");
        this.output.WriteLine($"Text B: \"{textB}\"");

        var embeddings = await this.embeddingService!.EmbedBatchAsync([textA, textB]);
        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        this.output.WriteLine($"Cosine similarity: {similarity:F4}");
        this.output.WriteLine($"Threshold: < 0.5");
        this.output.WriteLine(similarity < 0.5f ? "PASS: Dissimilar sentences have low similarity" : "FAIL: Similarity too high");

        // Dissimilar sentences should have low similarity (< 0.5)
        Assert.True(similarity < 0.5f, $"Expected similarity < 0.5, got {similarity:F4}");
    }

    [Fact]
    public async Task SameMeaning_DifferentPhrasing_HighSimilarity()
    {
        this.SkipIfNoOllama();

        string textA = "How do I fix a null reference exception in C#?";
        string textB = "Resolving NullReferenceException errors in dotnet applications";
        this.output.WriteLine($"Text A: \"{textA}\"");
        this.output.WriteLine($"Text B: \"{textB}\"");

        var embeddings = await this.embeddingService!.EmbedBatchAsync([textA, textB]);
        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        this.output.WriteLine($"Cosine similarity: {similarity:F4}");
        this.output.WriteLine($"Threshold: > 0.7");
        this.output.WriteLine(similarity > 0.7f ? "PASS: Same meaning with different phrasing recognized" : "FAIL: Model did not recognize semantic equivalence");

        // Same meaning with different phrasing should be similar (> 0.7)
        Assert.True(similarity > 0.7f, $"Expected similarity > 0.7, got {similarity:F4}");
    }

    [Fact]
    public async Task SimilarTexts_MoreSimilarThan_DissimilarTexts()
    {
        this.SkipIfNoOllama();

        string textA = "Python is a popular programming language for machine learning.";
        string textB = "Machine learning frameworks in Python include TensorFlow and PyTorch.";
        string textC = "The recipe calls for two cups of flour and one egg.";
        this.output.WriteLine($"Text A: \"{textA}\"");
        this.output.WriteLine($"Text B: \"{textB}\" (similar to A)");
        this.output.WriteLine($"Text C: \"{textC}\" (dissimilar to A)");

        var embeddings = await this.embeddingService!.EmbedBatchAsync([textA, textB, textC]);

        var simAB = CosineSimilarity(embeddings[0], embeddings[1]);
        var simAC = CosineSimilarity(embeddings[0], embeddings[2]);

        this.output.WriteLine($"Similarity A-B (related): {simAB:F4}");
        this.output.WriteLine($"Similarity A-C (unrelated): {simAC:F4}");
        this.output.WriteLine($"Gap: {simAB - simAC:F4}");
        this.output.WriteLine(simAB > simAC ? "PASS: Related texts are more similar than unrelated texts" : "FAIL: Model ranked unrelated text higher");

        Assert.True(simAB > simAC,
            $"Similar texts (A-B: {simAB:F4}) should be more similar than dissimilar texts (A-C: {simAC:F4})");
    }

    [Fact]
    public async Task IdenticalTexts_HaveMaxSimilarity()
    {
        this.SkipIfNoOllama();

        var text = "The quick brown fox jumps over the lazy dog.";
        this.output.WriteLine($"Text: \"{text}\"");
        this.output.WriteLine("Embedding the same text twice...");

        var embeddings = await this.embeddingService!.EmbedBatchAsync([text, text]);
        var similarity = CosineSimilarity(embeddings[0], embeddings[1]);

        this.output.WriteLine($"Cosine similarity: {similarity:F4}");
        this.output.WriteLine($"Threshold: > 0.99");
        this.output.WriteLine(similarity > 0.99f ? "PASS: Identical texts produce identical embeddings" : "FAIL: Embeddings are not deterministic");

        // Identical texts should have near-perfect similarity (> 0.99)
        Assert.True(similarity > 0.99f, $"Expected similarity > 0.99, got {similarity:F4}");
    }

    [Fact]
    public async Task EmbeddingVector_HasExpectedDimensions()
    {
        this.SkipIfNoOllama();

        var embedding = await this.embeddingService!.EmbedAsync("Test text");

        this.output.WriteLine($"Expected dimensions: {this.options.Ollama.Dimensions}");
        this.output.WriteLine($"Actual dimensions: {embedding.Length}");

        // Default model produces 1024-dimensional vectors
        Assert.Equal(this.options.Ollama.Dimensions, embedding.Length);
    }

    [Fact]
    public async Task EmbeddingVector_IsNormalized()
    {
        this.SkipIfNoOllama();

        string text = "Test text for normalization check";
        this.output.WriteLine($"Text: \"{text}\"");

        var embedding = await this.embeddingService!.EmbedAsync(text);

        // Compute L2 norm
        float norm = MathF.Sqrt(embedding.Sum(x => x * x));

        this.output.WriteLine($"L2 norm: {norm:F6}");
        this.output.WriteLine($"Expected: ~1.0 (tolerance: 0.05)");
        this.output.WriteLine($"Deviation from 1.0: {MathF.Abs(norm - 1.0f):F6}");
        this.output.WriteLine(MathF.Abs(norm - 1.0f) < 0.05f ? "PASS: Vector is normalized" : "FAIL: Vector is not normalized");

        // Normalized vectors should have L2 norm close to 1.0
        Assert.True(MathF.Abs(norm - 1.0f) < 0.05f,
            $"Expected normalized vector (L2 norm ~1.0), got {norm:F4}");
    }

    [Fact]
    public async Task BatchEmbedding_ProducesConsistentResults()
    {
        this.SkipIfNoOllama();

        var texts = new[] { "First sentence.", "Second sentence.", "Third sentence." };
        this.output.WriteLine("Comparing batch embedding vs. individual embedding...");
        this.output.WriteLine($"Texts: [{string.Join(", ", texts.Select(t => $"\"{t}\""))}]");

        // Embed as batch
        var batchResults = await this.embeddingService!.EmbedBatchAsync(texts);

        // Embed individually
        var individual1 = await this.embeddingService.EmbedAsync(texts[0]);
        var individual2 = await this.embeddingService.EmbedAsync(texts[1]);
        var individual3 = await this.embeddingService.EmbedAsync(texts[2]);

        var sim1 = CosineSimilarity(batchResults[0], individual1);
        var sim2 = CosineSimilarity(batchResults[1], individual2);
        var sim3 = CosineSimilarity(batchResults[2], individual3);

        this.output.WriteLine($"Text 1 batch vs. individual similarity: {sim1:F6}");
        this.output.WriteLine($"Text 2 batch vs. individual similarity: {sim2:F6}");
        this.output.WriteLine($"Text 3 batch vs. individual similarity: {sim3:F6}");
        this.output.WriteLine($"Threshold: > 0.99 for all");

        // Batch and individual results should be very similar (> 0.99)
        Assert.True(sim1 > 0.99f, $"Text 1: batch vs individual similarity {sim1:F4} < 0.99");
        Assert.True(sim2 > 0.99f, $"Text 2: batch vs individual similarity {sim2:F4} < 0.99");
        Assert.True(sim3 > 0.99f, $"Text 3: batch vs individual similarity {sim3:F4} < 0.99");
    }

    public void Dispose()
    {
        this.embeddingService?.Dispose();
    }
}
