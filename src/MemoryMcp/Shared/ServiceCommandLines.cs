namespace MemoryMcp.Shared;

/// <summary>
/// The service commands as a user types them, with the data directory in use. A bare <c>MemoryMcp start</c> would act
/// on the default data directory, which need not be the one in use, so messages always name it.
/// </summary>
public static class ServiceCommandLines
{
    /// <summary>The <c>start</c> command for <paramref name="dataDirectory"/>, in backticks.</summary>
    public static string Start(string dataDirectory)
    {
        return Command("start", dataDirectory);
    }

    /// <summary>The <c>status</c> command for <paramref name="dataDirectory"/>, in backticks.</summary>
    public static string Status(string dataDirectory)
    {
        return Command("status", dataDirectory);
    }

    private static string Command(string name, string dataDirectory)
    {
        return $"`MemoryMcp {name} --MemoryMcp:DataDirectory=\"{dataDirectory}\"`";
    }
}
