using ModelContextProtocol;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// The memory service has not finished starting, or failed to start. It is an <see cref="McpException"/> so the
/// MCP SDK passes its message to the client as the tool error instead of a generic one.
/// </summary>
public sealed class ServiceNotReadyException : McpException
{
    public ServiceNotReadyException(string message)
        : base(message)
    {
    }
}
