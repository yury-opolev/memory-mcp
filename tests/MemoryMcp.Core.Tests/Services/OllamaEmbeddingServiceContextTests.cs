using System.Net;
using System.Text;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

/// <summary>num_ctx is sent explicitly, inputs are never truncated, and queries get the instruction prefix.</summary>
public class OllamaEmbeddingServiceContextTests : IDisposable
{
    private const string OneVector = """{"model":"m","embeddings":[[0.1,0.2,0.3,0.4]],"prompt_eval_count":3}""";

    private readonly List<OllamaEmbeddingService> services = [];

    public void Dispose()
    {
        foreach (var service in this.services)
        {
            service.Dispose();
        }
    }

    private OllamaEmbeddingService Build(HttpMessageHandler handler, int numCtx = 2048)
    {
        var options = new MemoryMcpOptions
        {
            Ollama = new OllamaOptions { Endpoint = "http://embeddings:11434", Model = "m", NumCtx = numCtx },
        };
        var service = new OllamaEmbeddingService(new TestOptionsMonitor(options), NullLogger<OllamaEmbeddingService>.Instance, handler);
        this.services.Add(service);
        return service;
    }

    [Fact]
    public async Task EmbedBatchStrictAsync_SendsNumCtxAndTruncateFalse()
    {
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, OneVector);
        var service = this.Build(handler, numCtx: 1536);

        var vectors = await service.EmbedBatchStrictAsync(["hello"], TestContext.Current.CancellationToken);

        Assert.Single(vectors);
        Assert.Contains("\"truncate\":false", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"num_ctx\":1536", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedBatchStrictAsync_InputOverContext_ThrowsInsteadOfTruncating()
    {
        var handler = new BodyCapturingHandler(HttpStatusCode.BadRequest, """{"error":"the input length exceeds the context length"}""");
        var service = this.Build(handler);

        await Assert.ThrowsAsync<EmbeddingInputTooLongException>(() => service.EmbedBatchStrictAsync(["very long"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedAsync_DefaultPath_DoesNotDisableTruncation()
    {
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, OneVector);
        var service = this.Build(handler);

        await service.EmbedAsync("hello", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"truncate\":false", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedBatchAsync_LongMemoryContent_SucceedsWhereStrictFails()
    {
        var handler = new TruncationAwareHandler(maxChars: 100);
        var service = this.Build(handler);
        var longContent = new string('x', 5000);

        var vectors = await service.EmbedBatchAsync([longContent], TestContext.Current.CancellationToken);

        Assert.Single(vectors);
        await Assert.ThrowsAsync<EmbeddingInputTooLongException>(
            () => service.EmbedBatchStrictAsync([longContent], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedBatchAsync_WrongVectorCount_Throws()
    {
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, OneVector);
        var service = this.Build(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EmbedBatchAsync(["a", "b"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmbedQueryAsync_PrefixesQwenInstruction()
    {
        var handler = new BodyCapturingHandler(HttpStatusCode.OK, OneVector);
        IEmbeddingService service = this.Build(handler);

        await service.EmbedQueryAsync("train times", "Retrieve passages", TestContext.Current.CancellationToken);

        Assert.Contains("Instruct: Retrieve passages\\nQuery:train times", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_NoInstruction_ReturnsQueryUnchanged()
    {
        Assert.Equal("q", EmbeddingInstruction.Format("q", null));
        Assert.Equal("q", EmbeddingInstruction.Format("q", "  "));
        Assert.Equal("Instruct: task\nQuery:q", EmbeddingInstruction.Format("q", " task "));
    }
}

/// <summary>Mimics Ollama: an over-long input is cut unless the request says truncate=false, in which case it is rejected.</summary>
internal sealed class TruncationAwareHandler(int maxChars) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var strict = body.Contains("\"truncate\":false", StringComparison.Ordinal);
        var tooLong = body.Length > maxChars;
        var (status, json) = strict && tooLong
            ? (HttpStatusCode.BadRequest, """{"error":"the input length exceeds the context length"}""")
            : (HttpStatusCode.OK, """{"model":"m","embeddings":[[0.1,0.2,0.3,0.4]],"prompt_eval_count":3}""");
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Returns a canned response and records the last request body.</summary>
internal sealed class BodyCapturingHandler(HttpStatusCode status, string responseJson) : HttpMessageHandler
{
    public string LastBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
    }
}
