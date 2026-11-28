using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// SQLite reported the database busy or locked. It is an <see cref="McpException"/> so the MCP SDK passes the
/// message to the client as a clear, retryable tool error for every tool.
/// </summary>
public sealed class DatabaseBusyException : McpException
{
    public const string BusyMessage = "The memory database is busy. Retry the call.";

    public DatabaseBusyException(SqliteException innerException)
        : base(BusyMessage, innerException)
    {
    }
}
