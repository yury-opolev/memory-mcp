# Shared memory service: one process for all local agent sessions

Date: 2026-11-28
Status: design, awaiting approval

Decisions are marked **(owner)** when the repository owner made them and **(designer)** when the
designer chose a default the owner has not explicitly confirmed.

## Why

Every MCP client (Claude Code, GitHub Copilot CLI, OpenCode, coda) launches its own `MemoryMcp.exe`
over stdio. On the owner's machine six were running at once. Each one opens the same `memory.db`.

That caused real trouble on 2026-11-28: upgrading the server and converting the database needed every
one of those processes stopped, which meant closing every agent session. It also means several
processes write one SQLite file, each holds its own embedder client, and each loads the full server.

## Goal

One long-lived memory service per OS user owns the database and the embedder connection and serves
every local agent session. **(owner)**

- It must be authenticated and must only serve callers on the same host. **(owner)**
- It must work on Windows, Linux and macOS from the first version. **(owner)**
- The Cortex agent, which consumes `MemoryMcp.Core` as a library in a single process, must keep
  working unchanged. It does not need this feature. **(owner)**
- coda (the coding agent bundled with Cortex, a host-side stdio client of this server) moves to the
  shared service with the other clients; that is accepted. **(owner)**

## Non-goals

- An HTTP endpoint. No current client needs one; it can be added later as a second listener.
  **(designer)**
- Serving other OS users, other machines, or a network.
- Changes to `MemoryMcp.Core`. Everything lives in the server project (`src/MemoryMcp`) and its tests.
  If the builder finds a Core change unavoidable, that is a stop-and-ask, because it puts the Cortex
  agent in scope (rebuild and `Cortex.Contained.Agent.Host.Tests` would then be required).
- Making the Cortex agent a client of the service.
- Idle shutdown of the service.
- A CI pipeline. The repository has none; adding one is a separate decision.

## Anti-goals

- A secret token copied into client config files.
- Any listener reachable from another machine or another OS account.
- The service dying because the client that happened to start it exited.
- Changing behaviour for people who do not opt in: without shared mode the server must behave exactly
  as it does today.

## Design

### Three roles, one executable **(designer)**

`MemoryMcp` gains two roles next to today's behaviour. Clients keep launching the same command with no
arguments; no client config changes.

| Role | How it is selected | What it does |
|---|---|---|
| Standalone | default, shared mode off | Today's behaviour: stdio MCP server that opens the store in-process. |
| Relay | no arguments, shared mode on | Thin stdio front. Opens no database, loads no embedder. Forwards the session's MCP traffic to the service. |
| Service | `MemoryMcp serve` | The one process that opens the store and serves every relay. |

Shared mode is a setting, `MemoryMcp:Shared:Enabled`, read from the same places as the other settings
(the data directory's `settings.json`, environment). The install scripts switch it on and off.

Small helper commands are part of the same executable: `status` (is the service running, its version,
how many sessions are connected), `stop` (graceful stop) and `start`. Their exact shape is the
builder's choice.

### Settings: the data directory is the unit **(designer)**

- A relay resolves its data directory exactly as the server does today (the `MemoryMcp:DataDirectory`
  setting from its own environment, else the default). It reads `Shared:Enabled` from that directory's
  `settings.json`, and it connects to, or starts, the service for that directory.
- The service takes its settings from the data directory's `settings.json` and from its own
  environment, never from a client. The launcher is registered per data directory and passes that
  directory to `serve`.
- In shared mode, any other `MemoryMcp__*` setting in a client's environment (embedder endpoint, model,
  dimensions, API key) has no effect. The relay writes a warning to stderr when it sees one, naming
  the setting and saying it belongs in `settings.json`. The install script does not migrate such
  values.
- Fact checked on the owner's machine on 2026-11-28: all four clients (Claude Code, Copilot CLI,
  OpenCode, coda) pass only `MemoryMcp__DataDirectory`, all with the same value
  (`%LOCALAPPDATA%\memory-mcp\data`), and no `MemoryMcp*` user or machine environment variables exist.
  So no settings need moving there.

### The link: a local pipe restricted to the current user **(designer)**

- Relay and service talk over a .NET named pipe opened with the current-user-only restriction. On
  Windows that is a named pipe with an ACL for the user; on Linux and macOS .NET implements it as a
  Unix domain socket with the same restriction.
- This is the authentication: the operating system vouches for the caller's identity. There is no
  token. Nothing listens on TCP, so other machines cannot reach it, and other OS accounts are refused.
