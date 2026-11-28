using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// The control pipe. A client writes one line, <c>status</c> or <c>stop</c>, and gets one line of JSON, a
/// <see cref="ControlResponse"/>. <c>stop</c> also sets the stop marker and, once the answer has been read,
/// stops the host. Any other line is closed without an answer.
/// </summary>
public sealed class ControlListener
{
    public const string StatusCommand = "status";
    public const string StopCommand = "stop";

    private const int MaxCommandLength = 64;
    private static readonly TimeSpan connectionTimeout = TimeSpan.FromSeconds(5);

    private readonly string pipeName;
    private readonly string dataDirectory;
    private readonly StorePool pool;
    private readonly CallMetrics metrics;
    private readonly SessionListener sessions;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<ControlListener> logger;

    public ControlListener(
        string pipeName,
        string dataDirectory,
        StorePool pool,
        CallMetrics metrics,
        SessionListener sessions,
        IHostApplicationLifetime lifetime,
        ILogger<ControlListener> logger)
    {
        this.pipeName = pipeName;
        this.dataDirectory = dataDirectory;
        this.pool = pool;
        this.metrics = metrics;
        this.sessions = sessions;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    /// <summary>Listens until <paramref name="ct"/> is cancelled; a pipe that cannot be created faults the task at once.</summary>
    public Task RunAsync(CancellationToken ct)
    {
        return SessionListener.AcceptConnectionsAsync(
            this.pipeName,
            SessionListener.CreateServerPipe,
            pipe => this.HandleAsync(pipe, ct),
            this.logger,
            ct);
    }

    private static async Task<string?> ReadCommandAsync(Stream pipe, CancellationToken ct)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (line.Length <= MaxCommandLength)
        {
            var read = await pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (buffer[0] == (byte)'\n')
            {
                return line.ToString().Trim();
            }

            line.Append((char)buffer[0]);
        }

        return null;
    }

    private static async Task WaitForClientToCloseAsync(Stream pipe, CancellationToken ct)
    {
        var buffer = new byte[64];
        while (await pipe.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
        {
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(connectionTimeout);
        var isStopRequested = false;
        try
        {
            var command = await ReadCommandAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (command == StopCommand)
            {
                isStopRequested = true;
                this.logger.LogInformation("Stop requested on the control pipe");
                StopMarker.Set(this.dataDirectory);
            }
            else if (command != StatusCommand)
            {
                this.logger.LogWarning("An unknown control command was ignored");
                return;
            }

            var answer = Encoding.UTF8.GetBytes(this.Snapshot().ToJson() + "\n");
            await pipe.WriteAsync(answer, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            // Closing before the client has read the answer could lose it, so wait (bounded) for the client to close.
            await WaitForClientToCloseAsync(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The client went away.
        }
        catch (Exception exception)
        {
            this.logger.LogError(exception, "A control request failed");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (isStopRequested)
            {
                this.lifetime.StopApplication();
            }
        }
    }

    private ControlResponse Snapshot()
    {
        return new ControlResponse(
            MemoryServiceHost.Version,
            Environment.ProcessId,
            this.sessions.ActiveSessions,
            this.pool.IsReady,
            this.pool.StartupError,
            this.metrics.Snapshot());
    }
}
