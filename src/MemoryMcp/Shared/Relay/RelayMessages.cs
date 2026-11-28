namespace MemoryMcp.Shared.Relay;

/// <summary>The JSON-RPC error code and the messages of the error responses that originate in the relay.</summary>
public static class RelayMessages
{
    public const int ErrorCode = -32000;

    public const string ConnectionLost =
        "The memory service connection was lost while this call was running. The call may or may not have completed; check before repeating a write.";

    public const string SharedModeOff = "Shared mode was switched off. Restart this session to use the memory server directly.";

    public const string AccessDenied =
        "The memory service is running, but this session is not allowed to connect to it. This happens when the session runs elevated (as administrator) and the service does not, or the other way round. Start the session at the same elevation as the service.";

    public static string StoppedOnPurpose(string dataDirectory)
    {
        return $"The memory service was stopped on purpose. Run {ServiceCommandLines.Start(dataDirectory)} to start it again.";
    }

    public static string Unreachable(int seconds, string logDirectory)
    {
        return $"The memory service could not be reached or started within {seconds} seconds. See the service log in {logDirectory}.";
    }
}
