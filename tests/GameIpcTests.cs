using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hexor.Runtime;

internal static class GameIpcTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        StatusParsing(check);
        Loopback(check).GetAwaiter().GetResult();
    }

    private static void StatusParsing(Action<bool, string> check)
    {
        var status = JsonSerializer.Deserialize<GameStatusData>("""
            {"name":"Hero","race":0,"classId":3,"level":62,"mapId":310,
             "isCombat":false,"isGather":true,"isAutoTask":false,"isDungeon":false,
             "dungeonStage":"","dungeonPhase":"","dungeonMechanic":"",
             "isInGame":true,"serverId":2301,"activity":"gathering"}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(status != null && status.Name == "Hero" && status.Level == 62 && status.MapId == 310 &&
            status.IsGather && status.IsInGame && status.ServerId == 2301,
            "The in-game status payload deserializes with camelCase properties");
        var snapshot = new GameSessionSnapshot(4242, true, true, true, true, status!.Name, status.Level, status.MapId,
            status.ServerId, status.IsCombat, status.IsGather, status.IsAutoTask, status.IsDungeon,
            status.DungeonStage, status.DungeonPhase, status.DungeonMechanic, status.Activity,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        check(snapshot.ActivityState == "gathering" &&
            snapshot.Describe().Contains("Hero", StringComparison.Ordinal) &&
            snapshot.Describe().Contains("Lv 62", StringComparison.Ordinal),
            "Session snapshots describe the character, level, map and activity");

        var dungeon = JsonSerializer.Deserialize<GameStatusData>("""
            {"name":"Hero","isDungeon":true,"dungeonStage":"Nightmare","dungeonPhase":"boss",
             "isInGame":true,"level":70,"mapId":55}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var dungeonSnapshot = new GameSessionSnapshot(99, true, true, true, true, dungeon!.Name, dungeon.Level, dungeon.MapId,
            dungeon.ServerId, dungeon.IsCombat, dungeon.IsGather, dungeon.IsAutoTask, dungeon.IsDungeon,
            dungeon.DungeonStage, dungeon.DungeonPhase, dungeon.DungeonMechanic, dungeon.Activity,
            DateTimeOffset.UtcNow, null);
        check(dungeonSnapshot.ActivityState.Contains("dungeon", StringComparison.Ordinal) &&
            dungeonSnapshot.ActivityState.Contains("Nightmare", StringComparison.Ordinal) &&
            dungeonSnapshot.ActivityState.Contains("boss", StringComparison.Ordinal),
            "Dungeon sessions report stage and phase in the activity state");
    }

    // Full loopback against an in-process fixture server that speaks the verified per-PID wire
    // contract: 8-byte header (int32 length + int32 message type) plus payload.
    private static async Task Loopback(Action<bool, string> check)
    {
        var pipeName = "HexorGameIpcFixture_" + Guid.NewGuid().ToString("N");
        using var service = new GameIpcService(4242, pipeName);
        var statusSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GameSessionSnapshot? received = null;
        service.OnMessage += (type, payload) =>
        {
            if (type != GameIpcMessageType.StatusUpdate) return;
            received = JsonSerializer.Deserialize<GameStatusData>(Encoding.UTF8.GetString(payload), new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } s
                ? new GameSessionSnapshot(4242, true, true, true, s.IsInGame, s.Name, s.Level, s.MapId, s.ServerId,
                    s.IsCombat, s.IsGather, s.IsAutoTask, s.IsDungeon, s.DungeonStage, s.DungeonPhase, s.DungeonMechanic,
                    s.Activity, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                : null;
            statusSeen.TrySetResult();
        };
        service.Start();

        // The in-game component plays the client role.
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        var statusJson = """
            {"name":"Hero","level":62,"mapId":310,"isInGame":true,"isGather":true,
             "dungeonStage":"","dungeonPhase":"","dungeonMechanic":"","activity":"gathering","serverId":2301}
            """;
        WriteFrame(client, GameIpcMessageType.StatusUpdate, statusJson);
        await statusSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(received != null && received!.Name == "Hero" && received.Level == 62 && received.MapId == 310 &&
            received.IsGather && received.InGame,
            "The direct game IPC server receives and maps the in-game status update");
        check(received!.Describe().Contains("PID 4242", StringComparison.Ordinal),
            "Direct sessions describe their owning game process id");

        // The host sends an operator control; the in-game client reads the exact wire shape.
        var commandSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            var header = new byte[8];
            await ReadExactlyAsync(client, header);
            int length = BitConverter.ToInt32(header, 0);
            var type = (GameIpcMessageType)BitConverter.ToInt32(header, 4);
            var payload = new byte[length];
            if (length > 0) await ReadExactlyAsync(client, payload);
            if (type == GameIpcMessageType.Command)
                commandSeen.TrySetResult(Encoding.UTF8.GetString(payload));
        });
        await service.SendCommandAsync("StopAll");
        var command = await commandSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(command.Contains("\"command\":\"StopAll\"", StringComparison.Ordinal) &&
            command.Contains("\"args\":", StringComparison.Ordinal),
            "Operator stop commands carry the documented command/args JSON shape");
        check(service.PipeOwnedByHexor && service.ClientConnected,
            "The fixture server reports ownership and a connected client");
    }

    private static void WriteFrame(PipeStream stream, GameIpcMessageType type, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(payload.Length).CopyTo(header, 0);
        BitConverter.GetBytes((int)type).CopyTo(header, 4);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    private static async Task ReadExactlyAsync(PipeStream stream, Memory<byte> target)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await stream.ReadAsync(target[read..]);
            if (count == 0) throw new EndOfStreamException("fixture stream ended");
            read += count;
        }
    }
}
