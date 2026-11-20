using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hexor.Runtime;

// Direct host for the in-game client DLL transport (Aion2_IPC_<pid>).
// The installed in-game component (dxgi.dll inside the game process) connects to this pipe as a
// client and streams Heartbeat/StatusUpdate/Log/Captcha messages. The vendor host (LegionCommander)
// creates the same per-PID server with its IpcService; this implementation mirrors the verified
// wire contract so Hexor can observe the game and send operator controls without that host:
//   - single pipe instance, bidirectional, byte mode, asynchronous;
//   - frame = 8-byte header: int32 payload length + int32 IpcMessageType, then the payload;
//   - payload length must be 0..10485760 (10 MiB) exactly as the vendor host enforces;
//   - StatusUpdate payload is UTF-8 JSON GameStatusData (camelCase properties);
//   - Command payload is UTF-8 JSON {"command": string, "args": {...}}.
// Hexor observes game state and sends explicit operator-initiated controls only (StartAutoTask,
// StopAll, RefreshCurrentStatus, ApplyDefaultConfig), always from a visible button the operator
// clicks (or from the settings-repair follow-up after a file repair). It never starts automation
// on its own, never touches game memory, and never reads or writes challenge content.
public enum GameIpcMessageType
{
    Heartbeat = 1,
    StatusUpdate = 2,
    Command = 3,
    Log = 4,
    CaptchaRequest = 5,
    CaptchaOutcome = 6,
    RuntimeRequest = 7,
    RuntimeResponse = 8,
    RuntimeEvent = 9,
    RuntimeCancel = 10,
    RuntimeFlowControl = 11,
}

public static class GameIpcContract
{
    public const int MaxPayloadBytes = 10485760;
    public const int HeaderBytes = 8;
    public static string PipeNameFor(int processId) => $"Aion2_IPC_{processId}";
}

public sealed record GameStatusData(
    string Name,
    int? Race,
    int? ClassId,
    int Level,
    int MapId,
    bool IsCombat,
    bool IsGather,
    bool IsAutoTask,
    bool IsDungeon,
    string DungeonStage,
    string DungeonPhase,
    string DungeonMechanic,
    bool IsInGame,
    int ServerId,
    string Activity);

public sealed record GameSessionSnapshot(
    int ProcessId,
    bool PipeOwnedByHexor,
    bool ClientConnected,
    bool StatusSeen,
    bool InGame,
    string Name,
    int Level,
    int MapId,
    int ServerId,
    bool IsCombat,
    bool IsGather,
    bool IsAutoTask,
    bool IsDungeon,
    string DungeonStage,
    string DungeonPhase,
    string DungeonMechanic,
    string Activity,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastStatusAt)
{
    // The live-control label mirrors the bridge session wording so both sources read the same way.
    public string ActivityState =>
        IsDungeon ? ("dungeon " + (DungeonStage.Length > 0 ? DungeonStage + " " : "") + DungeonPhase).Trim()
        : IsAutoTask ? "auto task"
        : IsCombat ? "combat"
        : IsGather ? "gathering"
        : Activity.Length > 0 ? Activity
        : "idle";

    public string Describe() =>
        !ClientConnected ? $"PID {ProcessId}: in-game client not connected yet"
        : !StatusSeen ? $"PID {ProcessId}: connected, waiting for first status"
        : !InGame ? $"PID {ProcessId}: connected, not logged in to a character"
        : $"PID {ProcessId}: {Name} · Lv {Level} · map {MapId} · {ActivityState}";
}

// Per-process pipe server. Framing, reconnect cadence and error handling follow the vendor's
// IpcService so the in-game component cannot tell the difference.
public sealed class GameIpcService : IDisposable
{
    private readonly string pipeName;
    private NamedPipeServerStream? server;

    private CancellationTokenSource? cancellation;
    private readonly object gate = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private volatile bool started;
    private volatile bool pipeOwnedByHexor;
    private volatile bool clientConnected;

    public string PipeName => pipeName;
    public int ProcessId { get; }
    public bool PipeOwnedByHexor => pipeOwnedByHexor;
    public bool ClientConnected => clientConnected;
    public event Action<GameIpcMessageType, byte[]>? OnMessage;
    public event Action? OnClientConnected;
    public event Action? OnClientDisconnected;
    public event Action<string>? OnLog;

