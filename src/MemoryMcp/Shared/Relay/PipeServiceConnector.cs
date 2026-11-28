using System.Diagnostics;
using System.IO.Pipes;
using MemoryMcp.Hosting;
using MemoryMcp.Shared.Launch;
using MemoryMcp.Shared.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Relay;

/// <summary>
/// Connects to the memory service's session pipe. When nothing listens it starts the service, once per call, but only
/// while shared mode is still on and the service was not stopped on purpose; then it keeps trying until the timeout.
/// When a service listens but refuses this process, it returns at once and starts nothing.
/// </summary>
public sealed class PipeServiceConnector : IServiceConnector
{
    /// <summary>How long one attempt to open the pipe may take.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The client side is current-user-only too, so a pipe of another account is never used.</summary>
    public const PipeOptions ClientPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private static readonly TimeSpan pauseAfterQuickFailure = TimeSpan.FromMilliseconds(250);

    private readonly string[] args;
    private readonly string dataDirectory;
    private readonly string pipeName;
    private readonly IReadOnlyList<IServiceLauncher> launchers;
    private readonly ILogger logger;
    private readonly TimeSpan launcherCallTimeout;
    private readonly Func<string, TimeSpan, CancellationToken, Task<Stream>> openPipe;

    /// <param name="args">The relay's command line, so the shared-mode check reads the settings exactly as startup did.</param>
    /// <param name="dataDirectory">The data directory the relay resolved.</param>
    /// <param name="pipeName">The service's session pipe for that directory.</param>
    /// <param name="launchers">The launchers in order; the first that accepts starts the service.</param>
    /// <param name="logger">Where connection and start problems are reported.</param>
    public PipeServiceConnector(string[] args, string dataDirectory, string pipeName, IReadOnlyList<IServiceLauncher> launchers, ILogger logger)
        : this(args, dataDirectory, pipeName, launchers, logger, ServiceLauncherFactory.LauncherCallTimeout, OpenPipeAsync)
    {
    }

    /// <summary>
    /// Creates a connector whose launcher calls are bounded by <paramref name="launcherCallTimeout"/> and whose pipe is
    /// opened by <paramref name="openPipe"/> (test seams; see <see cref="OpenPipeAsync"/> for its contract).
    /// </summary>
    public PipeServiceConnector(
        string[] args,
        string dataDirectory,
        string pipeName,
        IReadOnlyList<IServiceLauncher> launchers,
        ILogger logger,
        TimeSpan launcherCallTimeout,
        Func<string, TimeSpan, CancellationToken, Task<Stream>> openPipe)
    {
        this.args = args;
        this.dataDirectory = dataDirectory;
        this.pipeName = pipeName;
        this.launchers = launchers;
        this.logger = logger;
        this.launcherCallTimeout = launcherCallTimeout;
        this.openPipe = openPipe;
    }

    /// <summary>
    /// Opens the session pipe current-user-only within <paramref name="attemptTimeout"/>. Throws
    /// <see cref="TimeoutException"/> or <see cref="IOException"/> when nothing could be reached and
    /// <see cref="UnauthorizedAccessException"/> when a pipe exists that this process may not use.
    /// </summary>
    public static async Task<Stream> OpenPipeAsync(string pipeName, TimeSpan attemptTimeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, ClientPipeOptions);
        try
        {
            await pipe.ConnectAsync((int)Math.Ceiling(attemptTimeout.TotalMilliseconds), ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConnectResult> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var hasRequestedStart = false;
        while (true)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return new ConnectResult(
                    null,
                    ConnectFailure.Unreachable,
                    RelayMessages.Unreachable((int)timeout.TotalSeconds, SharedPaths.LogDirectory(this.dataDirectory)));
            }

            var attemptTimeout = remaining < AttemptTimeout ? remaining : AttemptTimeout;
            var attemptStarted = Stopwatch.GetTimestamp();
            var (stream, isAccessDenied) = await this.TryConnectAsync(attemptTimeout, ct).ConfigureAwait(false);
            if (stream is not null)
            {
                return new ConnectResult(stream, ConnectFailure.None, string.Empty);
            }

            if (isAccessDenied)
            {
                // A service listens but this process may not use it; starting another one would not help.
                return new ConnectResult(null, ConnectFailure.AccessDenied, RelayMessages.AccessDenied);
            }

            if (!hasRequestedStart)
            {
                if (!this.IsSharedModeEnabled())
                {
                    return new ConnectResult(null, ConnectFailure.SharedModeOff, RelayMessages.SharedModeOff);
                }

                if (StopMarker.Exists(this.dataDirectory))
                {
                    return new ConnectResult(null, ConnectFailure.StoppedOnPurpose, RelayMessages.StoppedOnPurpose(this.dataDirectory));
                }

                hasRequestedStart = true;
                var startRemaining = timeout - Stopwatch.GetElapsedTime(started);
                await this.StartServiceAsync(startRemaining < this.launcherCallTimeout ? startRemaining : this.launcherCallTimeout, ct).ConfigureAwait(false);
            }
            else if (Stopwatch.GetElapsedTime(attemptStarted) < attemptTimeout)
            {
                // The attempt failed at once (the pipe exists but refused us); do not spin.
                await Task.Delay(pauseAfterQuickFailure, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One attempt to open the pipe: the stream, or why there is none (nothing reachable, or access denied).</summary>
    private async Task<(Stream? Stream, bool IsAccessDenied)> TryConnectAsync(TimeSpan attemptTimeout, CancellationToken ct)
    {
        try
        {
            return (await this.openPipe(this.pipeName, attemptTimeout, ct).ConfigureAwait(false), false);
        }
        catch (TimeoutException)
        {
            return (null, false);
        }
        catch (IOException exception)
        {
            this.logger.LogDebug(exception, "Opening the memory service pipe failed");
            return (null, false);
        }
        catch (UnauthorizedAccessException exception)
        {
            // The pipe is owned by another account, or on Windows by another elevation; it is never used.
            this.logger.LogDebug(exception, "The memory service pipe {PipeName} refused this process", this.pipeName);
            return (null, true);
        }
    }

    /// <summary>Reads the shared-mode setting with the sources and order of startup; the data directory's settings.json wins.</summary>
    private bool IsSharedModeEnabled()
    {
        var configuration = ServerConfiguration.Build(this.args);
        try
        {
            var options = configuration.GetSection(SharedOptions.SectionName).Get<SharedOptions>() ?? new SharedOptions();
            return options.Enabled;
        }
        finally
        {
            (configuration as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Asks the launchers to start the service; each launcher call may take <paramref name="callTimeout"/> (at most what
    /// is left of the connect timeout), and one that fails or times out hands over to the next.
    /// </summary>
    private async Task StartServiceAsync(TimeSpan callTimeout, CancellationToken ct)
    {
        var launcher = await ServiceLauncherFactory.StartFirstAvailableAsync(
            this.launchers,
            callTimeout,
            failure => this.logger.LogWarning("{Failure}", failure),
            ct).ConfigureAwait(false);
        if (launcher is null)
        {
            this.logger.LogWarning("No launcher accepted the request to start the memory service");
        }
        else
        {
            this.logger.LogInformation("Started the memory service through the {Launcher}", launcher.Name);
        }
    }
}
