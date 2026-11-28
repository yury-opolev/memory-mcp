using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MemoryMcp.Shared.Service;

/// <summary>
/// Accepts connections on the session pipe and runs one MCP server session per connection, all over the same
/// service provider (so every session shares the pooled memory services). A failing session ends alone.
/// </summary>
public sealed class SessionListener
{
    /// <summary>The options every service pipe is created with; there is no weaker fallback.</summary>
    public const PipeOptions ServerPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    private const string ServerName = "memory-mcp";

    private readonly string pipeName;
    private readonly IServiceProvider services;
    private readonly ILoggerFactory loggers;
    private readonly ILogger<SessionListener> logger;
    private readonly Func<string, PipeOptions, NamedPipeServerStream> pipeFactory;
    private int activeSessions;

    public SessionListener(string pipeName, IServiceProvider services, ILoggerFactory loggers)
        : this(pipeName, services, loggers, CreateServerPipe)
    {
    }

    /// <summary>Creates a listener whose pipes come from <paramref name="pipeFactory"/> (a test seam).</summary>
    public SessionListener(string pipeName, IServiceProvider services, ILoggerFactory loggers, Func<string, PipeOptions, NamedPipeServerStream> pipeFactory)
    {
        this.pipeName = pipeName;
        this.services = services;
        this.loggers = loggers;
        this.logger = loggers.CreateLogger<SessionListener>();
        this.pipeFactory = pipeFactory;
    }

    /// <summary>The number of connected sessions.</summary>
    public int ActiveSessions => Volatile.Read(ref this.activeSessions);

    public static NamedPipeServerStream CreateServerPipe(string pipeName, PipeOptions options)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options);
    }

    /// <summary>
    /// On Unix a pipe is a socket file in the temp directory; a crashed service leaves it behind and the next
    /// listener could not bind. It is removed only when nothing accepts connections on it.
    /// </summary>
    public static void RemoveStaleSocket(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        if (!File.Exists(socketPath))
        {
            return;
        }

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException)
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>
    /// Listens until <paramref name="ct"/> is cancelled, then waits for the open sessions to end. The first pipe is
    /// created before this method yields, so a pipe that cannot be created faults the returned task at once.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        McpServerOptions options;
        try
        {
            options = this.services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }

        return AcceptConnectionsAsync(this.pipeName, this.pipeFactory, pipe => this.RunSessionAsync(pipe, options, ct), this.logger, ct);
    }

    /// <summary>
    /// The accept loop both listeners share: keeps one pipe instance waiting, hands each connection to
    /// <paramref name="handleConnection"/> on its own task, and on cancellation waits for those tasks to end.
    /// </summary>
    internal static async Task AcceptConnectionsAsync(
        string pipeName,
        Func<string, PipeOptions, NamedPipeServerStream> pipeFactory,
        Func<NamedPipeServerStream, Task> handleConnection,
        ILogger logger,
        CancellationToken ct)
    {
        RemoveStaleSocket(pipeName);
        var connections = new ConcurrentDictionary<Task, byte>();
        NamedPipeServerStream? pipe = pipeFactory(pipeName, ServerPipeOptions);
        try
        {
            while (true)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException exception)
                {
                    // The client went away while connecting; this pipe instance cannot be reused.
                    logger.LogDebug(exception, "A connection attempt failed");
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    pipe = pipeFactory(pipeName, ServerPipeOptions);
                    continue;
                }

                // The connection task owns the connected pipe from here; the finally block must not dispose it.
                var connected = pipe;
                pipe = null;
                var connection = Task.Run(() => handleConnection(connected), CancellationToken.None);
                connections.TryAdd(connection, 0);
                _ = connection.ContinueWith(
                    finished =>
                    {
                        connections.TryRemove(finished, out _);
                        if (finished.IsFaulted)
                        {
                            logger.LogWarning(finished.Exception.GetBaseException(), "A connection ended with an error");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                pipe = pipeFactory(pipeName, ServerPipeOptions);
            }
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(connections.Keys).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Runs one session to its end. Never throws, so one session cannot take the listener down.</summary>
    private async Task RunSessionAsync(NamedPipeServerStream pipe, McpServerOptions options, CancellationToken ct)
    {
        var opened = Interlocked.Increment(ref this.activeSessions);
        this.logger.LogInformation("Session opened ({Sessions} active)", opened);
        try
        {
            await using var transport = new StreamServerTransport(pipe, pipe, ServerName, this.loggers);
            await using var server = McpServer.Create(transport, options, this.loggers, this.services);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "A session ended with an error");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            var remaining = Interlocked.Decrement(ref this.activeSessions);
            this.logger.LogInformation("Session closed ({Sessions} active)", remaining);
        }
    }
}
