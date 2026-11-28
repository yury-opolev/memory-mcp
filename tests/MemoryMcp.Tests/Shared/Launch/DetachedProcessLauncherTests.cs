using System.Collections;
using MemoryMcp.Shared;
using MemoryMcp.Shared.Launch;
using Xunit;

namespace MemoryMcp.Tests.Shared.Launch;

public sealed class DetachedProcessLauncherTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "memory mcp 'detached' " + Guid.NewGuid().ToString("N"));
    private readonly RecordingProcessRunner runner = new();
    private readonly string executable;

    public DetachedProcessLauncherTests()
    {
        Directory.CreateDirectory(this.dataDirectory);
        this.executable = Path.Combine(this.dataDirectory, "service", "MemoryMcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(this.executable)!);
        File.WriteAllText(this.executable, string.Empty);
        File.WriteAllText(SharedPaths.ServicePathFile(this.dataDirectory), this.executable);
    }

    public void Dispose()
    {
        Directory.Delete(this.dataDirectory, recursive: true);
    }

    [Fact]
    public async Task IsRegistered_IsAlwaysTrue_AndRunsNothing()
    {
        var launcher = new DetachedProcessLauncher(this.dataDirectory, this.runner, setsidPath: null);

        Assert.True(await launcher.IsRegisteredAsync(TestContext.Current.CancellationToken));
        Assert.Empty(this.runner.Processes);
    }

    [Fact]
    public async Task Start_RunsResolvedExecutableWithServeAndDataDirectory()
    {
        var launcher = new DetachedProcessLauncher(this.dataDirectory, this.runner, setsidPath: null);

        var isStarted = await launcher.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(isStarted);
        var process = Assert.Single(this.runner.Processes);
        Assert.True(process.IsDetached);
        Assert.Equal(this.executable, process.FileName);
        Assert.Equal(["serve", "--MemoryMcp:DataDirectory=" + this.dataDirectory], process.Arguments);
    }

    [Fact]
    public async Task Start_OnUnixWithSetsid_WrapsInSetsid()
    {
        var launcher = new DetachedProcessLauncher(this.dataDirectory, this.runner, setsidPath: "/usr/bin/setsid");

        await launcher.StartAsync(TestContext.Current.CancellationToken);

        var process = Assert.Single(this.runner.Processes);
        Assert.True(process.IsDetached);
        Assert.Equal("/usr/bin/setsid", process.FileName);
        Assert.Equal([this.executable, "serve", "--MemoryMcp:DataDirectory=" + this.dataDirectory], process.Arguments);
    }

    [Fact]
    public async Task Start_RemovesEveryMemoryMcpSettingFromTheServiceEnvironment()
    {
        var environment = new Hashtable
        {
            ["MemoryMcp__Ollama__Endpoint"] = "http://localhost:11434",
            ["MEMORYMCP__ENCRYPTION__ENABLED"] = "false",
            ["MemoryMcp__DataDirectory"] = this.dataDirectory,
            ["MemoryMcp__Shared__Enabled"] = "true",
            ["PATH"] = "/usr/bin",
            ["MemoryMcpOther"] = "unrelated",
        };
        var launcher = new DetachedProcessLauncher(this.dataDirectory, this.runner, setsidPath: null, () => environment);

        await launcher.StartAsync(TestContext.Current.CancellationToken);

        var process = Assert.Single(this.runner.Processes);
        Assert.Equal(
            ["MEMORYMCP__ENCRYPTION__ENABLED", "MemoryMcp__DataDirectory", "MemoryMcp__Ollama__Endpoint", "MemoryMcp__Shared__Enabled"],
            process.RemovedVariables.Order(StringComparer.Ordinal));
        Assert.Equal(["serve", "--MemoryMcp:DataDirectory=" + this.dataDirectory], process.Arguments);
    }

    [Fact]
    public void FindSetsid_OnWindows_IsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null(DetachedProcessLauncher.FindSetsid());
    }
}
