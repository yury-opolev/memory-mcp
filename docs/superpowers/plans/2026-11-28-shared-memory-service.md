# Shared Memory Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One long-lived `MemoryMcp serve` process per data directory owns the store and serves every local agent session through thin stdio relays, on Windows, Linux and macOS.

**Architecture:** The `MemoryMcp` executable gets three roles. With no arguments it is today's standalone stdio server, or, when `MemoryMcp:Shared:Enabled` is true, a relay that forwards the client's MCP traffic over a current-user-only named pipe. `MemoryMcp serve` is the service: it runs one MCP server session per pipe connection over a pool of store "lanes" (one lane by default) and is started by an OS launcher (scheduled task, systemd user unit, launchd agent) or, failing that, as a detached process.

**Tech Stack:** .NET 10, `ModelContextProtocol` 1.0.0 (`StreamServerTransport`, `McpServer.Create`), `System.IO.Pipes` with `PipeOptions.CurrentUserOnly`, xUnit v3 + NSubstitute, PowerShell and bash install scripts.

**Spec:** `docs/superpowers/specs/2026-11-28-shared-memory-service-design.md`

## Global Constraints

- `src/MemoryMcp.Core` is not modified. `git diff --stat origin/main -- src/MemoryMcp.Core` must stay empty. If a Core change looks unavoidable, stop and ask.
- With shared mode off, behaviour is exactly today's: same startup sequence, same tools, same output.
- No TCP listener anywhere. Every pipe is created with `PipeOptions.CurrentUserOnly`; if that fails the service exits with an error instead of creating a weaker pipe.
- No token or secret in any client config. Client configs do not change.
- The wire between relay and service is plain newline-delimited MCP JSON-RPC, with no private framing.
- No new NuGet packages. Inline `Version=` attributes, no warnings-as-errors, xUnit v3 (`using Xunit;` per file, pass `TestContext.Current.CancellationToken`).
- C# style: file-scoped namespaces, one type per file, `this.` for instance members, braces always, `sealed` unless designed for inheritance, `Async` suffix, `ILogger` extension calls (this repository does not use `[LoggerMessage]`).
- New code lives under `src/MemoryMcp/Shared/` and `src/MemoryMcp/Hosting/`; tests under `tests/MemoryMcp.Tests/Shared/` and `tests/MemoryMcp.Tests/Hosting/`.
- Metrics and logs never contain query text or memory content.

## Review Focus

1. **Lines the relay cannot parse** (not JSON, a JSON array batch, a very long line): the relay forwards them unchanged and keeps running. Test in Task 4.
2. **Traffic that is not a client request**: notifications in both directions and server-to-client requests pass through; a reply from the client to a server request is not mistaken for an in-flight call. Test in Task 4.
3. **Spelling variants of one data directory** (trailing separator, relative path, different case on Windows): all map to the same pipe name, so a session never ends up on a second service. Test in Task 1.
4. **Leftovers of a crashed service** (a lock file with a dead owner, a stale Unix socket file for the pipe): the next `serve` starts normally. Test in Task 3.
5. **Several relays starting at once with no service running**: exactly one service ends up running and every relay gets a session. Test in Task 6.

---

### Task 1: Roles, settings and the standalone refactor

**Files:**
- Create: `src/MemoryMcp/Hosting/ServerRole.cs`, `RoleSelector.cs`, `ServerConfiguration.cs`, `StandaloneServer.cs`, `StoreStartup.cs`
- Create: `src/MemoryMcp/Shared/SharedOptions.cs`, `SharedPaths.cs`, `PipeNames.cs`
- Modify: `src/MemoryMcp/Program.cs` (becomes a dispatcher of about 30 lines)
- Test: `tests/MemoryMcp.Tests/Hosting/RoleSelectorTests.cs`, `ServerConfigurationTests.cs`, `tests/MemoryMcp.Tests/Shared/PipeNamesTests.cs`

