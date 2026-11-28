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

    /// <summary>How long one launcher call (<c>schtasks</c>, <c>systemctl</c>, <c>launchctl</c>) may take.</summary>
    public static readonly TimeSpan LauncherCallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the launchers in order to start the service and stops at the first that accepts, so at most one start
    /// request succeeds. A launcher that is not registered is skipped; one whose check or start fails, is refused or
    /// takes longer than <paramref name="callTimeout"/> is reported to <paramref name="reportFailure"/> and the next is
    /// tried (the detached process last). Returns the launcher that accepted, or null.
    /// </summary>
    public static async Task<IServiceLauncher?> StartFirstAvailableAsync(
        IReadOnlyList<IServiceLauncher> launchers,
        TimeSpan callTimeout,
        Action<string> reportFailure,
        CancellationToken ct)
    {
        foreach (var launcher in launchers)
        {
            var isRegistered = await CallBoundedAsync(
                launcher.IsRegisteredAsync,
                $"Checking whether the {launcher.Name} is registered",
                callTimeout,
                reportFailure,
                ct).ConfigureAwait(false);
            if (isRegistered != true)
            {
                continue;
            }

            var isStarted = await CallBoundedAsync(
                launcher.StartAsync,
                $"Starting the memory service through the {launcher.Name}",
                callTimeout,
                reportFailure,
                ct).ConfigureAwait(false);
            if (isStarted == true)
            {
                return launcher;
            }

            if (isStarted == false)
            {
                reportFailure($"The {launcher.Name} did not accept the request to start the memory service.");
            }
        }

        return null;
    }

    /// <summary>
    /// Runs one launcher call bounded by <paramref name="callTimeout"/>, also when the launcher ignores its token.
    /// Null when it failed or timed out (reported as <paramref name="description"/>); cancellation of
    /// <paramref name="ct"/> is thrown.
    /// </summary>
    private static async Task<bool?> CallBoundedAsync(
        Func<CancellationToken, Task<bool>> call,
        string description,
        TimeSpan callTimeout,
        Action<string> reportFailure,
        CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(callTimeout > TimeSpan.Zero ? callTimeout : TimeSpan.Zero);
        try
        {
            return await call(bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            reportFailure($"{description} did not finish within {callTimeout.TotalSeconds:0.#} seconds.");
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportFailure($"{description} failed: {exception.Message}");
            return null;
        }
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
