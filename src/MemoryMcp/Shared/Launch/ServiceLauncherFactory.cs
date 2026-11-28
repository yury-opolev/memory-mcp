using System.Runtime.InteropServices;

namespace MemoryMcp.Shared.Launch;

/// <summary>The launchers to try for a data directory, in order; the first one that reports registered is used.</summary>
public static class ServiceLauncherFactory
{
    public static IServiceLauncher[] Create(string dataDirectory, IProcessRunner runner)
    {
        return Create(dataDirectory, runner, CurrentPlatform());
    }

    /// <summary>The launchers for <paramref name="platform"/>: its OS launcher first, the detached process last.</summary>
    public static IServiceLauncher[] Create(string dataDirectory, IProcessRunner runner, OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
        {
            return [new WindowsTaskLauncher(dataDirectory, runner), new DetachedProcessLauncher(dataDirectory, runner, setsidPath: null)];
        }

        var detached = new DetachedProcessLauncher(dataDirectory, runner, DetachedProcessLauncher.FindSetsid());
        if (platform == OSPlatform.Linux)
        {
            return [new SystemdUserLauncher(dataDirectory, runner), detached];
        }

        if (platform == OSPlatform.OSX)
        {
            return [new LaunchdLauncher(dataDirectory, runner), detached];
        }

        return [detached];
    }

    /// <summary>
    /// Starts the service with the first launcher that reports registered, once. Returns that launcher, or null when
    /// none is registered or its start request failed.
    /// </summary>
    public static async Task<IServiceLauncher?> StartWithFirstRegisteredAsync(IReadOnlyList<IServiceLauncher> launchers, CancellationToken ct)
    {
        foreach (var launcher in launchers)
        {
            if (await launcher.IsRegisteredAsync(ct).ConfigureAwait(false))
            {
                return await launcher.StartAsync(ct).ConfigureAwait(false) ? launcher : null;
            }
        }

        return null;
    }

    private static OSPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return OSPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.FreeBSD;
    }
}
