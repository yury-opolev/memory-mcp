using System.Net.Http.Headers;
using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Exceptions;

namespace MemoryMcp.Core.Services;

/// <summary>
/// Embedding service backed by Ollama via OllamaSharp.
/// Wraps OllamaApiClient which implements IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;.
/// Supports optional Bearer-key authentication for remote/authenticated Ollama endpoints.
/// Rebuilds the underlying client live when the endpoint or API key changes via IOptionsMonitor.
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly IOptionsMonitor<MemoryMcpOptions> options;
    private readonly ILogger<OllamaEmbeddingService> logger;
    private readonly HttpMessageHandler? testHandler;
    private readonly IDisposable? changeRegistration;
    private readonly object syncLock = new();

    // Protected under syncLock.
    private string currentEndpoint;
    private string? currentApiKey;
    private string model;
    private OllamaApiClient client;
    private HttpClient? httpClient;
    private bool disposed;

    /// <summary>
    /// Production constructor — used by DI.
    /// </summary>
    public OllamaEmbeddingService(
        IOptionsMonitor<MemoryMcpOptions> options,
        ILogger<OllamaEmbeddingService> logger)
        : this(options, logger, testHandler: null)
    {
    }

    /// <summary>
    /// Internal constructor for unit testing — accepts an injectable HttpMessageHandler
    /// so tests can intercept outgoing HTTP without a live Ollama server.
    /// </summary>
    internal OllamaEmbeddingService(
        IOptionsMonitor<MemoryMcpOptions> options,
        ILogger<OllamaEmbeddingService> logger,
        HttpMessageHandler? testHandler)
    {
        this.options = options;
        this.logger = logger;
        this.testHandler = testHandler;

        var current = options.CurrentValue.Ollama;
        this.model = current.Model;
        this.currentEndpoint = current.Endpoint;
        this.currentApiKey = current.ApiKey;

        (this.client, this.httpClient) = this.BuildClient(this.currentEndpoint, this.currentApiKey);

        this.changeRegistration = options.OnChange(this.OnOptionsChanged);
    }

    // ── IOptionsMonitor callback ───────────────────────────────────────────────

    private void OnOptionsChanged(MemoryMcpOptions newOptions, string? name)
    {
        var ollama = newOptions.Ollama;
        bool endpointChanged = !string.Equals(ollama.Endpoint, this.currentEndpoint, StringComparison.Ordinal);
        bool keyChanged = !string.Equals(ollama.ApiKey, this.currentApiKey, StringComparison.Ordinal);

        if (!endpointChanged && !keyChanged)
        {
            // Model changes don't require rebuilding the client.
            lock (this.syncLock)
            {
                this.model = ollama.Model;
            }

            return;
        }

        OllamaApiClient? oldClient;
        HttpClient? oldHttp;

        lock (this.syncLock)
        {
            this.model = ollama.Model;
            this.currentEndpoint = ollama.Endpoint;
            this.currentApiKey = ollama.ApiKey;

            oldClient = this.client;
            oldHttp = this.httpClient;

            (this.client, this.httpClient) = this.BuildClient(this.currentEndpoint, this.currentApiKey);
        }

        // Dispose old resources outside the lock.
        oldClient.Dispose();
        oldHttp?.Dispose();

        this.logger.LogClientRebuilt(ollama.Endpoint, ollama.ApiKey is not null);
    }

    // ── client factory ────────────────────────────────────────────────────────

    private (OllamaApiClient client, HttpClient? httpClient) BuildClient(string endpoint, string? apiKey)
    {
        var uri = new Uri(endpoint);

        if (!string.IsNullOrEmpty(apiKey))
        {
            // Build an HttpClient with a Bearer Authorization header, then wrap it
            // in OllamaApiClient using the (HttpClient, string?) constructor.
            var http = this.testHandler is not null
                ? new HttpClient(this.testHandler, disposeHandler: false) { BaseAddress = uri }
                : new HttpClient { BaseAddress = uri };

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            var apiClient = new OllamaApiClient(http, this.model);
            return (apiClient, http);
        }
        else
        {
            // No auth — use the plain (Uri, string) constructor.
            // If a test handler was injected, still route through an HttpClient so
            // the handler can capture requests even without an API key.
            if (this.testHandler is not null)
            {
                var http = new HttpClient(this.testHandler, disposeHandler: false) { BaseAddress = uri };
                var apiClient = new OllamaApiClient(http, this.model);
                return (apiClient, http);
            }

            return (new OllamaApiClient(uri, this.model), null);
        }
    }

    // ── IEmbeddingService ─────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the Ollama server is reachable.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            OllamaApiClient current;
            lock (this.syncLock)
            {
                current = this.client;
            }

            return await current.IsRunningAsync(cancellationToken).ConfigureAwait(false);
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
            OllamaApiClient current;
            string currentModel;
            lock (this.syncLock)
            {
                current = this.client;
                currentModel = this.model;
            }

            var models = await current.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            return models.Any(m =>
                string.Equals(m.Name, currentModel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, currentModel + ":latest", StringComparison.OrdinalIgnoreCase) ||
                m.Name.StartsWith(currentModel + ":", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await this.EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
        {
            return [];
        }

        OllamaApiClient current;
        string currentModel;
        lock (this.syncLock)
        {
            current = this.client;
            currentModel = this.model;
        }

        var numCtx = this.options.CurrentValue.Ollama.NumCtx;
        try
        {
            // truncate=false: an input longer than num_ctx fails loudly instead of being embedded cut.
            var response = await current.EmbedAsync(
                new EmbedRequest
                {
                    Model = currentModel,
                    Input = textList,
                    Truncate = false,
                    Options = new RequestOptions { NumCtx = numCtx },
                },
                cancellationToken).ConfigureAwait(false);

            if (response.Embeddings is null || response.Embeddings.Count != textList.Count)
            {
                throw new InvalidOperationException(
                    $"Ollama returned {response.Embeddings?.Count ?? 0} embeddings for {textList.Count} inputs.");
            }

            return response.Embeddings;
        }
        catch (HttpRequestException ex)
        {
            this.logger.LogEmbedConnectFailed(ex, current.Uri!);
            throw new InvalidOperationException(
                $"Failed to connect to Ollama at {current.Uri}. " +
                "Please ensure Ollama is installed and running. " +
                "Install: https://ollama.com/download -- " +
                $"Then run: ollama pull {currentModel}",
                ex);
        }
        catch (OllamaException ex) when (ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddingInputTooLongException(
                $"An input exceeds the embedding context window (num_ctx={numCtx}) of model '{currentModel}'; it was not truncated.",
                ex);
        }
        catch (OllamaException ex)
        {
            this.logger.LogEmbedOllamaError(ex, currentModel);
            throw new InvalidOperationException(
                $"Ollama returned an error for model '{currentModel}': {ex.Message}. " +
                $"Ensure the model is pulled: ollama pull {currentModel}",
                ex);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            this.changeRegistration?.Dispose();

            OllamaApiClient clientToDispose;
            HttpClient? httpToDispose;
            lock (this.syncLock)
            {
                clientToDispose = this.client;
                httpToDispose = this.httpClient;
            }

            clientToDispose.Dispose();
            httpToDispose?.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

// ── source-generated log messages ─────────────────────────────────────────────

internal static partial class OllamaEmbeddingServiceLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "OllamaEmbeddingService: rebuilt client for endpoint {Endpoint} (auth={HasAuth}).")]
    internal static partial void LogClientRebuilt(
        this ILogger logger, string endpoint, bool hasAuth);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to connect to Ollama at {Uri}. Is Ollama installed and running?")]
    internal static partial void LogEmbedConnectFailed(
        this ILogger logger, Exception ex, Uri uri);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Ollama returned an error while generating embeddings with model '{Model}'.")]
    internal static partial void LogEmbedOllamaError(
        this ILogger logger, Exception ex, string model);
}