- If the pipe cannot be created with that restriction, the service must refuse to start instead of
  falling back to something weaker.
- The pipe name is derived from the full path of the data directory, so two data directories give two
  independent services and a relay always finds the service for its own data.
- Threat model: other OS users and the network are excluded. Processes running as the same user are
  trusted, as they are today (they can already read the key store and the data directory).

### Wire protocol **(designer)**

Plain MCP (the same newline-delimited JSON-RPC used over stdio) flows over the pipe. The service runs
one MCP server session per connection; all sessions share the same singleton memory services.

Because the wire is plain MCP, a relay and a service from different releases interoperate. That
property matters for upgrades (below) and must be preserved: no private framing that both sides have
to agree on.

### The relay

- Forwards client stdin to the pipe and pipe output to client stdout.
- **Survives a service restart.** It remembers the client's `initialize` request and `initialized`
  notification. When the connection drops it reconnects (starting the service if needed), replays that
  handshake to the new service session, and discards the replayed response. Requests that were in
  flight when the connection dropped are answered with a JSON-RPC error so the client is never left
  waiting. The memory tools are stateless per call, so nothing else needs restoring.
- **It is a pure forwarder (designer).** It does not answer `initialize` or `tools/list` from its own
  binary; the service answers them. That keeps a relay and a service from different releases
  interoperable, which the upgrade design depends on. The cost is handled on the service side: the
  service starts listening early (see "The service"), so the handshake is answered quickly even while
  the store is still starting.
- If the service cannot be reached or started within a bounded time: a pending `initialize` is answered
  with a JSON-RPC error and the reason goes to stderr; tool calls on an established session fail with
  a clear message saying so. The relay never opens the database itself: one process owns the store.
- **Deliberate stop (designer).** After `stop`, the service stays down until `start` or the next
  install; relays must not bring it back. While it is held down, tool calls fail with a message that
  says the service was stopped on purpose and how to start it, and relays reconnect on their own once
  it is running again. The marker that records the stop lives in the data directory, so the launcher
  honours it too (a logon start must not undo a stop). This is what makes maintenance on the database
  possible without closing agent sessions.
- **Shared mode switched off (designer).** Relays that are already running cannot turn into standalone
  servers. After `install -Shared:$false` their tool calls fail with a message saying shared mode was
  switched off and the session must be restarted; they must not start a service. Switching shared mode
  off is the one operation that does need sessions restarted, and the install script says so.
- Exits when the client closes stdin.

### The service

- Single instance per data directory. A second `serve` for the same data directory detects the first
  and exits cleanly; several relays racing to start it must end up with exactly one.
- Runs today's startup sequence once (SQLCipher conversion check, store initialisation, embedder
  health check). It accepts connections and answers the MCP handshake and `tools/list` before that
  sequence has finished; tool calls wait for it, up to a bound, and fail with the startup error if it
  failed. A normal start takes about 2.5 s on the owner's data, but a first start after an engine or
  index change took 13 to 30 s, longer than some clients wait for `initialize`. **(designer)**
- **Calls are serialised.** `SqliteVecMemoryStore` holds one `SqliteConnection` and no lock; today that
  is safe only because each session has its own process. In the service, tool calls from different
  sessions must not run concurrently against the store. The first version puts one gate around tool
  execution in the server project. A finer gate that lets embedding calls overlap would need Core
  changes and is out of scope. **(designer)**
- **Parallelism is a setting, default 1. (owner)** `MemoryMcp:Shared:MaxParallelCalls` says how many
  tool calls may run at once. With 1 the service has one store and one gate, as above. With N > 1 the
  service holds N independent store instances, each with its own connection, and a call takes a free
  one; no store instance ever runs two calls at once. This needs no Core change: it is what several
  standalone processes on one database do today, including the possibility of an occasional
  "database busy" from SQLite, which the service must surface as a clear, retryable tool error instead
  of crashing a session. Startup work that must happen once per database (conversion, rebuilds,
  backfill) runs on the first instance before the others open. Values above 1 are not expected to help
  until the embedder serves more than one request at a time (Ollama loads the model with one parallel
  slot today, and SQLite allows one writer regardless), so 1 is the supported default and higher values
  are for measuring.
