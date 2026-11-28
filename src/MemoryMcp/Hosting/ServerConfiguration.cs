using MemoryMcp.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MemoryMcp.Hosting;

/// <summary>Builds the configuration before a host exists, so the role can be chosen first.</summary>
public static class ServerConfiguration
{
    /// <summary>
    /// Reproduces the sources and precedence of <see cref="Host.CreateApplicationBuilder(string[])"/>
    /// (appsettings.json, environment variables, command line), then adds the data directory's settings.json last.
    /// </summary>
    public static IConfigurationRoot Build(string[] args)
    {
        // The default builder is used only as the source of the default configuration providers, so they cannot drift.
        var builder = Host.CreateApplicationBuilder(args);
        AddUserSettings(builder.Configuration);
        return builder.Configuration;
    }

    /// <summary>Adds the data directory's settings.json (which survives rebuilds) as the last source.</summary>
    public static void AddUserSettings(IConfigurationManager configuration)
    {
        var userSettings = Path.Combine(DataDirectory(configuration), "settings.json");
        if (File.Exists(userSettings))
        {
            configuration.AddJsonFile(userSettings, optional: true, reloadOnChange: false);
        }
    }

    public static string DataDirectory(IConfiguration configuration)
    {
        var dataDirectory = configuration[$"{MemoryMcpOptions.SectionName}:DataDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "memory-mcp");
        return Path.GetFullPath(dataDirectory);
    }
}
