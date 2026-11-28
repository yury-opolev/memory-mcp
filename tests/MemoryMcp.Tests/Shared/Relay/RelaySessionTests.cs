using System.Text.Json;
using MemoryMcp.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.Relay;

public sealed class RelaySessionTests
{
    private static readonly string stoppedMessage = RelayMessages.StoppedOnPurpose(Path.Combine("data", "memory-mcp"));

    [Fact]
    public async Task InitialConnectThrows_InitializeGetsTheUnreachableMessage_AndRunReturnsOne()
    {
        await using var relay = RelayHarness.Start();
        relay.Connector.EnqueueException(new InvalidOperationException("the launcher broke"));

        await relay.SendAsync(Lines.Initialize);

        Assert.Equal(1, await relay.WaitForExitAsync());
        Lines.AssertError(await relay.ReadLineAsync(), "1", RelayMessages.Unreachable(30, RelayHarness.LogDirectory));
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task InitialConnectAccessDenied_InitializeGetsTheAccessDeniedMessage_AndRunReturnsOne()
    {
        await using var relay = RelayHarness.Start();
        relay.Connector.EnqueueFailure(ConnectFailure.AccessDenied, RelayMessages.AccessDenied);

        await relay.SendAsync(Lines.Initialize);

        Assert.Equal(1, await relay.WaitForExitAsync());
        Lines.AssertError(await relay.ReadLineAsync(), "1", RelayMessages.AccessDenied);
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task RequestWhileAccessDenied_IsAnsweredAtOnce_AndTheRelayKeepsRetrying()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(firstService);
        relay.Connector.EnqueueFailure(ConnectFailure.AccessDenied, RelayMessages.AccessDenied);
        firstService.Close();
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);

        await relay.SendAsync(Lines.ToolCall("50"));

        Lines.AssertError(await relay.ReadLineAsync(), "50", RelayMessages.AccessDenied);
        var secondService = relay.Connector.EnqueueConnection();
        relay.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Contains("relay-replay", await secondService.ReadLineAsync(relay.Token));
        Assert.Equal([RelaySession.InitialConnectTimeout, RelaySession.ReconnectTimeout, RelaySession.ReconnectTimeout], relay.Connector.Timeouts);
    }

    [Fact]
    public void Messages_HaveTheirAgreedTexts()
    {
        Assert.Equal(
            "The memory service connection was lost while this call was running. The call may or may not have completed; check before repeating a write.",
            RelayMessages.ConnectionLost);
        Assert.Equal(
            "The memory service is running, but this session is not allowed to connect to it. This happens when the session runs elevated (as administrator) and the service does not, or the other way round. Start the session at the same elevation as the service.",
            RelayMessages.AccessDenied);
        Assert.Equal(
            "The memory service was stopped on purpose. Run `MemoryMcp start --MemoryMcp:DataDirectory=\"/data/my memory\"` to start it again.",
            RelayMessages.StoppedOnPurpose("/data/my memory"));
    }

    [Fact]
    public async Task ForwardsClientLinesToService_AndServiceLinesToClient_Unchanged()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();

