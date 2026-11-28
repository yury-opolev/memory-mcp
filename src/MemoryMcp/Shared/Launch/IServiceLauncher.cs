namespace MemoryMcp.Shared.Launch;

/// <summary>One way to start the memory service for a data directory.</summary>
public interface IServiceLauncher
{
    /// <summary>What this launcher is, for messages and logs.</summary>
    string Name { get; }

    /// <summary>True when this launcher is set up for the data directory and can start the service.</summary>
    Task<bool> IsRegisteredAsync(CancellationToken ct);

    /// <summary>Asks for the service to start; true when the request was accepted. It does not wait for the service.</summary>
    Task<bool> StartAsync(CancellationToken ct);
}
