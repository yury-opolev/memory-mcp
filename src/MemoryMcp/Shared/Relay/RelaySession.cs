using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MemoryMcp.Shared.Relay;

/// <summary>
/// Relays one client session's newline-delimited JSON-RPC between the client streams and the memory service, line
/// for line and byte for byte. It remembers the client's handshake so it can replay it to a new service connection,
/// and answers calls the service can no longer answer, so the client is never left waiting.
/// </summary>
/// <remarks>
/// All state is owned by one loop: the client reader, the service reader, connection attempts and timers only post
/// work to it. That keeps the client output to one writer (lines never interleave) and makes every decision about a
/// request (forward, answer with an error, drop a stale response) happen in one order.
/// </remarks>
public sealed class RelaySession
{
    /// <summary>How long the first connection may take before the pending <c>initialize</c> fails.</summary>
    public static readonly TimeSpan InitialConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a request that arrives while the connection is down waits for it to come back.</summary>
    public static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(30);

    private const string InitializeMethod = "initialize";
    private const string InitializedMethod = "notifications/initialized";
    private const string ReplayId = "relay-replay";

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly byte[] newline = "\n"u8.ToArray();
    private static readonly JsonWriterOptions errorWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions replayWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string replayIdKey = JsonRpcPeek.StringIdKey(ReplayId);

    private readonly Stream clientInput;
    private readonly Stream clientOutput;
    private readonly IServiceConnector connector;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly string logDirectory;
    private readonly Channel<Func<Task>> work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    /// <summary>The calls forwarded and not yet answered: the canonical id key, and the id as the client wrote it.</summary>
    private readonly Dictionary<string, string> inFlightIds = [];
    private readonly LinkedList<(byte[] Line, string? RequestIdRaw, ITimer? Deadline)> waitingLines = new();

    private CancellationToken sessionToken;
    private Stream? service;
    private int connectionNumber;
    private CancellationTokenSource? connectionStopping;
    private Task? serviceReader;
    private Task<ConnectResult>? connectAttempt;
    private ITimer? retryTimer;
    private TimeSpan nextRetryDelay = FirstRetryDelay;
    private ConnectResult? lastFailure;
    private bool isReplaying;
    private bool isServiceWriteFailed;
    private bool isSharedModeOff;
    private bool hasStartedConnecting;
    private bool hasConnected;
    private int? exitCode;
    private byte[]? initializeLine;
    private string? initializeIdKey;
    private byte[]? initializedLine;

    /// <param name="clientInput">The client's requests.</param>
    /// <param name="clientOutput">Where responses to the client go.</param>
    /// <param name="connector">Opens connections to the service.</param>
    /// <param name="logger">The relay's log.</param>
    /// <param name="time">The clock of the waits and retries.</param>
    /// <param name="logDirectory">The service's log directory, named in the message of a connection attempt that failed.</param>
    public RelaySession(Stream clientInput, Stream clientOutput, IServiceConnector connector, ILogger logger, TimeProvider time, string logDirectory)
    {
        this.clientInput = clientInput;
        this.clientOutput = clientOutput;
        this.connector = connector;
        this.logger = logger;
        this.time = time;
        this.logDirectory = logDirectory;
    }

    /// <summary>
    /// True when client lines go straight to the service: connected, any replayed handshake answered, and no write to
    /// the connection has failed (a connection that cannot be written to is only read to its end).
    /// </summary>
    private bool IsReady => this.service is not null && !this.isReplaying && !this.isServiceWriteFailed;

