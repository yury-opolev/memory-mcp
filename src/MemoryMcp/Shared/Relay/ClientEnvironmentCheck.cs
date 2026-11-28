using System.Collections;

namespace MemoryMcp.Shared.Relay;

/// <summary>
/// Finds the settings in a client's environment that a relay does not pass on: in shared mode the service takes its
/// settings from the data directory's settings.json and its own environment, so only the data directory and the
/// shared-mode settings of the client's environment have an effect.
/// </summary>
public static class ClientEnvironmentCheck
{
    private const string Prefix = "MemoryMcp__";
    private const string DataDirectoryVariable = "MemoryMcp__DataDirectory";
    private const string SharedPrefix = "MemoryMcp__Shared__";

    /// <summary>The names of the <c>MemoryMcp__*</c> variables other than the data directory and <c>MemoryMcp__Shared__*</c>, sorted.</summary>
    public static IReadOnlyList<string> IgnoredSettings(IDictionary environment)
    {
        var ignored = new List<string>();
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name)
            {
                continue;
            }

            var isMemoryMcpSetting = name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
            var isPassedOn = name.Equals(DataDirectoryVariable, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase);
            if (isMemoryMcpSetting && !isPassedOn)
            {
                ignored.Add(name);
            }
        }

        ignored.Sort(StringComparer.Ordinal);
        return ignored;
    }
}
