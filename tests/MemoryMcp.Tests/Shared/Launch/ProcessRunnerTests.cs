using MemoryMcp.Shared.Launch;
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
    public async Task Run_ReturnsExitCodeAndOutput()
    {
        var runner = new ProcessRunner();
        var result = OperatingSystem.IsWindows()
            ? await runner.RunAsync("cmd", ["/c", "echo hello& exit 3"], TestContext.Current.CancellationToken)
            : await runner.RunAsync("sh", ["-c", "echo hello; exit 3"], TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello", result.Output);
    }
}