    /// <summary>
    /// Relays until the client closes its input (returns 0) or the first connection fails (returns 1, after the
    /// waiting requests were answered with the failure's message).
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var sessionStopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.sessionToken = sessionStopping.Token;
        var clientReader = this.ReadClientAsync(sessionStopping.Token);
        try
        {
            while (this.exitCode is null)
            {
                var next = await this.work.Reader.ReadAsync(ct).ConfigureAwait(false);
                await next().ConfigureAwait(false);
            }

            return this.exitCode.Value;
        }
        finally
        {
            await sessionStopping.CancelAsync().ConfigureAwait(false);
            await this.ShutdownAsync(clientReader).ConfigureAwait(false);
        }
    }

    private void Post(Func<Task> next)
    {
        this.work.Writer.TryWrite(next);
    }

    private async Task OnClientLineAsync(byte[] line)
    {
        if (this.IsReady)
        {
            await this.ForwardClientLineAsync(line).ConfigureAwait(false);
            return;
        }

        JsonRpcPeek.TryRead(line, out var method, out var idRaw, out var isResponse);
        var isRequest = method is not null && idRaw is not null && !isResponse;
        if (isResponse && this.hasConnected)
        {
            // An answer to a request of a service session that is gone; no later session asked for it.
            this.logger.LogDebug("Dropped a client response {Id} while the memory service is not connected", idRaw);
            return;
        }

        if (isRequest && this.GetImmediateFailureMessage() is { } failureMessage)
        {
            await this.WriteErrorAsync(idRaw!, failureMessage).ConfigureAwait(false);
            return;
        }

        if (this.isSharedModeOff)
        {
            return;
        }

        var node = this.waitingLines.AddLast((line, isRequest ? idRaw : null, null));
        if (isRequest && this.hasConnected)
        {
            var deadline = this.time.CreateTimer(
                _ => this.Post(() => this.OnWaitExpiredAsync(node)),
                null,
                ReconnectTimeout,
                Timeout.InfiniteTimeSpan);
            node.Value = node.Value with { Deadline = deadline };
        }

        if (!this.hasStartedConnecting)
        {
            // The first connection starts with the first client line, so the initialize it carries is always
            // waiting when a failure of that connection is handled.
            this.hasStartedConnecting = true;
            this.StartConnectAttempt(InitialConnectTimeout);
        }
    }

    /// <summary>A failure that waiting for the connection would not cure, so requests are answered with it at once.</summary>
    private static bool IsAnsweredAtOnce(ConnectFailure failure)
    {
        return failure is ConnectFailure.StoppedOnPurpose or ConnectFailure.SharedModeOff or ConnectFailure.AccessDenied;
    }

    /// <summary>
    /// The message a request gets at once while the connection is down: the service was stopped on purpose, shared
    /// mode was switched off, or this session may not connect to the service. Null when the request should wait.
    /// </summary>
    private string? GetImmediateFailureMessage()
    {
        if (this.service is null && this.lastFailure is { } failure && IsAnsweredAtOnce(failure.Failure))
        {
            return failure.Message;
        }

        return null;
    }

    private async Task OnWaitExpiredAsync(LinkedListNode<(byte[] Line, string? RequestIdRaw, ITimer? Deadline)> node)
    {
        if (node.List is null)
        {
            // Already forwarded or answered.
            return;
        }

        this.waitingLines.Remove(node);
        node.Value.Deadline?.Dispose();
        await this.WriteErrorAsync(node.Value.RequestIdRaw!, this.lastFailure?.Message ?? RelayMessages.ConnectionLost).ConfigureAwait(false);
    }

    private Task OnClientClosedAsync()
    {
        this.logger.LogInformation("The client closed its input; the relay session ends");
        this.exitCode = 0;
        return Task.CompletedTask;
    }

    /// <summary>Sends a client line to the service, tracking requests and remembering the handshake.</summary>
    private async Task ForwardClientLineAsync(byte[] line)
    {
        JsonRpcPeek.TryRead(line, out var method, out var idRaw, out var idKey, out var isResponse);
        if (method is not null && idRaw is not null && idKey is not null && !isResponse)
        {
            this.inFlightIds[idKey] = idRaw;
            if (method == InitializeMethod && this.initializeLine is null)
            {
                this.initializeLine = line;
                this.initializeIdKey = idKey;
            }

            this.logger.LogDebug("Forwarding request {Method} {Id} ({Bytes} bytes)", method, idRaw, line.Length);
        }
        else if (method == InitializedMethod && idRaw is null && this.initializedLine is null)
        {
            this.initializedLine = line;
        }

        await this.WriteToServiceAsync(line).ConfigureAwait(false);
    }

    private async Task OnServiceLineAsync(byte[] line, int fromConnection)
    {
        if (fromConnection != this.connectionNumber || this.service is null)
        {
            // A line from a connection that was already given up; its calls were answered with an error.
            return;
        }

        if (JsonRpcPeek.TryRead(line, out _, out _, out var idKey, out var isResponse) && isResponse && idKey is not null)
        {
            if (this.isReplaying && idKey == replayIdKey)
            {
                // The answer to the replayed initialize; the client had its own long ago.
                await this.CompleteReplayAsync().ConfigureAwait(false);
                return;
            }

            this.inFlightIds.Remove(idKey);
        }

        await this.WriteToClientAsync(line).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives up the connection once its reader reached the end, so every line the service sent before it went was
    /// delivered first: its calls still in flight are answered with <see cref="RelayMessages.ConnectionLost"/> and a
    /// new connection is attempted. A loss reported for an older connection is ignored.
    /// </summary>
    private async Task OnConnectionLostAsync(int fromConnection)
    {
        if (fromConnection != this.connectionNumber || this.service is null)
        {
            return;
        }

        this.logger.LogWarning("The memory service connection was lost with {Calls} calls in flight", this.inFlightIds.Count);
        await this.CloseConnectionAsync().ConfigureAwait(false);
        var lostIds = this.inFlightIds.ToList();
        this.inFlightIds.Clear();
        foreach (var (idKey, idRaw) in lostIds)
        {
            if (idKey == this.initializeIdKey)
            {
                // The handshake never completed, so there is nothing to replay; the client starts it again.
                this.initializeLine = null;
                this.initializeIdKey = null;
            }

            await this.WriteErrorAsync(idRaw, RelayMessages.ConnectionLost).ConfigureAwait(false);
        }

        this.nextRetryDelay = FirstRetryDelay;
        this.StartConnectAttempt(ReconnectTimeout);
    }

    private void StartConnectAttempt(TimeSpan timeout)
    {
        this.connectAttempt = this.ConnectAsync(timeout);
    }

    /// <summary>One connection attempt; it never throws and posts its result to the loop.</summary>
    private async Task<ConnectResult> ConnectAsync(TimeSpan timeout)
    {
        ConnectResult result;
        try
        {
            result = await this.connector.ConnectAsync(timeout, this.sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (this.sessionToken.IsCancellationRequested)
        {
            return new ConnectResult(null, ConnectFailure.Unreachable, this.UnreachableMessage(timeout));
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "A connection attempt to the memory service failed with an error");
            result = new ConnectResult(null, ConnectFailure.Unreachable, this.UnreachableMessage(timeout));
        }

        this.Post(() => this.OnConnectAttemptFinishedAsync(result));
        return result;
    }

    private string UnreachableMessage(TimeSpan timeout)
    {
        return RelayMessages.Unreachable((int)timeout.TotalSeconds, this.logDirectory);
    }

    private async Task OnConnectAttemptFinishedAsync(ConnectResult result)
    {
        this.connectAttempt = null;
        if (result is { Failure: ConnectFailure.None, Stream: { } stream })
        {
            await this.AdoptConnectionAsync(stream).ConfigureAwait(false);
            return;
        }

        if (!this.hasConnected)
        {
            this.logger.LogError("Could not connect to the memory service: {Message}", result.Message);
            await this.AnswerWaitingRequestsAsync(result.Message).ConfigureAwait(false);
            this.exitCode = 1;
            return;
        }

        var isNewFailure = this.lastFailure?.Failure != result.Failure;
        this.lastFailure = result;
        this.logger.Log(
            isNewFailure ? LogLevel.Warning : LogLevel.Debug,
            "Reconnecting to the memory service failed ({Failure}): {Message}",
            result.Failure,
            result.Message);
        if (IsAnsweredAtOnce(result.Failure))
        {
            await this.AnswerWaitingRequestsAsync(result.Message).ConfigureAwait(false);
        }

        if (result.Failure == ConnectFailure.SharedModeOff)
        {
            // This relay must not start a service again; every later request gets the same answer.
            this.isSharedModeOff = true;
            this.waitingLines.Clear();
            return;
        }

        this.ScheduleRetry();
    }

    /// <summary>Tries again after the back-off delay: 250 ms, doubling up to 2 s.</summary>
    private void ScheduleRetry()
    {
        var delay = this.nextRetryDelay;
        this.nextRetryDelay = delay * 2 < LongestRetryDelay ? delay * 2 : LongestRetryDelay;
        this.retryTimer = this.time.CreateTimer(_ => this.Post(this.OnRetryDueAsync), null, delay, Timeout.InfiniteTimeSpan);
    }

    private Task OnRetryDueAsync()
    {
        this.retryTimer?.Dispose();
        this.retryTimer = null;
        this.StartConnectAttempt(ReconnectTimeout);
        return Task.CompletedTask;
    }

    /// <summary>Answers every waiting request with <paramref name="message"/>; other waiting lines stay queued.</summary>
    private async Task AnswerWaitingRequestsAsync(string message)
    {
        var node = this.waitingLines.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.RequestIdRaw is { } idRaw)
            {
                this.waitingLines.Remove(node);
                node.Value.Deadline?.Dispose();
                await this.WriteErrorAsync(idRaw, message).ConfigureAwait(false);
            }

            node = next;
        }
    }

    private async Task AdoptConnectionAsync(Stream stream)
    {
        this.hasConnected = true;
        this.connectionNumber++;
        this.service = stream;
        this.isServiceWriteFailed = false;
        this.connectionStopping = CancellationTokenSource.CreateLinkedTokenSource(this.sessionToken);
        this.serviceReader = this.ReadServiceAsync(stream, this.connectionNumber, this.connectionStopping.Token);
        this.lastFailure = null;
        this.nextRetryDelay = FirstRetryDelay;
        this.logger.LogInformation("Connected to the memory service");
        if (this.initializeLine is { } initialize && BuildReplayLine(initialize) is { } replay)
        {
            // Client lines wait until the new service session has answered the replayed handshake.
            this.isReplaying = true;
            this.logger.LogInformation("Replaying the session handshake to the memory service");
            await this.WriteToServiceAsync(replay).ConfigureAwait(false);
            return;
        }

        await this.FlushWaitingLinesAsync().ConfigureAwait(false);
    }

    /// <summary>The remembered initialize with the private replay id, so its response can be told apart.</summary>
    private static byte[]? BuildReplayLine(byte[] initialize)
    {
        try
        {
            if (JsonNode.Parse(initialize) is not JsonObject request)
            {
                return null;
            }

            request["id"] = ReplayId;
            return Encoding.UTF8.GetBytes(request.ToJsonString(replayWriterOptions));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task CompleteReplayAsync()
    {
        this.isReplaying = false;
        if (this.initializedLine is { } initialized)
        {
            await this.WriteToServiceAsync(initialized).ConfigureAwait(false);
        }

        await this.FlushWaitingLinesAsync().ConfigureAwait(false);
    }

    /// <summary>Forwards, in arrival order, the lines that waited for the connection, while it stays usable.</summary>
    private async Task FlushWaitingLinesAsync()
    {
        while (this.IsReady && this.waitingLines.First is { } first)
        {
            this.waitingLines.RemoveFirst();
            first.Value.Deadline?.Dispose();
            await this.ForwardClientLineAsync(first.Value.Line).ConfigureAwait(false);
        }
    }

    /// <summary>Stops reading the current connection and closes it; later lines and losses from it are ignored.</summary>
    private async Task CloseConnectionAsync()
    {
        if (this.service is not { } stream)
        {
            return;
        }

        this.service = null;
        this.isReplaying = false;
        this.isServiceWriteFailed = false;
        this.connectionNumber++;
        if (this.connectionStopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        // The stream is disposed before the reader is awaited: a stream that ignores cancellation still ends its
        // pending read when it is disposed, so the relay cannot hang here.
        await stream.DisposeAsync().ConfigureAwait(false);
        if (this.serviceReader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }

        this.connectionStopping?.Dispose();
        this.connectionStopping = null;
        this.serviceReader = null;
    }

    /// <summary>
    /// Writes a line to the service. A failed write does not give up the connection at once: the service may have
    /// answered earlier calls before it went, and the connection's reader delivers those lines and then reports the
    /// loss. Until then nothing more is written to the connection; client lines wait for the next one.
    /// </summary>
    private async Task WriteToServiceAsync(byte[] line)
    {
        if (this.service is not { } stream || this.isServiceWriteFailed)
        {
            return;
        }

        try
        {
            await stream.WriteAsync(line, this.sessionToken).ConfigureAwait(false);
            await stream.WriteAsync(newline, this.sessionToken).ConfigureAwait(false);
            await stream.FlushAsync(this.sessionToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            this.logger.LogDebug(exception, "Writing to the memory service failed; reading what it sent before it went");
            this.isServiceWriteFailed = true;
        }
    }

    private async Task WriteToClientAsync(ReadOnlyMemory<byte> line)
    {
        await this.clientOutput.WriteAsync(line, this.sessionToken).ConfigureAwait(false);
        await this.clientOutput.WriteAsync(newline, this.sessionToken).ConfigureAwait(false);
        await this.clientOutput.FlushAsync(this.sessionToken).ConfigureAwait(false);
    }

    /// <summary>Answers a client request with a relay error, echoing its id exactly as the client sent it.</summary>
    private Task WriteErrorAsync(string idRaw, string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, errorWriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            writer.WriteRawValue(idRaw, skipInputValidation: true);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", RelayMessages.ErrorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        this.logger.LogInformation("Answered request {Id} with a relay error", idRaw);
        return this.WriteToClientAsync(buffer.WrittenMemory);
    }

    private async Task ReadClientAsync(CancellationToken ct)
    {
        try
        {
            await ReadLinesAsync(this.clientInput, line => this.Post(() => this.OnClientLineAsync(line)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            this.logger.LogDebug(exception, "Reading the client input failed");
        }

        this.Post(this.OnClientClosedAsync);
    }

    private async Task ReadServiceAsync(Stream stream, int fromConnection, CancellationToken ct)
    {
        try
        {
            await ReadLinesAsync(stream, line => this.Post(() => this.OnServiceLineAsync(line, fromConnection)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            this.logger.LogDebug(exception, "Reading from the memory service failed");
        }

        this.Post(() => this.OnConnectionLostAsync(fromConnection));
    }

    /// <summary>
    /// Hands each newline-terminated line (without the newline; any carriage return stays) to
    /// <paramref name="onLine"/>, and a final unterminated line as well, until the stream ends.
    /// </summary>
    private static async Task ReadLinesAsync(Stream stream, Action<byte[]> onLine, CancellationToken ct)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            // How much of the buffered, unterminated line was already searched, so a long line is scanned once.
            long searchedLength = 0;
            while (true)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (buffer.Slice(searchedLength).PositionOf((byte)'\n') is { } position)
                {
                    onLine(buffer.Slice(0, position).ToArray());
                    buffer = buffer.Slice(buffer.GetPosition(1, position));
                    searchedLength = 0;
                }

                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                    {
                        onLine(buffer.ToArray());
                    }

                    return;
                }

                searchedLength = buffer.Length;
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync(Task clientReader)
    {
        this.work.Writer.TryComplete();
        this.retryTimer?.Dispose();
        this.retryTimer = null;
        foreach (var waiting in this.waitingLines)
        {
            waiting.Deadline?.Dispose();
        }

        this.waitingLines.Clear();
        await this.CloseConnectionAsync().ConfigureAwait(false);
        if (this.connectAttempt is { } attempt)
        {
            // The attempt was cancelled with the session; a connection it still made is closed here.
            var result = await attempt.ConfigureAwait(false);
            if (result.Stream is { } stream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // The client reader was cancelled and never throws. It is awaited only when it has ended: a console input
        // stream may ignore cancellation, and the relay must not hang on it after the first connection failed.
        if (clientReader.IsCompleted)
        {
            await clientReader.ConfigureAwait(false);
        }
    }
}
