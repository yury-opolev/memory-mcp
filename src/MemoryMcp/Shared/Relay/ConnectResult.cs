namespace MemoryMcp.Shared.Relay;

/// <summary>
/// The outcome of one connection attempt: the connected <paramref name="Stream"/> (owned by the caller from here) when
/// <paramref name="Failure"/> is <see cref="ConnectFailure.None"/>, else the reason and the message for the client.
/// </summary>
public sealed record ConnectResult(Stream? Stream, ConnectFailure Failure, string Message);
