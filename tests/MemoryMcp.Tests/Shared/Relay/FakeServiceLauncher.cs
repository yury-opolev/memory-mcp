using MemoryMcp.Shared.Launch;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A launcher that counts start requests and runs <c>onStart</c> instead of starting anything.</summary>
internal sealed class FakeServiceLauncher : IServiceLauncher
{
    private readonly bool isRegistered;
    private readonly Action onStart;
    private int startCount;

    public FakeServiceLauncher(string name, bool isRegistered, Action? onStart = null)
    {
        this.Name = name;
        this.isRegistered = isRegistered;
        this.onStart = onStart ?? (() => { });
    }

    public string Name { get; }

    public int StartCount => Volatile.Read(ref this.startCount);

    public Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        return Task.FromResult(this.isRegistered);
    }

    public Task<bool> StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref this.startCount);
        this.onStart();
        return Task.FromResult(true);
    }
}
