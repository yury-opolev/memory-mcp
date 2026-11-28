using System.Text.Json;

namespace MemoryMcp.Shared.Service;

/// <summary>The one line of JSON the service answers on the control pipe.</summary>
/// <param name="Version">The service's version.</param>
/// <param name="ProcessId">The service's process id.</param>
/// <param name="Sessions">Connected sessions.</param>
/// <param name="Ready">True once the store finished starting.</param>
/// <param name="StartupError">Why the store failed to start, or null.</param>
/// <param name="Metrics">Wait and run times of memory calls.</param>
public sealed record ControlResponse(string Version, int ProcessId, int Sessions, bool Ready, string? StartupError, CallMetricsSnapshot Metrics)
{
    public string ToJson()
    {
        return JsonSerializer.Serialize(this);
    }

    public static ControlResponse? FromJson(string json)
    {
        return JsonSerializer.Deserialize<ControlResponse>(json);
    }
}
