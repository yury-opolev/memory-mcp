using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemoryMcp.Shared.Launch;

/// <summary>Runs programs with <see cref="Process"/>, passing arguments through <see cref="ProcessStartInfo.ArgumentList"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>The exit code reported for a program that could not be started at all.</summary>
    public const int NotStartedExitCode = -1;

    private const int StandardInputHandleId = -10;
    private const int StandardOutputHandleId = -11;
    private const int StandardErrorHandleId = -12;
    private const uint HandleFlagInherit = 1;

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

    public void StartDetached(string fileName, IReadOnlyList<string> arguments, IReadOnlyList<string> removedVariables)
    {
        // The standard streams are redirected and closed at once, so the started process never holds this process's
        // stdin or stdout (the client's MCP channel); its writes to them are discarded.
        var startInfo = CreateStartInfo(fileName, arguments);
        startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in removedVariables)
        {
            startInfo.Environment.Remove(name);
        }

        if (OperatingSystem.IsWindows())
        {
            StopStandardHandleInheritance();
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start.");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    /// <summary>
    /// Windows starts every child with all inheritable handles of this process, and the standard handles a relay got
    /// from its client are inheritable: a detached service would hold the client's pipes open (as extra handles, next
    /// to its own redirected streams) after the relay exits, so the client would never see the end of the relay's
    /// output. This process's standard handles are made non-inheritable; its own use of them is unaffected. On Unix
    /// there is nothing to do: the child's descriptors 0 to 2 are replaced by the redirected pipes.
    /// </summary>
    private static void StopStandardHandleInheritance()
    {
        foreach (var handleId in (int[])[StandardInputHandleId, StandardOutputHandleId, StandardErrorHandleId])
        {
            var handle = GetStdHandle(handleId);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                // A console handle may refuse the change; it is not a pipe a client waits on.
                SetHandleInformation(handle, HandleFlagInherit, 0);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandleId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

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
