using System.Globalization;
using System.Text.Json.Nodes;
using MemoryMcp.Shared.Relay;
using Xunit;

namespace MemoryMcp.Tests.Shared.EndToEnd;

/// <summary>
/// The built executable as MCP clients run it: relays that start their own service through the detached-process
/// fallback (a temp data directory has no launcher registered), the service, and the <c>serve</c>, <c>start</c>,
/// <c>stop</c> and <c>status</c> commands. Each test has its own data directory and stops what it started.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class SharedModeEndToEndTests : IAsyncDisposable
{
    private const int MaxCallsPastLostConnection = 3;

    private static readonly string[] toolNames = ["delete_memory", "get_memory", "ingest_memory", "search_memory", "update_memory"];

    private readonly EndToEndDataDirectory directory = EndToEndDataDirectory.Create(isSharedModeEnabled: true);

    public ValueTask DisposeAsync()
    {
        return this.directory.DisposeAsync();
    }

    [Fact]
    public async Task Relay_StartsTheService_AndListsFiveTools()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await this.directory.GetServiceProcessIdAsync(ct));

        var relay = this.directory.StartServer();
        await relay.InitializeAsync(ct);
        var names = await relay.ListToolNamesAsync(ct);

        Assert.Equal(toolNames, names);
        var serviceProcessId = await this.directory.GetServiceProcessIdAsync(ct);
        Assert.NotNull(serviceProcessId);
        Assert.NotEqual(relay.ProcessId, serviceProcessId.Value);
        Assert.True(EndToEndDataDirectory.IsAlive(serviceProcessId.Value));

        // A relay's stdout is the client's MCP channel: nothing but JSON-RPC lines.
        Assert.Empty(relay.NonJsonOutputLines);
    }

    [Fact]
    public async Task TwoRelays_ShareOneServiceProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = this.directory.StartServer();
        await first.InitializeAsync(ct);
        var second = this.directory.StartServer();
        await second.InitializeAsync(ct);

        var status = await this.directory.WaitForStatusAsync(result => result.Field("Sessions") == "2", "two sessions", ct);

        Assert.Equal(0, status.ExitCode);
        await this.AssertExactlyOneServiceAsync(status, relayCount: 2, ct);
        Assert.Equal(toolNames, await first.ListToolNamesAsync(ct));
        Assert.Equal(toolNames, await second.ListToolNamesAsync(ct));
    }

    [Fact]
    public async Task FiveRelaysStartedTogether_ExactlyOneService_AllGetASession()
    {
        var ct = TestContext.Current.CancellationToken;
        var relays = Enumerable.Range(0, 5).Select(_ => this.directory.StartServer()).ToList();

        // Every relay's first line starts its connection, so all five race to start a service.
        await Task.WhenAll(relays.Select(relay => relay.InitializeAsync(ct)));

        foreach (var relay in relays)
        {
            Assert.Equal(toolNames, await relay.ListToolNamesAsync(ct));
        }

        var status = await this.directory.WaitForStatusAsync(result => result.Field("Sessions") == "5", "five sessions", ct);
        await this.AssertExactlyOneServiceAsync(status, relayCount: 5, ct);
    }

    [Fact]
    public async Task ToolCallWithoutEmbedder_GetMemoryUnknownId_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = this.directory.StartServer();
        await relay.InitializeAsync(ct);
        var id = Guid.NewGuid().ToString();

        var response = await CallGetMemoryAsync(relay, id, ct);

        Assert.Null(ServerProcess.ErrorMessage(response));
        Assert.NotEqual(true, response["result"]?["isError"]?.GetValue<bool>());
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(response));
    }

    [Fact]
    public async Task ServiceKilled_NextCallOnOpenSession_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = this.directory.StartServer();
        await relay.InitializeAsync(ct);
        var id = Guid.NewGuid().ToString();
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(await CallGetMemoryAsync(relay, id, ct)));

        var killedProcessId = await this.directory.KillServiceAsync(ct);

        // Only the relay starts a service for this directory, and it does so once it saw the connection go: when a
        // new service answers, the relay is reconnecting (or has reconnected) and replaying the handshake.
        var status = await this.directory.WaitForStatusAsync(
            result => result.ExitCode == 0 && result.Field("ProcessId") != killedProcessId.ToString(CultureInfo.InvariantCulture),
            "a new service process",
            ct);
        var response = await CallGetMemoryAsync(relay, id, ct);

        Assert.Null(ServerProcess.ErrorMessage(response));
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(response));
        status = await this.directory.WaitForStatusAsync(result => result.Field("Sessions") == "1", "the relay's session on the new service", ct);
        Assert.NotEqual(killedProcessId.ToString(CultureInfo.InvariantCulture), status.Field("ProcessId"));
    }

    [Fact]
    public async Task Stop_HoldsServiceDown_OpenSessionGetsStoppedMessage_ThenStart_Recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = this.directory.StartServer();
        await relay.InitializeAsync(ct);
        var id = Guid.NewGuid().ToString();
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(await CallGetMemoryAsync(relay, id, ct)));

        var stop = await this.directory.RunAsync(["stop"], ct);
        Assert.Equal(0, stop.ExitCode);

        var stopped = await CallGetMemoryPastLostConnectionAsync(relay, id, ct);
        Assert.Equal(RelayMessages.StoppedOnPurpose(this.directory.DirectoryPath), ServerProcess.ErrorMessage(stopped));
        var stoppedAgain = await CallGetMemoryAsync(relay, id, ct);
        Assert.Equal(RelayMessages.StoppedOnPurpose(this.directory.DirectoryPath), ServerProcess.ErrorMessage(stoppedAgain));

        // The relay answered from the stop marker and started nothing: the service is still down.
        var status = await this.directory.StatusAsync(ct);
        Assert.Equal(3, status.ExitCode);
        Assert.Contains("stop marker: set", status.Output, StringComparison.Ordinal);
        await this.directory.WaitForStartedProcessCountAsync(1, ct);

        var start = await this.directory.RunAsync(["start"], ct);
        Assert.Equal(0, start.ExitCode);
        Assert.False(File.Exists(this.directory.StopMarkerPath));

        // The relay keeps trying in the background and joins the started service.
        await this.directory.WaitForStatusAsync(result => result.Field("Sessions") == "1", "the relay's session on the started service", ct);
        var recovered = await CallGetMemoryAsync(relay, id, ct);
        Assert.Null(ServerProcess.ErrorMessage(recovered));
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(recovered));
    }

    [Fact]
    public async Task SharedModeSwitchedOffInSettings_ThenServiceKilled_OpenSessionGetsSharedModeOffMessage_NoServiceStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = this.directory.StartServer();
        await relay.InitializeAsync(ct);
        var id = Guid.NewGuid().ToString();
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(await CallGetMemoryAsync(relay, id, ct)));

        this.directory.WriteSettings(isSharedModeEnabled: false);
        await this.directory.KillServiceAsync(ct);

        var response = await CallGetMemoryPastLostConnectionAsync(relay, id, ct);
        Assert.Equal(RelayMessages.SharedModeOff, ServerProcess.ErrorMessage(response));
        var responseAgain = await CallGetMemoryAsync(relay, id, ct);
        Assert.Equal(RelayMessages.SharedModeOff, ServerProcess.ErrorMessage(responseAgain));

        // The relay decided not to start a service before it answered; only the relay itself runs.
        var status = await this.directory.StatusAsync(ct);
        Assert.Equal(3, status.ExitCode);
        await this.directory.WaitForStartedProcessCountAsync(1, ct);
    }

    [Fact]
    public async Task SharedModeOff_NoArguments_RunsStandalone_AndCreatesNoServiceLock()
    {
        var ct = TestContext.Current.CancellationToken;
        this.directory.WriteSettings(isSharedModeEnabled: false);

        var server = this.directory.StartServer();
        await server.InitializeAsync(ct);
        var names = await server.ListToolNamesAsync(ct);
        var id = Guid.NewGuid().ToString();
        var response = await CallGetMemoryAsync(server, id, ct);

        Assert.Equal(toolNames, names);
        Assert.Equal($"Memory not found: {id}", ServerProcess.ToolText(response));
        Assert.True(File.Exists(Path.Combine(this.directory.DirectoryPath, "memory.db")));
        Assert.False(File.Exists(this.directory.ServiceLockPath));
        Assert.Equal(3, (await this.directory.StatusAsync(ct)).ExitCode);
        await this.directory.WaitForStartedProcessCountAsync(1, ct);
    }

    [Fact]
    public async Task Serve_WhileStopMarkerSet_ExitsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var stop = await this.directory.RunAsync(["stop"], ct);
        Assert.Equal(0, stop.ExitCode);
        Assert.True(File.Exists(this.directory.StopMarkerPath));

        var serve = await this.directory.RunAsync(["serve"], ct);

        Assert.Equal(0, serve.ExitCode);
        Assert.Contains("stopped on purpose", serve.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(this.directory.ServiceLockPath));
        Assert.Equal(3, (await this.directory.StatusAsync(ct)).ExitCode);
    }

    [Fact]
    public async Task Status_NotRunning_ExitCodeThree()
    {
        var ct = TestContext.Current.CancellationToken;

        var status = await this.directory.StatusAsync(ct);

        Assert.Equal(3, status.ExitCode);
        Assert.StartsWith("not running", status.Output, StringComparison.Ordinal);
        Assert.Contains("stop marker: not set", status.Output, StringComparison.Ordinal);
    }

    private static Task<JsonObject> CallGetMemoryAsync(ServerProcess server, string id, CancellationToken ct)
    {
        return server.CallToolAsync("get_memory", new JsonObject { ["id"] = id }, ct);
    }

    /// <summary>
    /// Calls get_memory until the answer is not <see cref="RelayMessages.ConnectionLost"/>. A call that reaches the
    /// relay before it noticed the service went away is forwarded and then answered as lost in flight; the call after
    /// it is the one that shows what the relay decided. The number of calls is bounded.
    /// </summary>
    private static async Task<JsonObject> CallGetMemoryPastLostConnectionAsync(ServerProcess server, string id, CancellationToken ct)
    {
        var response = await CallGetMemoryAsync(server, id, ct);
        var callCount = 1;
        while (ServerProcess.ErrorMessage(response) == RelayMessages.ConnectionLost && callCount < MaxCallsPastLostConnection)
        {
            response = await CallGetMemoryAsync(server, id, ct);
            callCount++;
        }

        return response;
    }

    /// <summary>
    /// Exactly one service: <c>status</c> reports one live process id, a second <c>serve</c> for the directory exits 0
    /// saying it is already running, and besides the relays exactly one process of the executable runs.
    /// </summary>
    private async Task AssertExactlyOneServiceAsync(CommandResult status, int relayCount, CancellationToken ct)
    {
        var serviceProcessId = int.Parse(status.Field("ProcessId")!, CultureInfo.InvariantCulture);
        Assert.True(EndToEndDataDirectory.IsAlive(serviceProcessId));

        // Services that lost the race to the instance lock exit on their own.
        await this.directory.WaitForStartedProcessCountAsync(relayCount + 1, ct);

        var secondServe = await this.directory.RunAsync(["serve"], ct);
        Assert.Equal(0, secondServe.ExitCode);
        Assert.Contains("is already running", secondServe.Output, StringComparison.Ordinal);
        Assert.Equal(serviceProcessId, await this.directory.GetServiceProcessIdAsync(ct));
    }
}
