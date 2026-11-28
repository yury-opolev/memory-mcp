namespace MemoryMcp.Shared.Service;

/// <summary>Creates store lanes.</summary>
public interface IStoreLaneFactory
{
    /// <summary>
    /// Creates a lane. <paramref name="runStartup"/> runs the one-time startup sequence (database conversion,
    /// initialisation, embedder health check); otherwise only the store is initialised.
    /// </summary>
    Task<StoreLane> CreateAsync(bool runStartup, CancellationToken ct);
}