- **Queue wait is measured. (owner)** For every tool call the service records how long it waited for a
  free store and how long it ran. `status` reports, since the service started: the number of calls, and
  the average, 95th percentile and maximum of both the wait and the run time, plus the current queue
  depth. A call that waited longer than a threshold (builder's choice, around one second) is also
  written to the log with the tool name. No query text or memory content goes into these records.
- Runs as the logged-in user, never as a system account: the database key lives in DPAPI (Windows),
  the Keychain (macOS) or libsecret (Linux), all bound to the user's session.
- Writes a rolling log file under the data directory; it has no console.
- Stops gracefully on `stop` and on normal OS session end.

### Starting the service

The relay must be able to start the service when it is not running, and the service must outlive the
client that triggered the start.

That second requirement rules out a plain child process on Windows: coda runs inside the Cortex
Bridge's job object (kill-on-close, no breakaway), so a service spawned by a coda session would be
killed on the next Bridge restart and take memory away from every other client. Cortex's own
self-update solves the same problem with a scheduled task.

One abstraction with a per-OS implementation **(designer)**:

| OS | Registered launcher (set up by the install script) | Fallback when not registered |
|---|---|---|
| Windows | Per-user scheduled task: runs at logon and on demand, only while the user is logged on, no admin rights | Detached process |
| Linux | systemd user unit, when a systemd user manager is available | Detached process in its own session |
| macOS | launchd LaunchAgent | Detached process in its own session |

- The relay asks the registered launcher to start the service; if none is registered it falls back to
  a detached process. On Windows the fallback may be killed with its job object; the relay's reconnect
  logic then starts it again, so the fallback degrades instead of failing.
- The registered launcher also starts the service at login, so the first session of the day does not
  pay the start-up cost.

### Install and upgrade **(designer)**

- `scripts/install.ps1 -Shared` and `scripts/install.sh --shared` switch shared mode on and register
  the launcher; the negative form unregisters it and returns to standalone. Without the flag the
  scripts behave as today.
- **An upgrade is one service restart, with agent sessions left open.** On Windows running relays lock
  the files they were started from, so the install layout separates what clients launch from what the
  service runs:
  - `bin\` is what client configs point at (unchanged path). It is refreshed when no relay holds it;
    if it is in use the script says so and leaves it, which is safe because the wire is plain MCP.
  - The service runs from a versioned directory. An upgrade publishes a new one, repoints the
    launcher, stops the old service and starts the new one. Relays reconnect on their own.
  - Old service directories are cleaned up on a later install.
- The upgrade must not need any agent session closed. That is the acceptance test for this section.

## Constraints

- .NET 10; the repository's conventions (xUnit v3, inline package versions, no warnings-as-errors).
- `ModelContextProtocol` 1.0.0 is the MCP SDK in use. It exposes `StreamServerTransport(Stream, Stream, …)`
  and `McpServer.Create(ITransport, McpServerOptions, ILoggerFactory, IServiceProvider)`, so one server
  session per pipe connection is supported.
- The server project links `SQLite3MC.PCLRaw.bundle`; that stays.
- Existing data must keep working: the same data directory, key store and database, no migration.

## Verification

- Unit tests for the relay's handshake replay and in-flight error handling, for single-instance
  behaviour, for the call gate (two sessions calling at once never overlap in the same store instance;
  with `MaxParallelCalls` = 2 two calls do overlap and a third waits), for the wait and run-time
  measurements, and for launcher selection per OS.
- Integration tests over a real pipe between a service and relays inside the test run.
- End to end on Windows on the owner's machine: enable shared mode, all four client types work with
  their existing configs, exactly one process holds `memory.db`, another OS account cannot connect,
  restarting the service does not break an open session, an upgrade needs no session closed, and after
  `stop` the service stays down while open sessions report it and recover after `start`.
- End to end on Linux in WSL (a systemd user session is available there): the systemd user unit path
  and the detached fallback.
- macOS cannot be run from the owner's machine. It is designed and unit-tested through the launcher
  abstraction but ships unverified; the README must say so. **(owner: testing on Windows and WSL only
  is accepted)**
- Cortex: because Core is untouched, no Cortex run is required. coda is verified as one of the four
  clients above.

## Left to the builder

- Names: the setting keys, the pipe name scheme, the scheduled task / unit / agent names, the versioned
  directory layout, log file naming and retention.
- The mechanism for graceful stop and for single-instance detection.
- Timeouts and retry pacing for connect, start and reconnect.
- Whether the gate lives in the tool class, a decorator, or the session host.
- How the relay parses just enough JSON-RPC to track the handshake and in-flight request ids.
- README and install-script wording.
