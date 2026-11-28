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

    /// <param name="args">The relay's command line, so the shared-mode check reads the settings exactly as startup did.</param>
    /// <param name="dataDirectory">The data directory the relay resolved.</param>
    /// <param name="pipeName">The service's session pipe for that directory.</param>
    /// <param name="launchers">The launchers in order; the first that reports registered starts the service.</param>
    /// <param name="logger">Where connection and start problems are reported.</param>
    public PipeServiceConnector(string[] args, string dataDirectory, string pipeName, IReadOnlyList<IServiceLauncher> launchers, ILogger logger)
    {
        this.args = args;
        this.dataDirectory = dataDirectory;
        this.pipeName = pipeName;
        this.launchers = launchers;
        this.logger = logger;
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
            var stream = await this.TryConnectAsync(attemptTimeout, ct).ConfigureAwait(false);
            if (stream is not null)
            {
                return new ConnectResult(stream, ConnectFailure.None, string.Empty);
            }

            if (!hasRequestedStart)
            {
                if (!this.IsSharedModeEnabled())
                {
                    return new ConnectResult(null, ConnectFailure.SharedModeOff, RelayMessages.SharedModeOff);
                }

                if (StopMarker.Exists(this.dataDirectory))
                {
                    return new ConnectResult(null, ConnectFailure.StoppedOnPurpose, RelayMessages.StoppedOnPurpose);
                }

                hasRequestedStart = true;
                await this.StartServiceAsync(ct).ConfigureAwait(false);
            }
            else if (Stopwatch.GetElapsedTime(attemptStarted) < attemptTimeout)
            {
                // The attempt failed at once (the pipe exists but refused us); do not spin.
                await Task.Delay(pauseAfterQuickFailure, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<Stream?> TryConnectAsync(TimeSpan attemptTimeout, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", this.pipeName, PipeDirection.InOut, ClientPipeOptions);
        try
        {
            await pipe.ConnectAsync((int)Math.Ceiling(attemptTimeout.TotalMilliseconds), ct).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException exception)
        {
            this.logger.LogDebug(exception, "Opening the memory service pipe failed");
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            // The pipe belongs to another account; it is never used.
            this.logger.LogWarning(exception, "The memory service pipe {PipeName} is not owned by the current user", this.pipeName);
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
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

    private async Task StartServiceAsync(CancellationToken ct)
    {
        try
        {
            var launcher = await ServiceLauncherFactory.StartWithFirstRegisteredAsync(this.launchers, ct).ConfigureAwait(false);
            if (launcher is null)
            {
                this.logger.LogWarning("No launcher accepted the request to start the memory service");
            }
            else
            {
                this.logger.LogInformation("Started the memory service through the {Launcher}", launcher.Name);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            this.logger.LogWarning(exception, "Starting the memory service failed");
        }
    }
}
