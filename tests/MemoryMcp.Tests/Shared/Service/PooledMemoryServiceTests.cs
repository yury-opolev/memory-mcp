using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using MemoryMcp.Shared.Service;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Xunit;

namespace MemoryMcp.Tests.Shared.Service;

public class PooledMemoryServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (StorePool Pool, CallMetrics Metrics, IMemoryService Memory, IHybridMemorySearch Search) NewPool()
    {
        var memory = Substitute.For<IMemoryService>();
        var search = Substitute.For<IHybridMemorySearch>();
        var factory = Substitute.For<IStoreLaneFactory>();
        factory.CreateAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new StoreLane(memory, search, Substitute.For<IAsyncDisposable>())));
        var metrics = new CallMetrics();
        var pool = new StorePool(factory, 1, metrics, new ListLogger<StorePool>(), TimeProvider.System);
        pool.Start();
        return (pool, metrics, memory, search);
    }

    private static object? ArgumentFor(Type type)
    {
        if (type == typeof(CancellationToken))
        {
            return Token;
        }

        if (type == typeof(string))
        {
            return "value";
        }

        if (type == typeof(float[]))
        {
            return new float[] { 1f };
        }

        if (type == typeof(IReadOnlyCollection<string>))
        {
            return new[] { "id" };
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    [Fact]
    public async Task EveryMember_LeasesAndReleasesALane()
    {
        var (pool, metrics, _, _) = NewPool();
        await using var owner = pool;
        var service = new PooledMemoryService(pool);
        var members = typeof(IMemoryService).GetMethods();
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            var arguments = member.GetParameters().Select(parameter => ArgumentFor(parameter.ParameterType)).ToArray();
            var callsBefore = metrics.Snapshot().Calls;

            await (Task)member.Invoke(service, arguments)!;

            Assert.Equal(callsBefore + 1, metrics.Snapshot().Calls);

            // The lane is free again: leasing it does not wait.
            using var lease = await pool.LeaseAsync("check", Token).WaitAsync(TestTimeout, Token);
        }
    }

    [Fact]
    public async Task Members_DelegateTheirArgumentsToTheLane()
    {
        var (pool, _, memory, _) = NewPool();
        await using var owner = pool;
        var service = new PooledMemoryService(pool);

        await service.GetAsync("id-1", Token);
        await service.DeleteAsync("id-2", Token);
        await service.SearchAsync("query", 3, null, null, Token);

        await memory.Received(1).GetAsync("id-1", Token);
        await memory.Received(1).DeleteAsync("id-2", Token);
        await memory.Received(1).SearchAsync("query", 3, null, null, Token);
    }

    [Fact]
    public async Task HybridSearch_DelegatesToTheLaneAndReleasesIt()
    {
        var (pool, metrics, _, search) = NewPool();
        await using var owner = pool;
        var expected = new HybridSearchResult([], MemorySearchMode.Dense, null, null);
        var request = new HybridSearchRequest { Query = "q", Mode = MemorySearchMode.Dense };
        search.SearchAsync(request, Token).Returns(expected);
        var pooled = new PooledHybridMemorySearch(pool);

        var result = await pooled.SearchAsync(request, Token);

        Assert.Same(expected, result);
        Assert.Equal(1, metrics.Snapshot().Calls);
        using var lease = await pool.LeaseAsync("check", Token).WaitAsync(TestTimeout, Token);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task BusyDatabase_BecomesRetryableError(int sqliteErrorCode)
    {
        var (pool, metrics, memory, _) = NewPool();
        await using var owner = pool;
        var busy = new SqliteException("database is locked", sqliteErrorCode);
        memory.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<MemoryResult?>(busy));
        var service = new PooledMemoryService(pool);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync("id", Token));

        Assert.Contains("busy", exception.Message);
        Assert.Same(busy, exception.InnerException);
        Assert.Equal(1, metrics.Snapshot().Calls);
    }

    [Fact]
    public async Task OtherSqliteError_IsNotRewritten()
    {
        var (pool, _, memory, _) = NewPool();
        await using var owner = pool;
        var corrupt = new SqliteException("database disk image is malformed", 11);
        memory.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<MemoryResult?>(corrupt));
        var service = new PooledMemoryService(pool);

        var exception = await Assert.ThrowsAsync<SqliteException>(() => service.GetAsync("id", Token));

        Assert.Same(corrupt, exception);
    }
}
