using System.Diagnostics;

namespace MemoryMcp.Tests.Shared.Service;

/// <summary>The id of a process that has already exited, standing in for a service that stopped.</summary>
internal static class ExitedProcess
{
    public static async Task<int> GetIdAsync(CancellationToken ct)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", ["/c", "exit 0"])
            : new ProcessStartInfo("sh", ["-c", "exit 0"]);
        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync(ct);
        return process.Id;
    }
}
