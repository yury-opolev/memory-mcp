using Microsoft.Extensions.Logging;

namespace MemoryMcp.Hosting;

/// <summary>A logger for one category that writes through its <see cref="FileLoggerProvider"/>.</summary>
public sealed class FileLogger : ILogger
{
    private readonly string category;
    private readonly FileLoggerProvider provider;

    internal FileLogger(string category, FileLoggerProvider provider)
    {
        this.category = category;
        this.provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        this.provider.Write(logLevel, this.category, formatter(state, exception), exception);
    }
}
