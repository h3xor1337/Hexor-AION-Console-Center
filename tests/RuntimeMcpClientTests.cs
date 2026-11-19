using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Hexor.Runtime;

internal static class RuntimeMcpClientTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Parse(check);
        Mappings(check);
        Loopback(check).GetAwaiter().GetResult();
    }

    private static void Parse(Action<bool, string> check)
    {
        var success = RuntimeMcpClient.Parse("{\"protocolVersion\":1,\"requestId\":\"r1\",\"status\":\"ok\"," +
            "\"result\":{\"sessions\":[{\"runtimeSessionId\":\"s-1\",\"accountId\":7,\"processId\":4242}]}}");
        check(success.Ok && success.Payload.GetProperty("sessions").GetArrayLength() == 1,
            "Successful bridge response parses the result payload");

        var failure = RuntimeMcpClient.Parse("{\"protocolVersion\":1,\"requestId\":\"r2\",\"status\":\"failure\"," +
            "\"error\":{\"code\":\"unsupported_protocol\",\"message\":\"Bridge protocol 99 is unsupported.\"}}");
        check(!failure.Ok && failure.Error is { } error && error.Code == "unsupported_protocol" &&
            error.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase),
            "Failure responses surface the bridge error code and message");

        check(!RuntimeMcpClient.Parse("{\"status\":\"weird\"}").Ok,
            "Unknown response status is treated as failure");
    }

    private static void Mappings(Action<bool, string> check)
    {
        var sessions = JsonDocument.Parse("""
            [
              {"runtimeSessionId":"s-1","accountId":7,"processId":4242,"controllerState":"Running",
               "ipcConnected":true,"readiness":"Ready","characterLevel":62,"mapId":310},
              {"runtimeSessionId":"s-2","accountId":9,"processId":4300,"controllerState":"PausedByExternal",
               "ipcConnected":false,"readiness":"Waiting","characterLevel":41,"mapId":0}
            ]
            """).RootElement.Clone();
        var rows = RuntimeMcpResponses.Sessions(sessions);
        check(rows.Count == 2 && rows[0].AccountId == 7 && rows[0].ProcessId == 4242 &&
            rows[0].ControllerState == "Running" && rows[0].IpcConnected && rows[0].CharacterLevel == 62 &&
            rows[1].ControllerState == "PausedByExternal" && !rows[1].IpcConnected,
            "Session rows map the vendor's session summary fields");

        var incidents = JsonDocument.Parse("""
            {"accountId":7,"incidents":[
              {"incidentId":"inc-1","status":"Open","kind":"StateStalled","summary":"state machine stalled 41000 ms",
               "claimant":null},
              {"incidentId":"inc-2","status":"Claimed","kind":"DialogBlocked","summary":"dialog pending",
               "claimant":"operator","claimToken":"tok-2"}
            ]}
            """).RootElement.Clone();
        var incidentRows = RuntimeMcpResponses.Incidents(incidents);
        check(incidentRows.Count == 2 && incidentRows[0].IncidentId == "inc-1" &&
            incidentRows[0].Summary.Contains("stalled", StringComparison.Ordinal) && incidentRows[0].Claimant == null &&
            incidentRows[1].Claimant == "operator" && incidentRows[1].ClaimToken == "tok-2",
            "Incident rows map identity, status, summary, claimant, and claim token");

        var captcha = JsonDocument.Parse("""
            {"accountId":7,"operations":[
              {"operationId":"op-1","accountId":7,"state":"Completed","detail":"SUCCESS confirmed by game",
               "updatedAtUtc":"2026-01-01T12:03:00Z"},
              {"operationId":"op-2","accountId":7,"state":"Failed","detail":"solver error"}
            ]}
            """).RootElement.Clone();
        var captchaRows = RuntimeMcpResponses.CaptchaOperations(captcha);
        check(captchaRows.Count == 2 && captchaRows[0].OperationId == "op-1" &&
            captchaRows[0].State == "Completed" && captchaRows[0].UpdatedAt != null &&
            captchaRows[1].UpdatedAt == null,
            "Captcha operation rows map state, detail, and timestamps");

        var summaries = JsonDocument.Parse("""
            [
              {"accountId":7,"gameDay":"2026-01-01","serverId":2301,"characterId":"ch-1","name":"Hero",
               "gatherSeconds":3600,"dailyKina":1250000,"dailyKinaLimit":1400000,"floorGoldFull":false,
               "combatPower":68000,"status":"Complete"}
            ]
            """).RootElement.Clone();
        var summaryRows = RuntimeMcpResponses.DailySummaries(summaries);
        check(summaryRows.Count == 1 && summaryRows[0].AccountId == 7 &&
            summaryRows[0].GameDay == "2026-01-01" && summaryRows[0].Summary.Contains("Hero", StringComparison.Ordinal) &&
            summaryRows[0].Summary.Contains("Complete", StringComparison.Ordinal) &&
            summaryRows[0].Detail.Contains("1,250,000", StringComparison.Ordinal),
            "Daily summary rows map name, status, kinah, and gather time");

        check(RuntimeMcpResponses.Sessions(JsonSerializer.SerializeToElement(new { })).Count == 0 &&
            RuntimeMcpResponses.Incidents(JsonSerializer.SerializeToElement(new { })).Count == 0,
            "Empty payloads map to empty rows without exceptions");
    }

    private const string SessionReply = "{\"protocolVersion\":1,\"requestId\":\"echo\",\"status\":\"ok\",\"result\":" +
        "{\"sessions\":[{\"runtimeSessionId\":\"s-1\",\"accountId\":7,\"processId\":4242,\"controllerState\":\"Running\"," +
        "\"ipcConnected\":true,\"readiness\":\"Ready\",\"characterLevel\":62,\"mapId\":310}]}}";

    // Full client against an in-process fake bridge that speaks the verified wire contract:
    // 4-byte little-endian length framing with UTF-8 JSON on both directions.
    private static async Task Loopback(Action<bool, string> check)
    {
        var pipeName = "HexorMcpFixture_" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        using var client = new RuntimeMcpClient(pipeName);
        check(await client.ProbeAsync(CancellationToken.None), "Probe detects the fake bridge pipe as available");
        // The probe consumed one connection; reset the single-instance listener for the real request.
        await accept;
        server.Disconnect();
        accept = server.WaitForConnectionAsync();

        var call = client.SendAsync("sessions.list", new { }, CancellationToken.None);
        await accept;
        var request = await ReadFrameAsync(server, check);
        check(request.Contains("\"method\":\"sessions.list\"", StringComparison.Ordinal) &&
            request.Contains("\"protocolVersion\":1", StringComparison.Ordinal) &&
            request.Contains("\"deadlineUtc\"", StringComparison.Ordinal),
            "The client sends the documented request shape over the pipe");
        await WriteFrameAsync(server, SessionReply);
        var result = await call;
        check(result.Ok, "The client parses the canned success response");
        var rows = RuntimeMcpResponses.Sessions(result.Payload);
        check(rows.Count == 1 && rows[0].ProcessId == 4242 && rows[0].RuntimeSessionId == "s-1",
            "The canned session reply maps into one session row");

        // Second connection: the fake bridge answers with the documented failure envelope.
        server.Dispose();
        using var server2 = new NamedPipeServerStream(pipeName,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept2 = server2.WaitForConnectionAsync();
        var call2 = client.SendAsync("daily.control", new { accountId = 7, operation = "stop" }, CancellationToken.None);
        await accept2;
        var request2 = await ReadFrameAsync(server2, check);
        check(request2.Contains("\"accountId\":7", StringComparison.Ordinal) &&
            request2.Contains("\"operation\":\"stop\"", StringComparison.Ordinal),
            "Control requests carry the account and operation parameters");
        await WriteFrameAsync(server2,
            "{\"protocolVersion\":1,\"requestId\":\"echo2\",\"status\":\"failure\"," +
            "\"error\":{\"code\":\"deadline_exceeded\",\"message\":\"The request deadline elapsed before execution.\"}}");
        var failure2 = await call2;
        check(!failure2.Ok && failure2.Error is { } f && f.Code == "deadline_exceeded",
            "Failure envelopes map to typed errors");
    }

    private static async Task<string> ReadFrameAsync(PipeStream stream, Action<bool, string> check)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        check(length is > 0 and <= 4194304, "Fixture frame length is inside the documented bounds");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task WriteFrameAsync(PipeStream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] target)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await stream.ReadAsync(target.AsMemory(read));
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }
}
