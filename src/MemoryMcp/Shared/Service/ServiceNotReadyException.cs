namespace MemoryMcp.Shared.Service;

/// <summary>The memory service has not finished starting, or failed to start.</summary>
public sealed class ServiceNotReadyException : Exception
{
    public ServiceNotReadyException(string message)
        : base(message)
    {
    }
}
