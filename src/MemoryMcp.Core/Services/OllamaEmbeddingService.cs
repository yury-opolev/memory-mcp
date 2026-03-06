using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models.Exceptions;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Embedding service backed by Ollama via OllamaSharp.
/// Wraps OllamaApiClient which implements IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;.
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly OllamaApiClient client;
    private readonly string model;
    private readonly ILogger<OllamaEmbeddingService> logger;
    private bool disposed;

    public OllamaEmbeddingService(IOptions<MemoryMcpOptions> options, ILogger<OllamaEmbeddingService> logger)
    {
        this.logger = logger;
        this.model = options.Value.Ollama.Model;

        var uri = new Uri(options.Value.Ollama.Endpoint);
        this.client = new OllamaApiClient(uri, this.model);
    }

    /// <summary>
    /// Checks whether the Ollama server is reachable.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.client.IsRunningAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the configured embedding model is pulled locally in Ollama.
    /// </summary>
    public async Task<bool> IsModelAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await this.client.ListLocalModelsAsync(cancellationToken);
            return models.Any(m =>
                string.Equals(m.Name, this.model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, this.model + ":latest", StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(this.model + ":", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await this.EmbedBatchAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
        {
            return [];
        }

        try
        {
            IEmbeddingGenerator<string, Embedding<float>> generator = this.client;
            var result = await generator.GenerateAsync(textList, cancellationToken: cancellationToken);

            return result
                .Select(e => e.Vector.ToArray())
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogError(ex, "Failed to connect to Ollama at {Endpoint}. Is Ollama installed and running?",
                this.client.Uri);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama at {this.client.Uri}. " +
                "Please ensure Ollama is installed and running. " +
                "Install: https://ollama.com/download -- " +
                $"Then run: ollama pull {this.model}",
                ex);
        }
        catch (OllamaException ex)
        {
            this.logger.LogError(ex, "Ollama returned an error while generating embeddings with model '{Model}'.",
                this.model);
            throw new InvalidOperationException(
                $"Ollama returned an error for model '{this.model}': {ex.Message}. " +
                $"Ensure the model is pulled: ollama pull {this.model}",
                ex);
        }
    }

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.client.Dispose();
            this.disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
