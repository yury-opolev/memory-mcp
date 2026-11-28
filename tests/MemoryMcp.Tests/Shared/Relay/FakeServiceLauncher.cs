using MemoryMcp.Shared.Launch;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>A launcher that counts start requests and runs <c>onStart</c> instead of starting anything.</summary>
internal sealed class FakeServiceLauncher : IServiceLauncher
{
    private readonly bool isRegistered;
    private readonly Action onStart;
    private readonly bool isStartAccepted;
    private readonly bool isHangingOnIsRegistered;
    private readonly bool isHangingOnStart;
    private int startCount;

    /// <param name="name">The launcher's name.</param>
    /// <param name="isRegistered">What <see cref="IsRegisteredAsync"/> answers.</param>
    /// <param name="onStart">Runs on every start request.</param>
    /// <param name="isStartAccepted">What <see cref="StartAsync"/> answers.</param>
    /// <param name="isHangingOnIsRegistered">True: <see cref="IsRegisteredAsync"/> never completes, whatever its token.</param>
    /// <param name="isHangingOnStart">True: <see cref="StartAsync"/> never completes, whatever its token.</param>
    public FakeServiceLauncher(
        string name,
        bool isRegistered,
        Action? onStart = null,
        bool isStartAccepted = true,
        bool isHangingOnIsRegistered = false,
        bool isHangingOnStart = false)
    {
        this.Name = name;
        this.isRegistered = isRegistered;
        this.onStart = onStart ?? (() => { });
        this.isStartAccepted = isStartAccepted;
        this.isHangingOnIsRegistered = isHangingOnIsRegistered;
        this.isHangingOnStart = isHangingOnStart;
    }

    public string Name { get; }

    public int StartCount => Volatile.Read(ref this.startCount);

    public Task<bool> IsRegisteredAsync(CancellationToken ct)
    {
        if (this.isHangingOnIsRegistered)
        {
            return new TaskCompletionSource<bool>().Task;
        }

        return Task.FromResult(this.isRegistered);
    }

    public Task<bool> StartAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref this.startCount);
        if (this.isHangingOnStart)
        {
            return new TaskCompletionSource<bool>().Task;
        }

        this.onStart();
        return Task.FromResult(this.isStartAccepted);
    }
}
