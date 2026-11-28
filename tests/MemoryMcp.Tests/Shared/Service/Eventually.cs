namespace MemoryMcp.Tests.Shared.Service;

/// <summary>Waits for a condition with a bound, for state that changes on another thread.</summary>
internal static class Eventually
{
    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 30 seconds.");
            }

            await Task.Delay(10, ct);
        }
    }
}
