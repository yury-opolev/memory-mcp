using System.Diagnostics;
using MemoryMcp.Shared.Launch;
using MemoryMcp.Tests.Shared.Service;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Run_MissingProgram_ReturnsNonZeroExit_InsteadOfThrowing()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync("memory-mcp-no-such-program-" + Guid.NewGuid().ToString("N"), ["--version"], TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Run_Cancelled_KillsTheChild()
    {
        var ct = TestContext.Current.CancellationToken;
        var processIdFile = Path.Combine(Path.GetTempPath(), "memory-mcp-runner-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var run = OperatingSystem.IsWindows()
                ? new ProcessRunner().RunAsync(
                    "powershell",
                    ["-NoProfile", "-NonInteractive", "-Command", $"Set-Content -LiteralPath '{processIdFile}' -Value $PID; Start-Sleep -Seconds 120"],
                    cancellation.Token)
                : new ProcessRunner().RunAsync("sh", ["-c", $"echo $$ > '{processIdFile}'; exec sleep 120"], cancellation.Token);
            var processId = await ReadProcessIdAsync(processIdFile, ct);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30), ct));
            await Eventually.WaitUntilAsync(() => !IsRunning(processId), ct);
        }
        finally
        {
            File.Delete(processIdFile);
        }
    }

    [Fact]
    public async Task Run_ReturnsExitCodeAndOutput()
    {
        var runner = new ProcessRunner();
        var result = OperatingSystem.IsWindows()
            ? await runner.RunAsync("cmd", ["/c", "echo hello& exit 3"], TestContext.Current.CancellationToken)
            : await runner.RunAsync("sh", ["-c", "echo hello; exit 3"], TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.Output);
    }

    private static async Task<int> ReadProcessIdAsync(string processIdFile, CancellationToken ct)
    {
        var processId = 0;
        await Eventually.WaitUntilAsync(
            () =>
            {
                try
                {
                    return File.Exists(processIdFile) && int.TryParse(File.ReadAllText(processIdFile).Trim(), out processId);
                }
                catch (IOException)
                {
                    // Still being written.
                    return false;
                }
            },
            ct);
        return processId;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
