namespace MemoryMcp.Shared.Service;

/// <summary>What became of one request on the control pipe.</summary>
public enum ControlOutcome
{
    /// <summary>The service answered.</summary>
    Answered,

    /// <summary>Nothing accepted the connection: no service is listening.</summary>
    NotRunning,

    /// <summary>A service accepted the connection but sent no answer in time.</summary>
    NoAnswer,
}
