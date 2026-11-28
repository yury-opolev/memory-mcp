namespace MemoryMcp.Shared.Relay;

/// <summary>Opens a connection to the memory service, starting it when that is allowed.</summary>
public interface IServiceConnector
{
    /// <summary>
    /// Connects within <paramref name="timeout"/>. A failure is reported in the result, with the message the relay
    /// sends to the client; cancellation of <paramref name="ct"/> ends the attempt.
    /// </summary>
    Task<ConnectResult> ConnectAsync(TimeSpan timeout, CancellationToken ct);
}
