using System.Diagnostics;
using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp stop</c>: stops the service gracefully; it stays down until <c>start</c>.</summary>
public static class StopCommand
{
    private static readonly TimeSpan exitTimeout = TimeSpan.FromSeconds(30);

    public static Task<int> RunAsync(IConfigurationRoot configuration)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(configuration);
        return RunAsync(dataDirectory, PipeNames.Control(dataDirectory), Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary>
    /// Sends <c>stop</c> on the control pipe (the service sets the stop marker itself) and waits for the service
    /// process to exit, so a <c>start</c> right after finds it gone. When nothing listens it only sets the marker.
    /// </summary>
    public static async Task<int> RunAsync(string dataDirectory, string controlPipeName, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var response = await ControlClient.SendAsync(controlPipeName, ControlListener.StopCommand, ct).ConfigureAwait(false);
        if (response is null)
        {
            StopMarker.Set(dataDirectory);
            await output.WriteLineAsync("The memory service is not running. It stays stopped until `MemoryMcp start`.").ConfigureAwait(false);
            return 0;
        }

        if (!await WaitForExitAsync(response.ProcessId, ct).ConfigureAwait(false))
        {
            await error.WriteLineAsync(
                $"The memory service (process {response.ProcessId}) did not exit within {(int)exitTimeout.TotalSeconds} seconds.")
                .ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("The memory service was stopped. It stays stopped until `MemoryMcp start`.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<bool> WaitForExitAsync(int processId, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // It has exited already.
            return true;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(exitTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
