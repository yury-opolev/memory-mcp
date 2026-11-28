using MemoryMcp.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MemoryMcp.Tests.Hosting;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "memory-mcp-logs-" + Guid.NewGuid().ToString("N"));

    private string LogDirectory => Path.Combine(this.root, "logs");

    public void Dispose()
    {
        if (Directory.Exists(this.root))
        {
            Directory.Delete(this.root, recursive: true);
        }
    }

    [Fact]
    public void WritesToDailyFile()
    {
        var time = new SettableClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        using (var provider = new FileLoggerProvider(this.LogDirectory, time))
        {
            var logger = provider.CreateLogger("Test.Category");
            logger.LogInformation("Service started with {Lanes} lanes", 2);
            logger.LogError(new InvalidOperationException("boom"), "It failed");

            time.Advance(TimeSpan.FromDays(1));
            logger.LogWarning("Next day");
        }

        var first = File.ReadAllText(Path.Combine(this.LogDirectory, "service-20261128.log"));
        Assert.Contains("Test.Category", first, StringComparison.Ordinal);
        Assert.Contains("Service started with 2 lanes", first, StringComparison.Ordinal);
        Assert.Contains("It failed", first, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: boom", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Next day", first, StringComparison.Ordinal);

        var second = File.ReadAllText(Path.Combine(this.LogDirectory, "service-20261129.log"));
        Assert.Contains("Next day", second, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsNewestSevenFiles()
    {
        Directory.CreateDirectory(this.LogDirectory);
        for (var day = 1; day <= 10; day++)
        {
            File.WriteAllText(Path.Combine(this.LogDirectory, $"service-202611{day:00}.log"), "old");
        }

        var unrelated = Path.Combine(this.LogDirectory, "notes.txt");
        File.WriteAllText(unrelated, "keep");

        var time = new SettableClock(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
        using (var provider = new FileLoggerProvider(this.LogDirectory, time))
        {
            provider.CreateLogger("Test").LogInformation("today");
        }

        var remaining = Directory.GetFiles(this.LogDirectory, "service-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["service-20261105.log", "service-20261106.log", "service-20261107.log", "service-20261108.log", "service-20261109.log", "service-20261110.log", "service-20261128.log"],
            remaining);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void UnwritableDirectory_DoesNotThrow()
    {
        Directory.CreateDirectory(this.root);
        var file = Path.Combine(this.root, "a-file");
        File.WriteAllText(file, "not a directory");

        using var provider = new FileLoggerProvider(Path.Combine(file, "logs"));
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("first");
        logger.LogError(new InvalidOperationException("boom"), "second");

        Assert.False(Directory.Exists(Path.Combine(file, "logs")));
    }
}