    // Test seam: fixture tests run against a private pipe name instead of the real per-PID name.
    public GameIpcService(int processId, string? pipeNameOverride = null)
    {
        ProcessId = processId;
        pipeName = pipeNameOverride ?? GameIpcContract.PipeNameFor(processId);
    }

    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;
            cancellation = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cancellation.Token));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!started) return;
            started = false;
            cancellation?.Cancel();
            try { server?.Dispose(); } catch { }
            server = null;
        }
    }

    public async Task SendAsync(GameIpcMessageType type, byte[] payload)
    {
        var stream = server;
        if (stream == null || !stream.IsConnected) return;
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (server == null || !server.IsConnected) return;
            var header = new byte[GameIpcContract.HeaderBytes];
            BitConverter.GetBytes(payload.Length).CopyTo(header, 0);
            BitConverter.GetBytes((int)type).CopyTo(header, 4);
            await server.WriteAsync(header, CancellationToken.None).ConfigureAwait(false);
            if (payload.Length > 0)
                await server.WriteAsync(payload, CancellationToken.None).ConfigureAwait(false);
            await server.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            OnLog?.Invoke("send failed: " + e.Message);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public Task SendCommandAsync(string command, object? args = null) =>
        SendAsync(GameIpcMessageType.Command, Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["command"] = command, ["args"] = args ?? new { } }, JsonOptions.Default)));

    // Runtime RPC: the same RuntimeRequest/RuntimeResponse frames the vendor console sends over
    // this pipe. The in-game runtime serves method calls (commander.command among them), which
    // is how the vendor console starts and stops the daily activity machines. Requests are
    // correlated by requestId; responses arrive as RuntimeResponse frames.
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> runtimeCalls = new();
    private int runtimeCallLeak;
    // The in-game runtime requires a stable session id per connection; the vendor console uses
    // one generated id per account session. Hexor keeps one per pipe connection.
    private string runtimeSessionId = Guid.NewGuid().ToString("N");

    public async Task<JsonElement> SendRuntimeRequestAsync(string method, object? parameters, TimeSpan? timeout = null, CancellationToken? stop = null)
    {
        var stream = server;
        if (stream == null || !stream.IsConnected || !clientConnected)
            throw new IOException("No in-game client connected for a runtime request.");
        var requestId = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) runtimeCalls[requestId] = source;
        try
        {
            var budget = timeout ?? TimeSpan.FromSeconds(10);
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["requestId"] = requestId,
                ["runtimeSessionId"] = runtimeSessionId,
                ["method"] = method,
                ["params"] = parameters ?? new { },
                ["deadlineUnixMs"] = DateTimeOffset.UtcNow.Add(budget).ToUnixTimeMilliseconds(),
                ["processId"] = ProcessId,
            }, JsonOptions.Default);
            await SendAsync(GameIpcMessageType.RuntimeRequest, Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop ?? CancellationToken.None);
            linked.CancelAfter(budget);
            await using (linked.Token.Register(() => source.TrySetCanceled(linked.Token)))
                return await source.Task.ConfigureAwait(false);
        }
        finally { lock (gate) runtimeCalls.Remove(requestId); }
    }

    private void CompleteRuntimeResponse(byte[] payload)
    {
        try
        {
            var response = JsonSerializer.Deserialize<JsonObject>(payload, JsonOptions.Default);
            var requestId = response?["requestId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(requestId)) return;
            TaskCompletionSource<JsonElement>? source;
            lock (gate) runtimeCalls.TryGetValue(requestId, out source);
            if (source == null) return;
            var status = response["status"]?.GetValue<string>();
            var result = response["result"];
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && result is JsonNode && !result.ToString().Equals("null", StringComparison.Ordinal))
                source.TrySetResult(JsonSerializer.SerializeToElement(result, JsonOptions.Default));
            else
            {
                var message = response["error"] is JsonObject error
                    ? error["message"]?.GetValue<string>() ?? "runtime error"
                    : status ?? "runtime error";
                source.TrySetException(new IOException("Runtime request failed: " + message));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            if (Interlocked.Increment(ref runtimeCallLeak) < 5) OnLog?.Invoke("runtime response parse failed: " + e.Message);
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        OnLog?.Invoke("loop started");
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreateServer();
                if (stream == null)
                {
                    // Another host (LegionCommander) already owns this per-PID pipe. Do not retry:
                    // the vendor host serves this PID and Hexor stays in observe-only mode for it.
                    pipeOwnedByHexor = false;
                    OnLog?.Invoke("pipe already hosted by another process; observing only");
                    return;
                }
                pipeOwnedByHexor = true;
                server = stream;
                OnLog?.Invoke("server instance created; waiting for the in-game client");
                await stream.WaitForConnectionAsync(stop).ConfigureAwait(false);
                clientConnected = true;
                OnLog?.Invoke("client connection accepted");
                OnClientConnected?.Invoke();
                var header = new byte[GameIpcContract.HeaderBytes];
                while (stream.IsConnected && !stop.IsCancellationRequested)
                {
                    if (!await ReadExactlyAsync(stream, header, stop).ConfigureAwait(false)) break;
                    int length = BitConverter.ToInt32(header, 0);
                    var frameType = (GameIpcMessageType)BitConverter.ToInt32(header, 4);
                    if (frameType != GameIpcMessageType.Heartbeat)
                        OnLog?.Invoke($"frame {frameType} length={length}");
                    var type = frameType;
                    if (length < 0 || length > GameIpcContract.MaxPayloadBytes)
                    {
                        OnLog?.Invoke($"invalid payload length {length}; dropping connection");
                        break;
                    }
                    var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                    if (length > 0 && !await ReadExactlyAsync(stream, payload, stop).ConfigureAwait(false)) break;
                    if (type == GameIpcMessageType.RuntimeResponse) CompleteRuntimeResponse(payload);
                    OnMessage?.Invoke(type, payload);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException e)
            {
                OnLog?.Invoke("io error: " + e.Message);
            }
            catch (ObjectDisposedException e)
            {
                OnLog?.Invoke("disposed: " + e.Message);
                break;
            }
            catch (Exception e)
            {
                OnLog?.Invoke("loop error: " + e.Message);
            }
            finally
            {
                clientConnected = false;
                server = null;
                try { if (stream != null && stream.IsConnected) stream.Disconnect(); } catch { }
                try { stream?.Dispose(); } catch { }
                OnClientDisconnected?.Invoke();
                if (!stop.IsCancellationRequested)
                    await Task.Delay(1000, stop).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream? CreateServer()
    {
        try
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<bool> ReadExactlyAsync(PipeStream stream, Memory<byte> target, CancellationToken stop)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await stream.ReadAsync(target[read..], stop).ConfigureAwait(false);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }

    public void Dispose() => Stop();
}

// Watches for the AION2 game process and hosts one per-PID pipe server for each. The manager owns
// snapshot aggregation so the UI can bind to a stable list of sessions.
public sealed class GameIpcManager : IDisposable
{
    public const string GameProcessName = "AION2";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object gate = new();
    private readonly Dictionary<int, GameIpcService> services = new();
    private readonly Dictionary<int, GameSessionSnapshot> snapshots = new();
    private CancellationTokenSource? cancellation;
    private volatile bool running;
    public event Action? Changed;
    public event Action<string>? OnLog;
    public event Action<int, string, string, string?>? CaptchaRequestReceived;
    public event Action<int, string, bool, string>? CaptchaOutcomeReceived;

    private void RaiseServiceLog(int pid, string message)
    {
        try { OnLog?.Invoke($"pid {pid}: {message}"); } catch { }
    }

    public void Start()
    {
        lock (gate)
        {
            if (running) return;
            running = true;
            cancellation = new CancellationTokenSource();
            _ = Task.Run(() => WatchAsync(cancellation.Token));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
            cancellation?.Cancel();
            foreach (var service in services.Values) service.Dispose();
            services.Clear();
        }
    }

    public IReadOnlyList<GameSessionSnapshot> Sessions()
    {
        lock (gate) return snapshots.Values.OrderBy(s => s.ProcessId).ToArray();
    }

    public (int hosted, int connected) Counts()
    {
        lock (gate) return (snapshots.Count, snapshots.Values.Count(s => s.ClientConnected));
    }

    public string Describe()
    {
        var sessions = Sessions();
        if (sessions.Count == 0) return "Game is not running. Hexor watches for AION2 and connects automatically when it starts.";
        var connected = sessions.Where(s => s.ClientConnected).ToArray();
        if (connected.Length == 0)
            return string.Join("  |  ", sessions.Select(s => s.Describe()));
        return $"Live game connection: {connected.Length} session(s).  " + string.Join("  |  ", connected.Select(s => s.Describe()));
    }

    public async Task SendCommandAsync(int processId, string command, object? args = null)
    {
        GameIpcService? service;
        lock (gate) services.TryGetValue(processId, out service);
        if (service == null || !service.PipeOwnedByHexor || !service.ClientConnected)
            throw new IOException($"No direct game IPC connection for PID {processId}.");
        await service.SendCommandAsync(command, args).ConfigureAwait(false);
    }

    // Runtime RPC pass-through to the per-PID pipe the manager hosts for this game process.
    public async Task<JsonElement> SendRuntimeRequestAsync(int processId, string method, object? parameters, TimeSpan? timeout = null, CancellationToken? stop = null)
    {
        GameIpcService? service;
        lock (gate) services.TryGetValue(processId, out service);
        if (service == null || !service.PipeOwnedByHexor || !service.ClientConnected)
            throw new IOException($"No direct game IPC connection for PID {processId}.");
        return await service.SendRuntimeRequestAsync(method, parameters, timeout, stop).ConfigureAwait(false);
    }

    private async Task WatchAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var current = new HashSet<int>();
                foreach (var process in Process.GetProcessesByName(GameProcessName))
                {
                    // Capture the pid: the Process handle is disposed below, and the service
                    // callbacks must never dereference it afterwards.
                    var pid = process.Id;
                    using (process)
                    {
                        if (pid <= 0) continue;
                        current.Add(pid);
                        bool added = false;
                        GameIpcService? service;
                        lock (gate)
                        {
                            if (!services.ContainsKey(pid))
                            {
                                service = new GameIpcService(pid);
                                service.OnMessage += (type, payload) => HandleMessage(pid, type, payload);
                                service.OnClientConnected += () => UpdateConnectionState(pid, true, "client connected");
                                service.OnClientDisconnected += () => UpdateConnectionState(pid, false, "client disconnected");
                                service.OnLog += message => { Diagnostics.TryLog("game-ipc", $"pid {pid}: {message}"); RaiseServiceLog(pid, message); };
                                services[pid] = service;
                                added = true;
                            }
                            else service = services[pid];
                        }
                        if (added)
                        {
                            Diagnostics.TryLog("game-ipc", $"pid {pid}: hosting {GameIpcContract.PipeNameFor(pid)}");
                            service.Start();
                        }
                        lock (gate)
                        {
                            if (!snapshots.TryGetValue(pid, out var snapshot))
                                snapshots[pid] = new GameSessionSnapshot(pid, service.PipeOwnedByHexor, service.ClientConnected, false, false, "", 0, 0, 0, false, false, false, false, "", "", "", "", DateTimeOffset.UtcNow, null);
                            else if (snapshot.PipeOwnedByHexor != service.PipeOwnedByHexor || snapshot.ClientConnected != service.ClientConnected)
                                snapshots[pid] = snapshot with { PipeOwnedByHexor = service.PipeOwnedByHexor, ClientConnected = service.ClientConnected };
                        }
                    }
                }
                var dead = new List<int>();
                lock (gate)
                {
                    foreach (var pid in services.Keys.Where(pid => !current.Contains(pid))) dead.Add(pid);
                    foreach (var pid in dead)
                    {
                        services.Remove(pid);
                        snapshots.Remove(pid);
                    }
                }                foreach (var pid in dead)
                {
                    Diagnostics.TryLog("game-ipc", $"pid {pid}: game exited; pipe released");
                    RaiseChanged(pid, "game exited");
                }
            }
            catch (Exception e)
            {
                Diagnostics.TryLog("game-ipc", "watch error: " + e.GetType().Name);
            }
            try { await Task.Delay(PollInterval, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void UpdateConnectionState(int pid, bool connected, string reason)
    {
        lock (gate)
        {
            if (snapshots.TryGetValue(pid, out var snapshot))
                snapshots[pid] = snapshot with { ClientConnected = connected, PipeOwnedByHexor = true };
        }
        RaiseChanged(pid, reason);
    }

    private void RaiseChanged(int pid, string reason)
    {
        Diagnostics.TryLog("game-ipc", $"pid {pid}: {reason}");
        try { Changed?.Invoke(); } catch { }
    }

    private void HandleMessage(int pid, GameIpcMessageType type, byte[] payload)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (type == GameIpcMessageType.StatusUpdate)
        {
            GameStatusData? status = null;
            try
            {
                status = JsonSerializer.Deserialize<GameStatusData>(Encoding.UTF8.GetString(payload), JsonOptions.Default);
            }
            catch (Exception e)
            {
                Diagnostics.TryLog("game-ipc", $"pid {pid}: status parse failed ({e.GetType().Name})");
            }
            lock (gate)
            {
                var previous = snapshots.TryGetValue(pid, out var snapshot) ? snapshot : null;
                snapshots[pid] = status == null
                    ? previous ?? new GameSessionSnapshot(pid, true, true, false, false, "", 0, 0, 0, false, false, false, false, "", "", "", "", now, null)
                    : new GameSessionSnapshot(pid, true, true, true, status.IsInGame, status.Name, status.Level, status.MapId, status.ServerId,
                        status.IsCombat, status.IsGather, status.IsAutoTask, status.IsDungeon, status.DungeonStage, status.DungeonPhase, status.DungeonMechanic,
                        status.Activity, now, now);
            }
            if (status != null)
                Diagnostics.TryLog("game-ipc", $"pid {pid}: status name={status.Name} level={status.Level} map={status.MapId} inGame={status.IsInGame} combat={status.IsCombat} gather={status.IsGather} autoTask={status.IsAutoTask} dungeon={status.IsDungeon}");
            Changed?.Invoke();
            return;
        }
        if (type == GameIpcMessageType.Heartbeat)
        {
            lock (gate)
            {
                if (snapshots.TryGetValue(pid, out var snapshot) && !snapshot.StatusSeen)
                    snapshots[pid] = snapshot with { LastSeenAt = now };
            }
            return;
        }
        if (type == GameIpcMessageType.Log && payload.Length > 0)
        {
            Diagnostics.TryLog("game-ipc", $"pid {pid}: game log: {Encoding.UTF8.GetString(payload)}");
            return;
        }
        if (type is GameIpcMessageType.CaptchaRequest or GameIpcMessageType.CaptchaOutcome)
        {
            if (type == GameIpcMessageType.CaptchaRequest)
            {
                CaptchaGameRequest? request = null;
                try { request = JsonSerializer.Deserialize<CaptchaGameRequest>(Encoding.UTF8.GetString(payload), JsonOptions.Default); }
                catch (Exception) { }
                if (request is { } challenge && !string.IsNullOrWhiteSpace(challenge.RequestId) && !string.IsNullOrWhiteSpace(challenge.ImageBase64))
                {
                    Diagnostics.TryLog("game-ipc", $"pid {pid}: captcha challenge {challenge.RequestId} ({challenge.ImageBase64.Length} bytes of image data)");
                    CaptchaRequestReceived?.Invoke(pid, challenge.RequestId, challenge.ImageBase64, challenge.MimeType);
                }
            }
            else
            {
                CaptchaGameOutcome? outcome = null;
                try { outcome = JsonSerializer.Deserialize<CaptchaGameOutcome>(Encoding.UTF8.GetString(payload), JsonOptions.Default); }
                catch (Exception) { }
                if (outcome is { } result && !string.IsNullOrWhiteSpace(result.RequestId) && result.Success is { } success)
                {
                    Diagnostics.TryLog("game-ipc", $"pid {pid}: captcha outcome {result.RequestId} accepted={success}");
                    CaptchaOutcomeReceived?.Invoke(pid, result.RequestId, success, result.Message ?? "");
                }
            }
            Changed?.Invoke();
        }
    }
}
