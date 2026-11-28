using System.ComponentModel;
using System.Diagnostics;

namespace MemoryMcp.Shared.Launch;

/// <summary>Runs programs with <see cref="Process"/>, passing arguments through <see cref="ProcessStartInfo.ArgumentList"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The exit code reported for a program that could not be started at all.</summary>
    public const int NotStartedExitCode = -1;

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = CreateStartInfo(fileName, arguments);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new ProcessResult(NotStartedExitCode, exception.Message);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited in the meantime.
            }

            throw;
        }
    }

    public void StartDetached(string fileName, IReadOnlyList<string> arguments)
    {
        // The standard streams are redirected and closed at once, so the started process never holds this process's
        // stdin or stdout (the client's MCP channel); its writes to them are discarded.
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start.");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
