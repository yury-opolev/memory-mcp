using System.IO.Pipes;
using System.Runtime.InteropServices;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

/// <summary>
/// <see cref="ProcessRunner.StartDetached"/> with real children. These tests change process-wide state (environment
/// variables, the standard output handle), so they share the non-parallel environment collection.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class ProcessRunnerDetachedTests : IDisposable
{
    private const string RemovedVariable = "MemoryMcp__TestRemoved";
    private const string KeptVariable = "MemoryMcp__TestKept";
    private const int StandardOutputHandleId = -11;

    private readonly string outputFile = Path.Combine(Path.GetTempPath(), "memory-mcp-detached-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RemovedVariable, null);
        Environment.SetEnvironmentVariable(KeptVariable, null);
        if (File.Exists(this.outputFile))
        {
            File.Delete(this.outputFile);
        }
    }

    [Fact]
    public async Task StartDetached_ChildDoesNotSeeRemovedVariables()
    {
        Environment.SetEnvironmentVariable(RemovedVariable, "removed-value");
        Environment.SetEnvironmentVariable(KeptVariable, "kept-value");

        if (OperatingSystem.IsWindows())
        {
            new ProcessRunner().StartDetached("cmd", ["/c", $"echo [%{RemovedVariable}%][%{KeptVariable}%]>{this.outputFile}"], [RemovedVariable]);
        }
        else
        {
            new ProcessRunner().StartDetached("sh", ["-c", $"echo \"[${RemovedVariable}][${KeptVariable}]\" > '{this.outputFile}'"], [RemovedVariable]);
        }

        var written = await this.ReadOutputFileAsync();
        Assert.Contains("kept-value", written);
        Assert.DoesNotContain("removed-value", written);
    }

    [Fact]
    public async Task StartDetached_ChildDoesNotHoldThisProcessesStandardOutputOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On Unix the child's descriptors 0 to 2 are replaced by the redirected pipes, and .NET opens every other
            // descriptor close-on-exec; only Windows can pass an inheritable standard handle on as an extra handle.
            return;
        }

        // Stand in for a relay whose stdout is the client's pipe: an inheritable pipe end as this process's stdout.
        using var clientEnd = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var relayEnd = new AnonymousPipeClientStream(PipeDirection.Out, clientEnd.ClientSafePipeHandle);
        MakeInheritable(relayEnd.SafePipeHandle.DangerousGetHandle());
        var originalOutput = GetStdHandle(StandardOutputHandleId);
        SetStdHandle(StandardOutputHandleId, relayEnd.SafePipeHandle.DangerousGetHandle());
        try
        {
            // A child that lives about ten seconds.
            new ProcessRunner().StartDetached("ping", ["-n", "11", "127.0.0.1"], []);
        }
        finally
        {
            SetStdHandle(StandardOutputHandleId, originalOutput);
        }

        // The "relay" exits: its end of the pipe closes. The client must see the end of the stream now, not when the
        // detached child exits.
        relayEnd.Dispose();
        clientEnd.DisposeLocalCopyOfClientHandle();
        var read = clientEnd.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Same(read, finished);
        Assert.Equal(0, await read);
    }

    private static void MakeInheritable(IntPtr handle)
    {
        const uint HandleFlagInherit = 1;
        Assert.True(SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandleId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int standardHandleId, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    private async Task<string> ReadOutputFileAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var text = await File.ReadAllTextAsync(this.outputFile, TestContext.Current.CancellationToken);
                if (text.Contains(']', StringComparison.Ordinal))
                {
                    return text;
                }
            }
            catch (IOException)
            {
                // Not written yet, or still being written.
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The child did not write its output file.");
    }
}