        await relay.HandshakeAsync(service);
        var spacedRequest = """ { "jsonrpc" : "2.0", "id" : "abc", "method" : "tools/call", "params" : { "query" : "secret words" } }""" + "\r";
        await relay.SendAsync(spacedRequest);
        Assert.Equal(spacedRequest, await service.ReadLineAsync(relay.Token));
        var spacedResult = """{"id":"abc",  "jsonrpc":"2.0","result":{"text":"secret content"}}""";
        await service.WriteLineAsync(spacedResult, relay.Token);
        Assert.Equal(spacedResult, await relay.ReadLineAsync());

        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await service.ReadLineAsync(relay.Token));
        Assert.Equal([RelaySession.InitialConnectTimeout], relay.Connector.Timeouts);
        Assert.DoesNotContain(relay.Logger.Entries, entry => entry.Message.Contains("secret"));
    }

    [Fact]
    public async Task UnparseableAndVeryLongLines_AreForwardedUnchanged()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        var longLine = $$$"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"text":"{{{new string('x', 1024 * 1024)}}}"}}""";
        string[] clientLines = ["not json at all", """[{"jsonrpc":"2.0","id":1,"method":"ping"}]""", """{"broken":""", longLine];
        string[] serviceLines = ["}{ garbage", longLine.Replace("x", "y")];

        var reading = Task.Run(
            async () =>
            {
                var received = new List<string?>();
                foreach (var unused in clientLines)
                {
                    received.Add(await service.ReadLineAsync(relay.Token));
                }

                return received;
            },
            relay.Token);
        foreach (var line in clientLines)
        {
            await relay.SendAsync(line);
        }

        Assert.Equal(clientLines, await reading);
        foreach (var line in serviceLines)
        {
            await service.WriteLineAsync(line, relay.Token);
            Assert.Equal(line, await relay.ReadLineAsync());
        }

        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
    }

    [Fact]
    public async Task NotificationsAndServerRequests_PassThrough_AndClientResponsesAreNotTrackedAsInFlight()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        var clientNotification = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":4}}""";
        var serverRequest = """{"jsonrpc":"2.0","id":"s1","method":"roots/list"}""";
        var clientResponse = """{"jsonrpc":"2.0","id":"s1","result":{"roots":[]}}""";
        var serverNotification = """{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info"}}""";

        await relay.SendAsync(clientNotification);
        Assert.Equal(clientNotification, await service.ReadLineAsync(relay.Token));
        await service.WriteLineAsync(serverRequest, relay.Token);
        Assert.Equal(serverRequest, await relay.ReadLineAsync());
        await relay.SendAsync(clientResponse);
        Assert.Equal(clientResponse, await service.ReadLineAsync(relay.Token));
        await service.WriteLineAsync(serverNotification, relay.Token);
        Assert.Equal(serverNotification, await relay.ReadLineAsync());

        // Only calls the client made get an error when the connection drops; the client's response is not one.
        service.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);
        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task InitialConnectFails_InitializeGetsErrorWithMessage_AndRunReturnsOne()
    {
        await using var relay = RelayHarness.Start();
        var message = RelayMessages.Unreachable(30, @"C:\data\logs");
        relay.Connector.EnqueueFailure(ConnectFailure.Unreachable, message);

        await relay.SendAsync(Lines.Initialize);

        Assert.Equal(1, await relay.WaitForExitAsync());
        Lines.AssertError(await relay.ReadLineAsync(), "1", message);
        Assert.Null(await relay.ReadLineAsync());
        Assert.Equal([RelaySession.InitialConnectTimeout], relay.Connector.Timeouts);
    }

    [Fact]
    public async Task ConnectionDrops_InFlightRequestsGetConnectionLostError_WithTheirOriginalIds()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        foreach (var idRaw in new[] { "7", "\"abc\"", "8" })
        {
            await relay.SendAsync(Lines.ToolCall(idRaw));
            Assert.Equal(Lines.ToolCall(idRaw), await service.ReadLineAsync(relay.Token));
        }

        await service.WriteLineAsync(Lines.ToolResult("8"), relay.Token);
        Assert.Equal(Lines.ToolResult("8"), await relay.ReadLineAsync());

        service.Close();

        var errors = new[] { await relay.ReadLineAsync(), await relay.ReadLineAsync() };
        var errorFor7 = Assert.Single(errors, line => line!.Contains("\"id\":7,"));
        var errorForAbc = Assert.Single(errors, line => line!.Contains("\"id\":\"abc\","));
        Lines.AssertError(errorFor7, "7", RelayMessages.ConnectionLost);
        Lines.AssertError(errorForAbc, "\"abc\"", RelayMessages.ConnectionLost);
        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await relay.ReadLineAsync());
        Assert.True(service.RelayStream.IsDisposed);
    }

    [Fact]
    public async Task Reconnect_ReplaysInitializeAndInitialized_AndDiscardsTheReplayResponse()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(firstService);
        var secondService = relay.Connector.EnqueueConnection();

        firstService.Close();

        var replay = await secondService.ReadLineAsync(relay.Token);
        using (var replayDocument = JsonDocument.Parse(replay!))
        using (var initializeDocument = JsonDocument.Parse(Lines.Initialize))
        {
            Assert.Equal("\"relay-replay\"", replayDocument.RootElement.GetProperty("id").GetRawText());
            Assert.Equal("initialize", replayDocument.RootElement.GetProperty("method").GetString());
            Assert.Equal(
                initializeDocument.RootElement.GetProperty("params").GetRawText(),
                replayDocument.RootElement.GetProperty("params").GetRawText());
        }

        await secondService.WriteLineAsync(Lines.ReplayResult, relay.Token);
        Assert.Equal(Lines.Initialized, await secondService.ReadLineAsync(relay.Token));

        // The replay response never reached the client: the next line it reads is the answer to its own call.
        await relay.SendAsync(Lines.ToolCall("10"));
        Assert.Equal(Lines.ToolCall("10"), await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ToolResult("10"), relay.Token);
        Assert.Equal(Lines.ToolResult("10"), await relay.ReadLineAsync());
        Assert.True(firstService.RelayStream.IsDisposed);
    }

    [Fact]
    public async Task Reconnect_ThenNextRequestIsForwardedToTheNewConnection()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(firstService);
        firstService.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);

        // A call made while the connection is down waits (its deadline timer shows it is queued), then goes to the
        // new connection after the replayed handshake, exactly once.
        await relay.SendAsync(Lines.ToolCall("11"));
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);
        var secondService = relay.Connector.EnqueueConnection();
        Assert.Contains("relay-replay", await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ReplayResult, relay.Token);
        Assert.Equal(Lines.Initialized, await secondService.ReadLineAsync(relay.Token));
        Assert.Equal(Lines.ToolCall("11"), await secondService.ReadLineAsync(relay.Token));
        Assert.Equal(0, relay.Time.ScheduledTimerCount);
        await secondService.WriteLineAsync(Lines.ToolResult("11"), relay.Token);
        Assert.Equal(Lines.ToolResult("11"), await relay.ReadLineAsync());

        await relay.SendAsync(Lines.ToolCall("12"));
        Assert.Equal(Lines.ToolCall("12"), await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ToolResult("12"), relay.Token);
        Assert.Equal(Lines.ToolResult("12"), await relay.ReadLineAsync());

        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task RequestWhileUnreachable_WaitsForReconnectTimeout_ThenGetsTheFailureMessage()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        var message = RelayMessages.Unreachable(30, @"C:\data\logs");
        relay.Connector.EnqueueFailure(ConnectFailure.Unreachable, message);
        relay.Connector.EnqueueFailure(ConnectFailure.Unreachable, message);
        service.Close();
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);

        await relay.SendAsync(Lines.ToolCall("13"));
        await relay.Time.WaitForScheduledTimersAsync(2, relay.Token);
        Assert.Equal([TimeSpan.FromMilliseconds(250), RelaySession.ReconnectTimeout], relay.Time.ScheduledDelays);
        relay.Time.Advance(TimeSpan.FromMilliseconds(250));
        await relay.Connector.WaitForCallsAsync(3, relay.Token);
        await relay.Time.WaitForScheduledTimersAsync(2, relay.Token);
        relay.Time.Advance(RelaySession.ReconnectTimeout - TimeSpan.FromMilliseconds(250) - TimeSpan.FromTicks(1));

        // Still waiting one tick before its deadline; the retries go on meanwhile (the fourth attempt is pending).
        await relay.Connector.WaitForCallsAsync(4, relay.Token);
        relay.Time.Advance(TimeSpan.FromTicks(1));

        Lines.AssertError(await relay.ReadLineAsync(), "13", message);
    }

    [Fact]
    public async Task Reconnect_BacksOffFrom250MillisecondsDoublingToTwoSeconds()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        for (var failure = 0; failure < 5; failure++)
        {
            relay.Connector.EnqueueFailure(ConnectFailure.Unreachable, "unreachable");
        }

        service.Close();

        var delays = new List<TimeSpan>();
        for (var retry = 0; retry < 5; retry++)
        {
            await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);
            var delay = Assert.Single(relay.Time.ScheduledDelays);
            delays.Add(delay);
            relay.Time.Advance(delay);
        }

        Assert.Equal([250, 500, 1000, 2000, 2000], delays.Select(delay => delay.TotalMilliseconds));
        await relay.Connector.WaitForCallsAsync(7, relay.Token);
        Assert.Equal(RelaySession.ReconnectTimeout, relay.Connector.Timeouts[^1]);
    }

    [Fact]
    public async Task RequestWhileStoppedOnPurpose_GetsstoppedMessage_AndRelayRecoversWhenServiceReturns()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(firstService);
        relay.Connector.EnqueueFailure(ConnectFailure.StoppedOnPurpose, stoppedMessage);
        firstService.Close();
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);

        await relay.SendAsync(Lines.ToolCall("20"));

        Lines.AssertError(await relay.ReadLineAsync(), "20", stoppedMessage);
        Assert.Equal(1, relay.Time.ScheduledTimerCount);
        var secondService = relay.Connector.EnqueueConnection();
        relay.Time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Contains("relay-replay", await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ReplayResult, relay.Token);
        Assert.Equal(Lines.Initialized, await secondService.ReadLineAsync(relay.Token));
        await relay.SendAsync(Lines.ToolCall("21"));
        Assert.Equal(Lines.ToolCall("21"), await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ToolResult("21"), relay.Token);
        Assert.Equal(Lines.ToolResult("21"), await relay.ReadLineAsync());
    }

    [Fact]
    public async Task RequestWaitingWhenServiceIsStoppedOnPurpose_IsAnsweredAtOnce()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        service.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);
        await relay.SendAsync(Lines.ToolCall("22"));
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);

        relay.Connector.EnqueueFailure(ConnectFailure.StoppedOnPurpose, stoppedMessage);

        Lines.AssertError(await relay.ReadLineAsync(), "22", stoppedMessage);
    }

    [Fact]
    public async Task RequestWhileSharedModeOff_GetsSharedModeOffMessage_AndConnectorIsNotAskedToStart()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        relay.Connector.EnqueueFailure(ConnectFailure.SharedModeOff, RelayMessages.SharedModeOff);
        service.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);

        // Whether this call arrives before or after the failure is handled, it is answered as soon as it is known.
        await relay.SendAsync(Lines.ToolCall("30"));
        Lines.AssertError(await relay.ReadLineAsync(), "30", RelayMessages.SharedModeOff);

        Assert.Equal(0, relay.Time.ScheduledTimerCount);
        relay.Time.Advance(TimeSpan.FromMinutes(5));
        await relay.SendAsync(Lines.ToolCall("31"));
        Lines.AssertError(await relay.ReadLineAsync(), "31", RelayMessages.SharedModeOff);
        Assert.Equal(2, relay.Connector.CallCount);
        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
    }

    [Fact]
    public async Task ClientClosesInputWhileReconnecting_RunReturnsZero()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        service.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);
        await relay.SendAsync(Lines.ToolCall("9"));

        relay.CloseInput();

        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Equal([RelaySession.InitialConnectTimeout, RelaySession.ReconnectTimeout], relay.Connector.Timeouts);
        Assert.Equal(0, relay.Time.ScheduledTimerCount);
    }

    [Fact]
    public async Task ClientClosesInput_RunReturnsZero()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);

        relay.CloseInput();

        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.True(service.RelayStream.IsDisposed);
    }

    [Fact]
    public async Task ClientClosesInputBeforeAnyConnection_RunReturnsZero()
    {
        await using var relay = RelayHarness.Start();
        await relay.SendAsync(Lines.Initialize);

        relay.CloseInput();

        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Equal([RelaySession.InitialConnectTimeout], relay.Connector.Timeouts);
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task ServiceAnswersAndCloses_WhileTheNextCallCannotBeWritten_TheAnswerIsDelivered_AndOnlyTheUnsentCallIsLost()
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        await relay.SendAsync(Lines.ToolCall("40"));
        Assert.Equal(Lines.ToolCall("40"), await service.ReadLineAsync(relay.Token));

        // The service exits after it answered call 40: the relay's write of call 41 fails before it read that answer.
        service.BreakRelayWrites();
        await relay.SendAsync(Lines.ToolCall("41"));
        await service.RelayStream.WriteFailed.WaitAsync(relay.Token);
        await service.WriteLineAsync(Lines.ToolResult("40"), relay.Token);
        service.Close();

        Assert.Equal(Lines.ToolResult("40"), await relay.ReadLineAsync());
        Lines.AssertError(await relay.ReadLineAsync(), "41", RelayMessages.ConnectionLost);
        await relay.Connector.WaitForCallsAsync(2, relay.Token);
        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task CallAfterAFailedWrite_WaitsForTheNextConnection()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(firstService);
        firstService.BreakRelayWrites();
        await relay.SendAsync(Lines.ToolCall("42"));
        await firstService.RelayStream.WriteFailed.WaitAsync(relay.Token);

        // The connection cannot take this call any more, so it waits instead of being written to a dead pipe.
        await relay.SendAsync(Lines.ToolCall("43"));
        await relay.Time.WaitForScheduledTimersAsync(1, relay.Token);
        var secondService = relay.Connector.EnqueueConnection();
        firstService.Close();

        Lines.AssertError(await relay.ReadLineAsync(), "42", RelayMessages.ConnectionLost);
        Assert.Contains("relay-replay", await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ReplayResult, relay.Token);
        Assert.Equal(Lines.Initialized, await secondService.ReadLineAsync(relay.Token));
        Assert.Equal(Lines.ToolCall("43"), await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.ToolResult("43"), relay.Token);
        Assert.Equal(Lines.ToolResult("43"), await relay.ReadLineAsync());
    }

    [Theory]
    [InlineData("5.0", "5")]
    [InlineData("\"a/b\"", "\"a\\/b\"")]
    public async Task ResponseWithReencodedId_EndsTheCall_SoALaterDropReportsNothingForIt(string requestIdRaw, string responseIdRaw)
    {
        await using var relay = RelayHarness.Start();
        var service = relay.Connector.EnqueueConnection();
        await relay.HandshakeAsync(service);
        await relay.SendAsync(Lines.ToolCall(requestIdRaw));
        Assert.Equal(Lines.ToolCall(requestIdRaw), await service.ReadLineAsync(relay.Token));
        await service.WriteLineAsync(Lines.ToolResult(responseIdRaw), relay.Token);
        Assert.Equal(Lines.ToolResult(responseIdRaw), await relay.ReadLineAsync());

        service.Close();
        await relay.Connector.WaitForCallsAsync(2, relay.Token);

        relay.CloseInput();
        Assert.Equal(0, await relay.WaitForExitAsync());
        Assert.Null(await relay.ReadLineAsync());
    }

    [Fact]
    public async Task InitializeLostInFlight_IsAnsweredAsLost_AndNotReplayedToTheNextConnection()
    {
        await using var relay = RelayHarness.Start();
        var firstService = relay.Connector.EnqueueConnection();
        await relay.SendAsync(Lines.Initialize);
        Assert.Equal(Lines.Initialize, await firstService.ReadLineAsync(relay.Token));
        var secondService = relay.Connector.EnqueueConnection();

        firstService.Close();

        Lines.AssertError(await relay.ReadLineAsync(), "1", RelayMessages.ConnectionLost);

        // The client starts the handshake again; the new connection sees it as the first line, not a replay.
        await relay.SendAsync(Lines.Initialize);
        Assert.Equal(Lines.Initialize, await secondService.ReadLineAsync(relay.Token));
        await secondService.WriteLineAsync(Lines.InitializeResult, relay.Token);
        Assert.Equal(Lines.InitializeResult, await relay.ReadLineAsync());
    }

    [Fact]
    public async Task ClientClosesInput_WhileTheConnectionIgnoresCancellation_RunStillEnds()
    {
        await using var relay = RelayHarness.Start();
        var stream = new CancellationIgnoringStream();
        relay.Connector.EnqueueStream(stream, stream);
        await relay.SendAsync(Lines.Initialize);
        await stream.Written.WaitAsync(relay.Token);

        relay.CloseInput();

        var exitCode = await relay.Run.WaitAsync(TimeSpan.FromSeconds(10), relay.Token);
        Assert.Equal(0, exitCode);
    }
}
