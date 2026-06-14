using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

/// <summary>
/// Unit tests for OllamaEmbeddingService Bearer auth and live IOptionsMonitor rebuild.
/// Uses a stub HttpMessageHandler (CapturingHandler) to intercept outgoing requests
/// and a hand-rolled IOptionsMonitor test double to drive OnChange callbacks.
/// </summary>
public class OllamaEmbeddingServiceAuthTests : IDisposable
{
    // Minimal valid /api/embed JSON body that OllamaSharp can deserialise.
    // We return a 4-dim vector so the test stays trivially small.
    private const string EmbedResponseJson = """
        {"model":"test-model","embeddings":[[0.1,0.2,0.3,0.4]],"total_duration":1,"load_duration":1,"prompt_eval_count":1}
        """;

    private readonly List<OllamaEmbeddingService> createdServices = [];

    public void Dispose()
    {
        foreach (var svc in this.createdServices)
        {
            svc.Dispose();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TestOptionsMonitor BuildMonitor(MemoryMcpOptions opts) => new(opts);

    private OllamaEmbeddingService BuildService(TestOptionsMonitor monitor, CapturingHandler handler)
    {
        var svc = new OllamaEmbeddingService(monitor, NullLogger<OllamaEmbeddingService>.Instance, handler);
        this.createdServices.Add(svc);
        return svc;
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmbedAsync_WhenApiKeySet_SendsBearerAuthorizationHeader()
    {
        var handler = new CapturingHandler(EmbedResponseJson);
        var opts = new MemoryMcpOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://localhost:11434",
                Model = "test-model",
                ApiKey = "test-key",
            }
        };
        var monitor = BuildMonitor(opts);
        var svc = this.BuildService(monitor, handler);

        await svc.EmbedAsync("hello world");

        Assert.NotNull(handler.LastRequest);
        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("test-key", auth.Parameter);
    }

    [Fact]
    public async Task EmbedAsync_WhenApiKeyNull_SendsNoAuthorizationHeader()
    {
        var handler = new CapturingHandler(EmbedResponseJson);
        var opts = new MemoryMcpOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://localhost:11434",
                Model = "test-model",
                ApiKey = null,
            }
        };
        var monitor = BuildMonitor(opts);
        var svc = this.BuildService(monitor, handler);

        await svc.EmbedAsync("hello world");

        Assert.NotNull(handler.LastRequest);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task EmbedAsync_AfterOnChangeWithNewEndpoint_TargetsNewBaseAddress()
    {
        var handler = new CapturingHandler(EmbedResponseJson);
        var opts = new MemoryMcpOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://original-host:11434",
                Model = "test-model",
                ApiKey = null,
            }
        };
        var monitor = BuildMonitor(opts);
        var svc = this.BuildService(monitor, handler);

        // First call — hits original endpoint.
        await svc.EmbedAsync("first");
        var firstUri = handler.LastRequest!.RequestUri!;
        Assert.StartsWith("http://original-host:11434", firstUri.ToString());

        // Simulate config change — new endpoint.
        var newOpts = new MemoryMcpOptions
        {
            Ollama = new OllamaOptions
            {
                Endpoint = "http://new-host:11434",
                Model = "test-model",
                ApiKey = null,
            }
        };
        monitor.TriggerChange(newOpts);

        // Second call — must hit new endpoint.
        await svc.EmbedAsync("second");
        var secondUri = handler.LastRequest!.RequestUri!;
        Assert.StartsWith("http://new-host:11434", secondUri.ToString());
    }
}

// ── test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// Records the last outgoing HttpRequestMessage and returns a canned JSON response.
/// </summary>
internal sealed class CapturingHandler(string responseJson) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        this.LastRequest = request;
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

/// <summary>
/// Minimal IOptionsMonitor that lets tests push new values via TriggerChange.
/// </summary>
internal sealed class TestOptionsMonitor(MemoryMcpOptions initial) : IOptionsMonitor<MemoryMcpOptions>
{
    private readonly List<Action<MemoryMcpOptions, string?>> listeners = [];
    private MemoryMcpOptions current = initial;

    public MemoryMcpOptions CurrentValue => this.current;

    public MemoryMcpOptions Get(string? name) => this.current;

    public IDisposable? OnChange(Action<MemoryMcpOptions, string?> listener)
    {
        this.listeners.Add(listener);
        return null;
    }

    public void TriggerChange(MemoryMcpOptions newValue)
    {
        this.current = newValue;
        foreach (var listener in this.listeners)
        {
            listener(newValue, null);
        }
    }
}
