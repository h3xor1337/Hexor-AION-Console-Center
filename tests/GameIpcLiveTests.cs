using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Hexor.Runtime;

// End-to-end harness with a REAL game process: spawns an AION2.exe copy, lets the real
// GameIpcManager host its per-PID pipe, connects an in-game-style client, streams a status
// update and reads back a StopAll command.
internal static class GameIpcLiveTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        RunAsync(check).GetAwaiter().GetResult();
    }

    private static async Task RunAsync(Action<bool, string> check)
    {
        var exe = Path.Combine(Path.GetTempPath(), "hexor-fake-game", "AION2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.Copy(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\cmd.exe", exe, overwrite: true);
        var start = new ProcessStartInfo(exe, "/k title hexor-fake-game") { CreateNoWindow = true, UseShellExecute = false };
        using var game = Process.Start(start) ?? throw new InvalidOperationException("fake game did not start");
        try
        {
            // Phase 1: the service alone, real per-PID pipe name, logs on the console.
            // Skipped when the environment sets HEXOR_GAMEIPC_SKIP_PROBE=1 (manager-only diagnosis).
            var probe = Environment.GetEnvironmentVariable("HEXOR_GAMEIPC_SKIP_PROBE") == "1" ? null : new GameIpcService(game.Id);
            if (probe != null)
            {
                probe.OnLog += message => Console.WriteLine("  [svc " + game.Id + "] " + message);
                probe.OnClientConnected += () => Console.WriteLine("  [svc " + game.Id + "] client connected");
                probe.OnClientDisconnected += () => Console.WriteLine("  [svc " + game.Id + "] client disconnected");
                probe.Start();
            }
            NamedPipeClientStream? directClient = probe == null ? null : new NamedPipeClientStream(".", $"Aion2_IPC_{game.Id}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                if (directClient != null)
                {
                    await directClient.ConnectAsync(8000);
                Console.WriteLine("  [client] connected");
                WriteFrame(directClient, GameIpcMessageType.Heartbeat, "");
                await Task.Delay(2500);
                Console.WriteLine("  [client] service.ClientConnected=" + probe.ClientConnected);
                    WriteFrame(directClient, GameIpcMessageType.StatusUpdate,
                        "{\"name\":\"HarnessHero\",\"level\":55,\"mapId\":42,\"isInGame\":true,\"isCombat\":true,\"dungeonStage\":\"\",\"dungeonPhase\":\"\",\"dungeonMechanic\":\"\",\"activity\":\"combat\",\"serverId\":2301,\"race\":0,\"classId\":1}");
                    await Task.Delay(1500);
                }
            }
            catch (Exception e) { Console.WriteLine("  [client] failed: " + e.GetType().Name + ": " + e.Message); }
            probe?.Stop();
            try { directClient?.Dispose(); } catch { }

            // Phase 2: the manager path.
            using var manager = new GameIpcManager();
            manager.OnLog += message => Console.WriteLine("  [mgr-log] " + message);
            manager.Start();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (manager.Sessions().Any(s => s.ProcessId == game.Id && s.PipeOwnedByHexor)) break;
                await Task.Delay(250);
            }
            var service = manager.Sessions().FirstOrDefault(s => s.ProcessId == game.Id);
            check(service != null && service.PipeOwnedByHexor, $"The manager hosted the game process pipe (pid {game.Id})");

            using var client = new NamedPipeClientStream(".", $"Aion2_IPC_{game.Id}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(8000);
                Console.WriteLine("  [client2] connected");
                WriteFrame(client, GameIpcMessageType.Heartbeat, "");
                Console.WriteLine("  [client2] heartbeat written");
            }
            catch (Exception e) { Console.WriteLine("  [client2] connect/write failed: " + e.GetType().Name + ": " + e.Message); }
            if (!await WaitUntil(() => manager.Sessions().Any(s => s.ProcessId == game.Id && s.ClientConnected)))
            {
                foreach (var s in manager.Sessions()) Console.WriteLine("  [mgr] " + s.Describe() + " owned=" + s.PipeOwnedByHexor + " connected=" + s.ClientConnected);
                Console.WriteLine("  [mgr] counts=" + manager.Counts());
            }
            check(await WaitUntil(() => manager.Sessions().Any(s => s.ProcessId == game.Id && s.ClientConnected)),
                "The manager marks the game session connected once the client dials in");
            var statusJson = "{\"name\":\"HarnessHero\",\"level\":55,\"mapId\":42,\"isInGame\":true," +
                "\"isCombat\":true,\"dungeonStage\":\"\",\"dungeonPhase\":\"\",\"dungeonMechanic\":\"\"," +
                "\"activity\":\"combat\",\"serverId\":2301,\"race\":0,\"classId\":1}";
            WriteFrame(client, GameIpcMessageType.StatusUpdate, statusJson);
            check(await WaitUntil(() =>
            {
                var snapshot = manager.Sessions().FirstOrDefault(s => s.ProcessId == game.Id);
                return snapshot != null && snapshot.Name == "HarnessHero";
            }), "The manager updated the live snapshot from the streamed status");

            var command = ReadCommandAsync(client);
            await manager.SendCommandAsync(game.Id, "StopAll");
            var received = await command.WaitAsync(TimeSpan.FromSeconds(10));
            check(received.Contains("\"command\":\"StopAll\"", StringComparison.Ordinal),
                "StopAll reached the in-game client over the manager-hosted pipe");

            // Captcha auto-resolve round trip: the in-game client sends a CaptchaRequest, the
            // solver answers, and the client receives the SubmitCaptchaAnswer command back.
            using var solver = new CaptchaAutoSolver(manager, (_, _) => Task.FromResult("8899"), keyOverride: "fixture");
            var answer = ReadCommandAsync(client);
            WriteFrame(client, GameIpcMessageType.CaptchaRequest,
                "{\"requestId\":\"cap-1\",\"imageBase64\":\"aGVsbG8=\",\"mimeType\":\"image/png\"}");
            var answerJson = await answer.WaitAsync(TimeSpan.FromSeconds(15));
            check(answerJson.Contains("\"command\":\"SubmitCaptchaAnswer\"", StringComparison.Ordinal) &&
                answerJson.Contains("\"requestId\":\"cap-1\"", StringComparison.Ordinal) &&
                answerJson.Contains("\"answer\":\"8899\"", StringComparison.Ordinal),
                "A captcha challenge is solved and the answer returns on the game pipe");
            WriteFrame(client, GameIpcMessageType.CaptchaOutcome,
                "{\"requestId\":\"cap-1\",\"success\":true,\"message\":\"SUCCESS\"}");
            check(await WaitUntil(() => solver.Solved == 1),
                "The game's captcha outcome is counted by the auto-resolver");
        }
        finally
        {
            try { game.Kill(true); } catch { }
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private static async Task<string> ReadCommandAsync(NamedPipeClientStream client)
    {
        var header = new byte[8];
        await ReadExactlyAsync(client, header);
        int length = BitConverter.ToInt32(header, 0);
        var payload = new byte[length];
        if (length > 0) await ReadExactlyAsync(client, payload);
        return Encoding.UTF8.GetString(payload);
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
}
