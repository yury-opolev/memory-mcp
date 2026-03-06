using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Embedding service backed by Ollama via OllamaSharp.
/// Wraps OllamaApiClient which implements IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;.
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly OllamaApiClient _client;
    private readonly string _model;
    private readonly ILogger<OllamaEmbeddingService> _logger;
    private bool _disposed;

    public OllamaEmbeddingService(IOptions<MemoryMcpOptions> options, ILogger<OllamaEmbeddingService> logger)
    {
        _logger = logger;
        _model = options.Value.Ollama.Model;

        var uri = new Uri(options.Value.Ollama.Endpoint);
        _client = new OllamaApiClient(uri, _model);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EmbedBatchAsync([text], cancellationToken);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
            return [];

        try
        {
            IEmbeddingGenerator<string, Embedding<float>> generator = _client;
            var result = await generator.GenerateAsync(textList, cancellationToken: cancellationToken);

            return result
                .Select(e => e.Vector.ToArray())
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {Endpoint}. Is Ollama installed and running?",
                _client.Uri);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama at {_client.Uri}. " +
                "Please ensure Ollama is installed and running. " +
                "Install: https://ollama.com/download — " +
                $"Then run: ollama pull {_model}",
                ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