**Interfaces:**
- Produces:
  - `enum ServerRole { Standalone, Relay, Service, Start, Stop, Status }`
  - `static ServerRole RoleSelector.Select(string[] args, bool sharedEnabled)`: `serve`/`start`/`stop`/`status` as the first argument select those roles; no role argument gives `Relay` when `sharedEnabled`, else `Standalone`; any other first argument that is not a `--key=value` configuration argument throws `ArgumentException`.
  - `sealed class SharedOptions { public const string SectionName = "MemoryMcp:Shared"; public bool Enabled { get; set; } public int MaxParallelCalls { get; set; } = 1; }` Values below 1 for `MaxParallelCalls` are treated as 1.
  - `static IConfigurationRoot ServerConfiguration.Build(string[] args)`: today's sources in today's order (appsettings.json, environment, command line, then the data directory's `settings.json`). `static string ServerConfiguration.DataDirectory(IConfiguration)`: today's resolution, returned as a full path.
  - `static class SharedPaths`: `ServiceLock(dataDirectory)` = `service.lock`, `StopMarker(dataDirectory)` = `service.stopped`, `ServicePathFile(dataDirectory)` = `service-path.txt`, `LogDirectory(dataDirectory)` = `logs`.
  - `static string PipeNames.Session(string dataDirectory)` = `memory-mcp-` + the first 16 hex characters of the SHA-256 of the normalised full path (trailing separators trimmed; lower-cased on Windows only). `PipeNames.Control(dataDirectory)` = the same plus `-control`. `PipeNames.Hash(dataDirectory)` = the first 8 of those hex characters (used for launcher names).
  - `static Task StoreStartup.RunAsync(IServiceProvider services, CancellationToken)`: the conversion check, `InitializeAsync` and the Ollama health check, moved verbatim from `Program.cs`.
  - `static Task<int> StandaloneServer.RunAsync(string[] args)`: today's `Program.cs` behaviour.

- [ ] **Step 1: Write the failing tests**

```csharp
// RoleSelectorTests
[Theory]
[InlineData(new string[0], false, ServerRole.Standalone)]
[InlineData(new string[0], true, ServerRole.Relay)]
[InlineData(new[] { "serve" }, false, ServerRole.Service)]
[InlineData(new[] { "serve" }, true, ServerRole.Service)]
[InlineData(new[] { "start" }, true, ServerRole.Start)]
[InlineData(new[] { "stop" }, true, ServerRole.Stop)]
[InlineData(new[] { "status" }, false, ServerRole.Status)]
[InlineData(new[] { "--MemoryMcp:DataDirectory=x" }, true, ServerRole.Relay)]
public void Select_ReturnsRole(string[] args, bool shared, ServerRole expected)
// Select_UnknownCommand_Throws: Assert.Throws<ArgumentException>(() => RoleSelector.Select(["bogus"], false))

// PipeNamesTests
// Session_SameDirectorySpelledDifferently_SameName: dir, dir + Path.DirectorySeparatorChar, and a relative
//   spelling of dir all give the same name; on Windows dir.ToUpperInvariant() too.
// Session_DifferentDirectories_DifferentNames
// Session_HasExpectedShape: Assert.Matches("^memory-mcp-[0-9a-f]{16}$", name)
// Control_IsSessionPlusSuffix; Hash_IsFirstEightHexOfSession

// ServerConfigurationTests (temp data directory, Environment variable set and restored in the test)
// Build_ReadsSharedEnabledFromDataDirectorySettings: settings.json {"MemoryMcp":{"Shared":{"Enabled":true}}}
//   => config.GetSection(SharedOptions.SectionName).Get<SharedOptions>().Enabled is true
// SharedOptions_Defaults: Enabled false, MaxParallelCalls 1
```

- [ ] **Step 2: Run `dotnet test tests/MemoryMcp.Tests` and confirm the new tests fail to compile or fail**
- [ ] **Step 3: Implement the types above; `Program.cs` builds the configuration, selects the role and, in this task, runs `StandaloneServer` for `Standalone` and prints "not implemented yet" with exit code 2 for the other roles**
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all tests pass, including the existing 245**
- [ ] **Step 5: Commit** `refactor(server): role selection, shared settings and pipe names; standalone path unchanged`

---

### Task 2: Store lanes, the call gate and measurements

**Files:**
- Create: `src/MemoryMcp/Shared/Service/StoreLane.cs`, `IStoreLaneFactory.cs`, `StoreLaneFactory.cs`, `StorePool.cs`, `StoreLease.cs`, `CallMetrics.cs`, `CallMetricsSnapshot.cs`, `ServiceNotReadyException.cs`, `PooledMemoryService.cs`, `PooledHybridMemorySearch.cs`
- Test: `tests/MemoryMcp.Tests/Shared/Service/StorePoolTests.cs`, `CallMetricsTests.cs`, `PooledMemoryServiceTests.cs`

**Interfaces:**
- Produces:
  - `sealed record StoreLane(IMemoryService Memory, IHybridMemorySearch Search, IAsyncDisposable Owner)`
  - `interface IStoreLaneFactory { Task<StoreLane> CreateAsync(bool runStartup, CancellationToken ct); }` `StoreLaneFactory` builds a private `ServiceProvider` with `AddMemoryMcpCore(configuration)` per lane; `runStartup` true runs `StoreStartup.RunAsync` (first lane only), false only initialises the store.
  - `sealed class StorePool : IAsyncDisposable` with `StorePool(IStoreLaneFactory factory, int maxParallelCalls, CallMetrics metrics, ILogger<StorePool> logger, TimeProvider time)`, `void Start()` (begins creating lanes in the background: lane 0 with startup, then the rest), `Task<StoreLease> LeaseAsync(string toolName, CancellationToken ct)`.
  - `sealed class StoreLease : IDisposable { public StoreLane Lane { get; } }` Disposing returns the lane and records the run time.
  - `LeaseAsync` waits for readiness up to `StorePool.ReadinessTimeout` (120 s), then throws `ServiceNotReadyException`; if startup failed it throws `ServiceNotReadyException` carrying the startup error message immediately. A wait longer than `StorePool.SlowWaitThreshold` (1 s) is logged at warning level with the tool name and the wait.
  - `sealed class CallMetrics { void Record(TimeSpan wait, TimeSpan run); void EnterQueue(); void LeaveQueue(); CallMetricsSnapshot Snapshot(); }` `sealed record CallMetricsSnapshot(long Calls, int QueueDepth, double WaitAverageMs, double WaitP95Ms, double WaitMaxMs, double RunAverageMs, double RunP95Ms, double RunMaxMs)`. Percentiles are over the most recent 1,000 calls.
  - `PooledMemoryService : IMemoryService` and `PooledHybridMemorySearch : IHybridMemorySearch`: every interface member leases a lane (tool name = the member name), delegates, and releases. `HybridMemorySearch`-style properties, if the interface has any, delegate to lane 0 once ready.
  - A `SqliteException` with `SqliteErrorCode` 5 (busy) or 6 (locked) from a lane is rethrown as `InvalidOperationException("The memory database is busy. Retry the call.", inner)`.

- [ ] **Step 1: Write the failing tests** (lanes are NSubstitute fakes whose members block on a `TaskCompletionSource`; use `FakeTimeProvider`-style manual time only where a threshold is asserted)

```csharp
// StorePoolTests
// LeaseAsync_OneLane_SecondCallWaitsUntilFirstReleases
// LeaseAsync_TwoLanes_TwoCallsOverlap_ThirdWaits
// LeaseAsync_BeforeStartupFinishes_WaitsThenSucceeds
// LeaseAsync_StartupFailed_ThrowsServiceNotReadyWithStartupMessage
// LeaseAsync_CancelledWhileWaiting_LeavesQueueDepthAtZero
// Start_RunsStartupOnFirstLaneOnly: factory.Received(1).CreateAsync(true, ...); Received(n-1).CreateAsync(false, ...)
// LeaseAsync_WaitOverThreshold_LogsWarningWithToolNameAndNoArguments

// CallMetricsTests
// Snapshot_NoCalls_AllZero
// Snapshot_ReportsAverageP95AndMax: record waits 10,20,...,1000 ms => Calls 100, WaitMaxMs 1000, WaitP95Ms 950
// Snapshot_QueueDepth_FollowsEnterAndLeave

// PooledMemoryServiceTests
// EveryMember_LeasesAndReleasesALane (reflection over IMemoryService: pool is free after each call)
// BusyDatabase_BecomesRetryableError: lane throws SqliteException(…, 5) => InvalidOperationException with "busy"
```

- [ ] **Step 2: Run the tests, confirm they fail**
- [ ] **Step 3: Implement the types**
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all pass**
- [ ] **Step 5: Commit** `feat(service): store lanes with a call gate, MaxParallelCalls and wait/run measurements`

---

### Task 3: The service process

**Files:**
- Create: `src/MemoryMcp/Shared/Service/MemoryServiceHost.cs`, `SessionListener.cs`, `ControlListener.cs`, `ControlResponse.cs`, `InstanceLock.cs`, `StopMarker.cs`, `ServiceCommand.cs`
- Create: `src/MemoryMcp/Hosting/FileLoggerProvider.cs`, `FileLogger.cs`
- Modify: `src/MemoryMcp/Program.cs` (`Service` role runs `ServiceCommand`)
- Test: `tests/MemoryMcp.Tests/Shared/Service/SessionListenerTests.cs`, `ControlListenerTests.cs`, `InstanceLockTests.cs`, `StopMarkerTests.cs`, `tests/MemoryMcp.Tests/Hosting/FileLoggerProviderTests.cs`

**Interfaces:**
- Consumes: `StorePool`, `CallMetrics`, `PooledMemoryService`, `PooledHybridMemorySearch`, `PipeNames`, `SharedPaths`, `SharedOptions`.
- Produces:
  - `sealed class InstanceLock : IDisposable` with `static InstanceLock? TryAcquire(string dataDirectory)`: opens `service.lock` with `FileShare.Read` exclusive write for the life of the process and writes the process id and version; returns null when another live process holds it. A leftover file with no holder is taken over.
  - `static class StopMarker { bool Exists(string dataDirectory); void Set(string dataDirectory); void Clear(string dataDirectory); }`
  - `sealed class SessionListener` with `SessionListener(string pipeName, IServiceProvider services, ILoggerFactory loggers)`, `Task RunAsync(CancellationToken)`, `int ActiveSessions { get; }`. It accepts connections on a `NamedPipeServerStream` (`PipeDirection.InOut`, `NamedPipeServerStream.MaxAllowedServerInstances`, `PipeTransmissionMode.Byte`, `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`) and runs `McpServer.Create(new StreamServerTransport(pipe, pipe, …), options, loggers, services)` per connection, where `options` is the `IOptions<McpServerOptions>` value produced by `AddMcpServer().WithToolsFromAssembly()` registered without a transport. On Unix a stale socket file for the pipe is removed before listening.
  - `sealed class ControlListener` on `PipeNames.Control`: reads one line, `status` or `stop`; answers one line of JSON, `ControlResponse`, and for `stop` sets the stop marker and triggers host shutdown. `sealed record ControlResponse(string Version, int ProcessId, int Sessions, bool Ready, string? StartupError, CallMetricsSnapshot Metrics)`.
  - `static Task<int> ServiceCommand.RunAsync(IConfigurationRoot config, string[] args)`: exit 0 with a message when the stop marker exists ("stopped on purpose; run `MemoryMcp start`") or when the lock is held ("already running"); otherwise builds a `Host` with the pooled services and `MemoryTools`, starts the listeners first, then `StorePool.Start()`, and runs until shutdown. Exit 1 when a pipe cannot be created with `CurrentUserOnly`.
  - `FileLoggerProvider(string logDirectory)`: one file per day `service-yyyyMMdd.log`, keeps the newest 7, never throws when the directory is unwritable (it stops logging to file).

- [ ] **Step 1: Write the failing tests** (pipe names are unique per test: `"memory-mcp-test-" + Guid.NewGuid().ToString("N")`; the client side is `NamedPipeClientStream` with `PipeOptions.CurrentUserOnly` wrapped in the SDK's `McpClient` over `StreamClientTransport`)

```csharp
// SessionListenerTests
// Session_ListsTheFiveMemoryTools: names are ingest_memory, get_memory, update_memory, delete_memory, search_memory
// Session_ToolCall_ReachesTheSharedMemoryService: get_memory "x" => fake IMemoryService.Received(1).GetAsync("x", …)
// TwoSessions_ShareOneMemoryService_AndActiveSessionsIsTwo
// Handshake_IsAnsweredWhileStoreIsStillStarting: lane factory blocked; initialize and tools/list complete;
//   a tool call completes only after the factory is released
// ToolCall_WhenStartupFailed_ReturnsErrorWithStartupMessage_AndSessionStaysOpen
// Listener_CreatesPipesWithCurrentUserOnly (assert via a seam: the options passed to the pipe factory)
// Listener_StartsWhenAStaleSocketFileExists (skipped on Windows)

// ControlListenerTests
// Status_ReturnsVersionSessionsReadinessAndMetrics
// Stop_SetsStopMarker_AndRequestsShutdown

// InstanceLockTests
// TryAcquire_Twice_SecondReturnsNull; TryAcquire_AfterDispose_Succeeds; TryAcquire_LeftoverFileWithNoHolder_Succeeds

// StopMarkerTests: Set_Exists_Clear round trip

// FileLoggerProviderTests
// WritesToDailyFile; KeepsNewestSevenFiles; UnwritableDirectory_DoesNotThrow
```

- [ ] **Step 2: Run the tests, confirm they fail**
- [ ] **Step 3: Implement the types.** If `IOptions<McpServerOptions>` does not carry the tool collection when no transport is registered, build the collection from `MemoryTools` with `McpServerTool.Create` and say so in the commit message.
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all pass. Run `dotnet run --project src/MemoryMcp -- serve` twice by hand against a temp data directory (`MemoryMcp__DataDirectory`, encryption off): the second prints "already running" and exits 0**
- [ ] **Step 5: Commit** `feat(service): MemoryMcp serve with per-connection MCP sessions, control pipe, single instance and stop marker`

---

### Task 4: The relay's session logic

**Files:**
- Create: `src/MemoryMcp/Shared/Relay/RelaySession.cs`, `JsonRpcPeek.cs`, `IServiceConnector.cs`, `ConnectResult.cs`, `ConnectFailure.cs`, `RelayMessages.cs`
- Test: `tests/MemoryMcp.Tests/Shared/Relay/RelaySessionTests.cs`, `JsonRpcPeekTests.cs`

**Interfaces:**
- Produces:
  - `interface IServiceConnector { Task<ConnectResult> ConnectAsync(TimeSpan timeout, CancellationToken ct); }`
  - `sealed record ConnectResult(Stream? Stream, ConnectFailure Failure, string Message)`; `enum ConnectFailure { None, Unreachable, StoppedOnPurpose, SharedModeOff }`
  - `static class JsonRpcPeek { static bool TryRead(ReadOnlySpan<byte> line, out string? method, out string? idRaw, out bool isResponse); }` It reads only top-level `method`, `id`, `result`, `error`; `idRaw` is the id's raw JSON text so numbers and strings round-trip. Returns false for anything that is not a single JSON object.
  - `sealed class RelaySession` with `RelaySession(Stream clientInput, Stream clientOutput, IServiceConnector connector, ILogger logger, TimeProvider time)` and `Task<int> RunAsync(CancellationToken)`; returns 0 when the client closes its input.
  - `static class RelayMessages`: `ErrorCode = -32000`; `Unreachable(int seconds, string logDirectory)` = "The memory service could not be reached or started within {seconds} seconds. See the service log in {logDirectory}."; `ConnectionLost` = "The memory service connection was lost while this call was running. Retry the call."; `StoppedOnPurpose` = "The memory service was stopped on purpose. Run `MemoryMcp start` to start it again."; `SharedModeOff` = "Shared mode was switched off. Restart this session to use the memory server directly."
- Behaviour to implement:
  - Lines are forwarded byte-for-byte in both directions; unparseable lines too.
  - The first `initialize` request and the `notifications/initialized` notification from the client are remembered.
  - Client requests (a `method` and an `id`) are tracked as in flight until a response with that id comes back from the service. Responses sent by the client to server requests are not tracked.
  - `RelaySession.InitialConnectTimeout` = 30 s. If the first connection fails, the pending `initialize` gets an error response with the failure's message and `RunAsync` returns 1.
  - When an established connection drops: every in-flight request is answered with `ConnectionLost`; the relay reconnects with back-off (250 ms doubling to 2 s). On success it sends the remembered `initialize` with the private id `"relay-replay"`, discards the response with that id, then sends the remembered `initialized` notification. Requests that arrive while disconnected wait up to `RelaySession.ReconnectTimeout` (30 s) and are then answered with the last failure's message; `StoppedOnPurpose` and `SharedModeOff` are answered immediately and the relay keeps retrying only for `Unreachable` and `StoppedOnPurpose`.

- [ ] **Step 1: Write the failing tests** (in-memory duplex streams; a fake connector whose queue of results the test controls)

```csharp
// JsonRpcPeekTests
// Request_GivesMethodAndRawId (ids 7, "abc"); Notification_HasNoId; Response_IsResponse
// NotJson_ReturnsFalse; JsonArrayBatch_ReturnsFalse; NestedMethodInParams_IsIgnored

// RelaySessionTests
// ForwardsClientLinesToService_AndServiceLinesToClient_Unchanged
// UnparseableAndVeryLongLines_AreForwardedUnchanged (1 MB line)
// NotificationsAndServerRequests_PassThrough_AndClientResponsesAreNotTrackedAsInFlight
// InitialConnectFails_InitializeGetsErrorWithMessage_AndRunReturnsOne
// ConnectionDrops_InFlightRequestsGetConnectionLostError_WithTheirOriginalIds
// Reconnect_ReplaysInitializeAndInitialized_AndDiscardsTheReplayResponse
// Reconnect_ThenNextRequestIsForwardedToTheNewConnection
// RequestWhileStoppedOnPurpose_GetsStoppedMessage_AndRelayRecoversWhenServiceReturns
// RequestWhileSharedModeOff_GetsSharedModeOffMessage_AndConnectorIsNotAskedToStart
// ClientClosesInput_RunReturnsZero
```

- [ ] **Step 2: Run the tests, confirm they fail**
- [ ] **Step 3: Implement the types**
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all pass**
- [ ] **Step 5: Commit** `feat(relay): forwarding session with handshake replay and in-flight error handling`

---

### Task 5: Launchers, the connector and the commands

**Files:**
- Create: `src/MemoryMcp/Shared/Launch/IServiceLauncher.cs`, `IProcessRunner.cs`, `ProcessRunner.cs`, `ProcessResult.cs`, `LauncherNames.cs`, `WindowsTaskLauncher.cs`, `SystemdUserLauncher.cs`, `LaunchdLauncher.cs`, `DetachedProcessLauncher.cs`, `ServiceLauncherFactory.cs`, `ServiceExecutable.cs`
- Create: `src/MemoryMcp/Shared/Relay/PipeServiceConnector.cs`, `RelayCommand.cs`, `ClientEnvironmentCheck.cs`
- Create: `src/MemoryMcp/Shared/Service/ControlClient.cs`, `StartCommand.cs`, `StopCommand.cs`, `StatusCommand.cs`
- Modify: `src/MemoryMcp/Program.cs` (all roles dispatched)
- Test: `tests/MemoryMcp.Tests/Shared/Launch/*Tests.cs`, `tests/MemoryMcp.Tests/Shared/Relay/PipeServiceConnectorTests.cs`, `ClientEnvironmentCheckTests.cs`

**Interfaces:**
- Consumes: `IServiceConnector`, `ConnectResult`, `PipeNames`, `SharedPaths`, `StopMarker`, `ControlResponse`.
- Produces:
  - `interface IProcessRunner { Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct); void StartDetached(string fileName, IReadOnlyList<string> arguments); }` `sealed record ProcessResult(int ExitCode, string Output)`.
  - `interface IServiceLauncher { string Name { get; } Task<bool> IsRegisteredAsync(CancellationToken ct); Task<bool> StartAsync(CancellationToken ct); }`
  - `static class LauncherNames`: `WindowsTask(hash)` = `MemoryMcp-{hash}`; `SystemdUnit(hash)` = `memory-mcp-{hash}.service`; `LaunchdLabel(hash)` = `io.github.memory-mcp.{hash}`.
  - Exact commands: Windows `schtasks /Query /TN <task>` (registered when exit code 0) and `schtasks /Run /TN <task>`; Linux `systemctl --user cat <unit>` and `systemctl --user start <unit>`; macOS `launchctl print gui/<uid>/<label>` and `launchctl kickstart gui/<uid>/<label>`.
  - `DetachedProcessLauncher`: runs `ServiceExecutable.Resolve(dataDirectory)` with `serve --MemoryMcp:DataDirectory=<dir>`; on Unix through `setsid` when it is on the PATH, so the service has its own session; `IsRegisteredAsync` is always true.
  - `static string ServiceExecutable.Resolve(string dataDirectory)`: the path in `service-path.txt` when the file exists and the path exists, else `Environment.ProcessPath`.
  - `static IServiceLauncher[] ServiceLauncherFactory.Create(string dataDirectory, IProcessRunner runner)`: the OS launcher first, the detached launcher last. The connector uses the first one that reports registered.
  - `sealed class PipeServiceConnector : IServiceConnector`: tries the pipe (2 s per attempt); when nothing is listening it checks, in this order, that shared mode is still enabled in the data directory's `settings.json` (else `SharedModeOff`), that no stop marker exists (else `StoppedOnPurpose`), then starts the service once per `ConnectAsync` call through the launcher and retries until the timeout (`Unreachable`).
  - `static IReadOnlyList<string> ClientEnvironmentCheck.IgnoredSettings(IDictionary environment)`: names of `MemoryMcp__*` variables other than `MemoryMcp__DataDirectory` and `MemoryMcp__Shared__*`. `RelayCommand` writes one stderr warning per name: "{name} is ignored in shared mode; put it in {settings.json path}."
  - `RelayCommand.RunAsync` wires stdin/stdout to `RelaySession`. `StartCommand` clears the stop marker and starts through the launcher, then waits until `status` answers. `StopCommand` sends `stop` on the control pipe (when nothing is listening it only sets the marker). `StatusCommand` prints the `ControlResponse` fields as plain lines, or "not running" plus whether the stop marker is set; exit code 0 when running, 3 when not.

- [ ] **Step 1: Write the failing tests** (a recording fake `IProcessRunner`)

```csharp
// WindowsTaskLauncherTests: IsRegistered_RunsSchtasksQuery_WithTaskName; Start_RunsSchtasksRun; NonZeroExit_IsFalse
// SystemdUserLauncherTests / LaunchdLauncherTests: the same three, with the exact commands above
// DetachedProcessLauncherTests: Start_RunsResolvedExecutableWithServeAndDataDirectory;
//   Start_OnUnixWithSetsid_WrapsInSetsid
// ServiceExecutableTests: PathFilePresent_ReturnsItsPath; PathFileMissingOrDangling_ReturnsProcessPath
// ServiceLauncherFactoryTests: Create_OsLauncherFirst_DetachedLast (per OS via an injected platform value)
// PipeServiceConnectorTests (real pipes, fake launchers):
//   ServiceListening_ConnectsWithoutStarting
//   NothingListening_StartsOnce_ThenConnects
//   OsLauncherNotRegistered_FallsBackToDetached
//   StopMarkerSet_ReturnsStoppedOnPurpose_AndDoesNotStart
//   SharedModeOffInSettings_ReturnsSharedModeOff_AndDoesNotStart
//   NeverComesUp_ReturnsUnreachableAfterTimeout
// ClientEnvironmentCheckTests: ReportsOllamaSettings_NotDataDirectoryOrSharedSettings
```

- [ ] **Step 2: Run the tests, confirm they fail**
- [ ] **Step 3: Implement the types and finish `Program.cs`**
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all pass**
- [ ] **Step 5: Commit** `feat(server): relay, start, stop and status commands with per-OS service launchers`

---

### Task 6: End-to-end tests with the real executable

**Files:**
- Create: `tests/MemoryMcp.Tests/Shared/EndToEnd/ServerProcess.cs`, `SharedModeEndToEndTests.cs`
- Modify: `tests/MemoryMcp.Tests/MemoryMcp.Tests.csproj` only if the built server executable is not already beside the test assembly

**Interfaces:**
- Consumes: the built `MemoryMcp` executable from the test output directory.
- Produces: `ServerProcess` test helper: starts the executable with a temp data directory (`MemoryMcp__DataDirectory`, `MemoryMcp__Shared__Enabled=true`, encryption off, `MemoryMcp__Ollama__Endpoint=http://127.0.0.1:9` so no embedder is needed), speaks MCP over its stdio, and can kill the service process recorded in `service.lock`.

- [ ] **Step 1: Write the tests** (each uses its own temp data directory and cleans up every process it started; they run on Windows and Linux)

```csharp
// Relay_StartsTheService_AndListsFiveTools (detached fallback, no launcher registered)
// TwoRelays_ShareOneServiceProcess: exactly one process holds service.lock; status reports Sessions 2
// FiveRelaysStartedTogether_ExactlyOneService_AllGetASession
// ToolCallWithoutEmbedder_GetMemoryUnknownId_ReturnsNotFound
// ServiceKilled_NextCallOnOpenSession_Succeeds (the relay restarts it and replays the handshake)
// Stop_HoldsServiceDown_OpenSessionGetsStoppedMessage_ThenStart_Recovers
// SharedModeSwitchedOffInSettings_ThenServiceKilled_OpenSessionGetsSharedModeOffMessage_NoServiceStarted
// SharedModeOff_NoArguments_RunsStandalone_AndCreatesNoServiceLock
// Serve_WhileStopMarkerSet_ExitsZero
// Status_NotRunning_ExitCodeThree
```

- [ ] **Step 2: Run them; fix what they expose in Tasks 1 to 5 code, with a unit test for each fix in the owning test class**
- [ ] **Step 3: Run `dotnet test MemoryMcp.slnx` three times in a row; all pass each time (these tests start processes, so flakiness has to be ruled out)**
- [ ] **Step 4: Commit** `test(shared): end-to-end tests for relay, service restart, stop and start`

---

### Task 7: Install scripts and documentation

**Files:**
- Modify: `scripts/install.ps1`, `scripts/install.sh`, `README.md`
- Create: `scripts/shared/memory-mcp.service.template`, `scripts/shared/io.github.memory-mcp.plist.template`

**Interfaces:**
- Consumes: `MemoryMcp serve|start|stop|status`, `LauncherNames`, `SharedPaths.ServicePathFile`, `PipeNames.Hash` (the scripts get the hash from `MemoryMcp status --print-hash`; add that flag to `StatusCommand` with a unit test).
- Behaviour, identical in both scripts apart from the OS launcher:
  - New option `-Shared <bool>` / `--shared true|false`. Omitted: shared mode and the launcher are left as they are.
  - Every install with shared mode on (newly or already): publish to `<root>/service/<yyyyMMdd-HHmmss>/`, write that executable's full path to `<data>/service-path.txt`, register or re-register the launcher to run `<that path> serve --MemoryMcp:DataDirectory=<data>`, run `stop` on the previous service, clear the stop marker, run `start`, and wait for `status` to succeed. Then delete service directories other than the newest two.
  - `bin/` is refreshed only when no file in it is locked; otherwise the script prints "bin is in use by open sessions and was left as is; the service was updated" and continues. When shared mode is off, `bin/` is published as today.
  - `-Shared $true` sets `MemoryMcp.Shared.Enabled` in `<data>/settings.json`, preserving other keys. `-Shared $false` sets it false, runs `stop`, unregisters the launcher, deletes `service-path.txt`, and prints "Restart open agent sessions: they keep reporting that shared mode was switched off until restarted."
  - Windows launcher: `schtasks /Create /F /TN MemoryMcp-<hash> /SC ONLOGON /RL LIMITED /TR "<path> serve --MemoryMcp:DataDirectory=<data>"` for the current user, with "run only when user is logged on" and no console window (use `Register-ScheduledTask` with a hidden `conhost`-free action if `schtasks` cannot hide the window; say which was used).
  - Linux: when `systemctl --user` works, write `~/.config/systemd/user/memory-mcp-<hash>.service` from the template (`Restart=on-failure`, `WantedBy=default.target`), `daemon-reload`, `enable`. Otherwise print that the detached fallback will be used.
  - macOS: write `~/Library/LaunchAgents/io.github.memory-mcp.<hash>.plist` from the template (`RunAtLoad` true) and `launchctl bootstrap gui/<uid>`.
- README: a new section "Shared mode: one process for all sessions" after "Quick Install" (what it is, how to switch it on and off, `start`/`stop`/`status`, `MaxParallelCalls`, that client settings other than the data directory are ignored, the security model, that macOS is implemented but not yet verified), and updates to "Configuration" and "Project Structure".

- [ ] **Step 1: Add `--print-hash` with its test; implement the script changes and the templates**
- [ ] **Step 2: Verify on Windows with a throwaway install root** (`$env:LOCALAPPDATA` pointed at a temp folder for the script run): `-Shared $true` registers the task and `status` reports running; a second install while a relay is open updates the service and leaves `bin` alone; `-Shared $false` removes the task. Record the commands and their output in the commit message body.
- [ ] **Step 3: Verify on Linux in WSL** (install the .NET SDK into `~/.dotnet` with `dotnet-install.sh`, no sudo): `--shared true` writes and enables the unit and `status` reports running; the same again with `systemctl --user` made unavailable (`XDG_RUNTIME_DIR` unset) uses the detached fallback. Record commands and output.
- [ ] **Step 4: Run `dotnet test MemoryMcp.slnx`; all pass**
- [ ] **Step 5: Commit** `feat(install): shared mode with per-OS launchers and upgrade without closing sessions`

---

### Task 8: Verification on the owner's machine

No code. Each item is a recorded command and its observed result; anything that fails goes back to the owning task.

- [ ] **Step 1: Back up** `%LOCALAPPDATA%\memory-mcp\data` and `bin` to a dated folder.
- [ ] **Step 2: Install** with `scripts\install.ps1 -Rebuild -SkipOllama -Shared $true` (sessions that are open keep their old standalone server until restarted; restart them once).
- [ ] **Step 3: Check the four clients** (Claude Code, Copilot CLI, OpenCode, coda) each run a `search_memory` call with their existing config.
- [ ] **Step 4: One owner of the database:** `Get-CimInstance Win32_Process -Filter "Name='MemoryMcp.exe'"` shows one `serve` process and one relay per session; only the `serve` process has `memory.db` open.
- [ ] **Step 5: Not reachable by another account:** from a second local Windows account (or `runas`), connecting to the pipe is refused; `netstat -ano` shows no listener owned by the service.
- [ ] **Step 6: Service restart keeps sessions:** kill the `serve` process; the next memory call in an open session succeeds.
- [ ] **Step 7: Stop and start:** `MemoryMcp stop`; an open session's call reports the stop message and the service stays down across a relay retry; `MemoryMcp start`; the next call succeeds.
- [ ] **Step 8: Upgrade without closing sessions:** rerun the install with sessions open; `status` shows the new version; open sessions keep working.
- [ ] **Step 9: Measurements:** `MemoryMcp status` shows call counts and wait/run figures after the calls above.
- [ ] **Step 10: coda and the Bridge:** start a coda session first after `stop`/`start` so that coda triggers the service start; restart the Cortex Bridge; the service is still running afterwards.
- [ ] **Step 11: Core untouched:** `git diff --stat origin/main -- src/MemoryMcp.Core` is empty.
