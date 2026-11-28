using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace MemoryMcp.Tests.Shared.Relay;

/// <summary>Reads and writes newline-delimited text lines on in-memory pipes.</summary>
internal static class PipeLines
{
    /// <summary>Reads the next line without its newline, or null when the writer completed.</summary>
    public static async Task<string?> ReadLineAsync(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var newline = buffer.PositionOf((byte)'\n');
            if (newline is { } position)
            {
                var line = Encoding.UTF8.GetString(buffer.Slice(0, position).ToArray());
                reader.AdvanceTo(buffer.GetPosition(1, position));
                return line;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                return null;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public static async Task WriteLineAsync(PipeWriter writer, string line, CancellationToken ct)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
    }
}
