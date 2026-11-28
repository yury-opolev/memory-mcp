using Microsoft.Extensions.Logging;

namespace MemoryMcp.Tests.Shared.Service;

/// <summary>A logger that keeps what was logged, for assertions.</summary>
internal sealed class ListLogger<TCategory> : ILogger<TCategory>
{
    private readonly List<(LogLevel Level, string Message)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (this.entries)
            {
                return this.entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (this.entries)
        {
            this.entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
