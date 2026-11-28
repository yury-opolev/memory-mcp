using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Hosting;

/// <summary>
/// Writes log entries to one file per day, <c>service-yyyyMMdd.log</c>, and keeps the newest seven. When the
/// directory or a file cannot be written it stops logging to file instead of throwing.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetainedFileCount = 7;

    private const string FilePrefix = "service-";
    private const string FileExtension = ".log";

    private readonly string logDirectory;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private StreamWriter? writer;
    private string? currentDay;
    private bool isBroken;
    private bool isDisposed;

    public FileLoggerProvider(string logDirectory)
        : this(logDirectory, TimeProvider.System)
    {
    }

    public FileLoggerProvider(string logDirectory, TimeProvider time)
    {
        this.logDirectory = logDirectory;
        this.time = time;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, this);
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.isDisposed = true;
            this.CloseWriter();
        }
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = this.time.GetLocalNow();
        var entry = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelName(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            entry.AppendLine().Append(exception);
        }

        lock (this.gate)
        {
            if (this.isBroken || this.isDisposed)
            {
                return;
            }

            try
            {
                var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (this.writer is null || day != this.currentDay)
                {
                    this.OpenFile(day);
                }

                this.writer!.WriteLine(entry.ToString());
            }
            catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                this.isBroken = true;
                this.CloseWriter();
            }
        }
    }

    private static string LevelName(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "NON",
        };
    }

    private void OpenFile(string day)
    {
        this.CloseWriter();
        Directory.CreateDirectory(this.logDirectory);
        var path = Path.Combine(this.logDirectory, FilePrefix + day + FileExtension);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        this.writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        this.currentDay = day;
        this.DeleteOldFiles();
    }

    private void DeleteOldFiles()
    {
        var files = Directory.GetFiles(this.logDirectory, FilePrefix + "*" + FileExtension)
            .Where(file => IsDailyFile(Path.GetFileName(file)))
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Skip(RetainedFileCount);
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another process may hold it; it is tried again on the next day's file.
            }
        }
    }

    private static bool IsDailyFile(string fileName)
    {
        var day = fileName.AsSpan(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileExtension.Length);
        return day.Length == 8 && DateTime.TryParseExact(day, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private void CloseWriter()
    {
        try
        {
            this.writer?.Dispose();
        }
        catch (IOException)
        {
        }

        this.writer = null;
        this.currentDay = null;
    }
}
