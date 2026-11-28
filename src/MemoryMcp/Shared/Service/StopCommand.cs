using System.Diagnostics;
using MemoryMcp.Hosting;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Shared.Service;

/// <summary><c>MemoryMcp stop</c>: stops the service gracefully; it stays down until <c>start</c>.</summary>
public static class StopCommand
{
    /// <summary>The service stopped, or none was running (the stop marker is then set).</summary>
    public const int StoppedExitCode = 0;

    /// <summary>The service did not answer the stop request, or was asked to stop and has not exited in time.</summary>
    public const int NotStoppedExitCode = 1;

    /// <summary>How long the service process may take to exit after it answered the stop request.</summary>
    public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    public static Task<int> RunAsync(IConfigurationRoot configuration)
    {
        var dataDirectory = ServerConfiguration.DataDirectory(configuration);
        return RunAsync(dataDirectory, PipeNames.Control(dataDirectory), Console.Out, Console.Error, CancellationToken.None);
    }

    public static Task<int> RunAsync(string dataDirectory, string controlPipeName, TextWriter output, TextWriter error, CancellationToken ct)
    {
        return RunAsync(dataDirectory, controlPipeName, output, error, ControlClient.AnswerTimeout, ExitTimeout, ct);
    }

    /// <summary>
    /// Sends <c>stop</c> on the control pipe (the service sets the stop marker itself) and waits for the service
    /// process to exit, so a <c>start</c> right after finds it gone. When nothing listens it only sets the marker. A
    /// service that does not answer within <paramref name="answerTimeout"/>, or does not exit within
    /// <paramref name="exitTimeout"/> after it answered, is reported as such, with exit code 1.
    /// </summary>
    public static async Task<int> RunAsync(
        string dataDirectory,
        string controlPipeName,
        TextWriter output,
        TextWriter error,
        TimeSpan answerTimeout,
        TimeSpan exitTimeout,
        CancellationToken ct)
    {
        var reply = await ControlClient.ExchangeAsync(controlPipeName, ControlListener.StopCommand, answerTimeout, ct).ConfigureAwait(false);
        if (reply.Outcome == ControlOutcome.NotRunning)
        {
            StopMarker.Set(dataDirectory);
            await output.WriteLineAsync(
                $"The memory service is not running. It stays stopped until you run {ServiceCommandLines.Start(dataDirectory)}.")
                .ConfigureAwait(false);
            return StoppedExitCode;
        }

        if (reply.Response is not { } response)
        {
            await error.WriteLineAsync(
                $"The memory service did not answer the stop request within {(int)answerTimeout.TotalSeconds} seconds. It may or may not be stopping; check with {ServiceCommandLines.Status(dataDirectory)}.")
                .ConfigureAwait(false);
            return NotStoppedExitCode;
        }

        if (response.Error is { } refusal)
        {
            await error.WriteLineAsync($"The memory service did not stop: {refusal}").ConfigureAwait(false);
            return NotStoppedExitCode;
        }

        if (!await WaitForExitAsync(response.ProcessId, exitTimeout, ct).ConfigureAwait(false))
        {
            await error.WriteLineAsync(
                $"The memory service (process {response.ProcessId}) was asked to stop and has not exited within {(int)exitTimeout.TotalSeconds} seconds. Open sessions report it as stopped; it stays stopped until you run {ServiceCommandLines.Start(dataDirectory)}.")
                .ConfigureAwait(false);
            return NotStoppedExitCode;
        }

        await output.WriteLineAsync(
            $"The memory service was stopped. It stays stopped until you run {ServiceCommandLines.Start(dataDirectory)}.")
            .ConfigureAwait(false);
        return StoppedExitCode;
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan exitTimeout, CancellationToken ct)
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
