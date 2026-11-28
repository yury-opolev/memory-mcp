namespace MemoryMcp.Shared.Relay;

/// <summary>The JSON-RPC error code and the messages of the error responses that originate in the relay.</summary>
public static class RelayMessages
{
    public const int ErrorCode = -32000;

    public const string ConnectionLost = "The memory service connection was lost while this call was running. Retry the call.";

    public const string StoppedOnPurpose = "The memory service was stopped on purpose. Run `MemoryMcp start` to start it again.";

    public const string SharedModeOff = "Shared mode was switched off. Restart this session to use the memory server directly.";

    public static string Unreachable(int seconds, string logDirectory)
    {
        return $"The memory service could not be reached or started within {seconds} seconds. See the service log in {logDirectory}.";
    }
}
