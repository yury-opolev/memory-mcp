using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using MemoryMcp.Core.Storage;
using MemoryMcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Hosting;

public sealed class StoreStartupTests
{
    [Fact]
    public async Task RunAsync_Cancelled_WhileTheStoreInitialises_EndsWithoutWaitingForIt()
    {
        var store = Substitute.For<IMemoryStore>();
        var initialising = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.InitializeAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            initialising.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
        });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new MemoryMcpOptions()))
            .AddSingleton(store)
            .AddSingleton(Substitute.For<IEmbeddingService>())
            .BuildServiceProvider();
        await using var disposeServices = services;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var startup = StoreStartup.RunAsync(services, stopping.Token);
        await initialising.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
