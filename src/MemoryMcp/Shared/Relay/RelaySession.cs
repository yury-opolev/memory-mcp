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
    private const string ReplayIdRaw = "\"" + ReplayId + "\"";

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly byte[] newline = "\n"u8.ToArray();
    private static readonly JsonWriterOptions errorWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions replayWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Stream clientInput;
    private readonly Stream clientOutput;
    private readonly IServiceConnector connector;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly Channel<Func<Task>> work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> inFlightIds = [];
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
    private bool isSharedModeOff;
    private bool hasStartedConnecting;
    private bool hasConnected;
    private int? exitCode;
    private byte[]? initializeLine;
    private string? initializeIdRaw;
    private byte[]? initializedLine;

    public RelaySession(Stream clientInput, Stream clientOutput, IServiceConnector connector, ILogger logger, TimeProvider time)
    {
        this.clientInput = clientInput;
        this.clientOutput = clientOutput;
        this.connector = connector;
        this.logger = logger;
        this.time = time;
    }

    /// <summary>True when client lines go straight to the service: connected, and any replayed handshake answered.</summary>
    private bool IsReady => this.service is not null && !this.isReplaying;

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

    /// <summary>
    /// The message a request gets at once while the connection is down: the service was stopped on purpose or shared
    /// mode was switched off. Null when the request should wait for the connection.
    /// </summary>
    private string? GetImmediateFailureMessage()
    {
        if (this.service is null && this.lastFailure is { Failure: ConnectFailure.StoppedOnPurpose or ConnectFailure.SharedModeOff } failure)
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
        JsonRpcPeek.TryRead(line, out var method, out var idRaw, out var isResponse);
        if (method is not null && idRaw is not null && !isResponse)
        {
            this.inFlightIds.Add(idRaw);
            if (method == InitializeMethod && this.initializeLine is null)
            {
                this.initializeLine = line;
                this.initializeIdRaw = idRaw;
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

        if (JsonRpcPeek.TryRead(line, out _, out var idRaw, out var isResponse) && isResponse && idRaw is not null)
        {
            if (this.isReplaying && idRaw == ReplayIdRaw)
            {
                // The answer to the replayed initialize; the client had its own long ago.
                await this.CompleteReplayAsync().ConfigureAwait(false);
                return;
            }

            this.inFlightIds.Remove(idRaw);
        }

        await this.WriteToClientAsync(line).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives up the connection: its calls in flight are answered with <see cref="RelayMessages.ConnectionLost"/> and
    /// a new connection is attempted. A loss reported for an older connection is ignored.
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
        foreach (var idRaw in lostIds)
        {
            if (idRaw == this.initializeIdRaw)
            {
                // The handshake never completed, so there is nothing to replay; the client starts it again.
                this.initializeLine = null;
                this.initializeIdRaw = null;
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
            return new ConnectResult(null, ConnectFailure.Unreachable, RelayMessages.ConnectionLost);
        }
        catch (Exception exception)
        {
            this.logger.LogWarning(exception, "A connection attempt to the memory service failed with an error");
            result = new ConnectResult(null, ConnectFailure.Unreachable, RelayMessages.ConnectionLost);
        }

        this.Post(() => this.OnConnectAttemptFinishedAsync(result));
        return result;
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
        if (result.Failure is ConnectFailure.StoppedOnPurpose or ConnectFailure.SharedModeOff)
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
        this.connectionNumber++;
        if (this.connectionStopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        if (this.serviceReader is { } reader)
        {
            await reader.ConfigureAwait(false);
        }

        this.connectionStopping?.Dispose();
        this.connectionStopping = null;
        this.serviceReader = null;
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WriteToServiceAsync(byte[] line)
    {
        if (this.service is not { } stream)
        {
            return;
        }

        var writtenTo = this.connectionNumber;
        try
        {
            await stream.WriteAsync(line, this.sessionToken).ConfigureAwait(false);
            await stream.WriteAsync(newline, this.sessionToken).ConfigureAwait(false);
            await stream.FlushAsync(this.sessionToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            this.logger.LogDebug(exception, "Writing to the memory service failed");
            await this.OnConnectionLostAsync(writtenTo).ConfigureAwait(false);
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
