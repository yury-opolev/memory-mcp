using MemoryMcp.Core.Services;
using Xunit;

namespace MemoryMcp.Core.Tests.Services;

public class EmbeddingServiceDefaultsTests
{
    [Fact]
    public async Task EmbedBatchStrictAsync_ImplementationWithoutStrictSupport_ThrowsNotSupported()
    {
        IEmbeddingService service = new TruncatingEmbedder();

        await Assert.ThrowsAsync<NotSupportedException>(() => service.EmbedBatchStrictAsync(["text"]));
    }

    private sealed class TruncatingEmbedder : IEmbeddingService
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(new float[] { 1 });

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1 }).ToList());
    }
}
