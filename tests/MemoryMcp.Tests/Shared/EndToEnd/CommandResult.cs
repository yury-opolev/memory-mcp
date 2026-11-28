namespace MemoryMcp.Tests.Shared.EndToEnd;

/// <summary>The exit code and the standard output and error of one finished server command.</summary>
internal sealed record CommandResult(int ExitCode, string Output)
{
    /// <summary>The value of a <c>Name: value</c> line, as <c>status</c> prints the service's fields; null when absent.</summary>
    public string? Field(string name)
    {
        var prefix = name + ": ";
        foreach (var line in this.Output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..];
            }
        }

        return null;
    }
}
