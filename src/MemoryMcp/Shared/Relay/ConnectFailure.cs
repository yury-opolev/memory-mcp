namespace MemoryMcp.Shared.Relay;

/// <summary>Why a connection to the memory service could not be made.</summary>
public enum ConnectFailure
{
    /// <summary>The connection was made.</summary>
    None,

    /// <summary>The service could not be reached or started in time.</summary>
    Unreachable,

    /// <summary>The service was stopped on purpose and must not be started by a relay.</summary>
    StoppedOnPurpose,

    /// <summary>Shared mode was switched off; a relay must not start a service.</summary>
    SharedModeOff,
}
