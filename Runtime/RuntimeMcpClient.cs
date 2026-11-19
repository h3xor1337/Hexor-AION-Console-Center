using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Hexor.Runtime;

// Client for the installed LegionCommander runtime bridge (Aion2_RuntimeMcp_Commander_v1).
// The wire contract is the vendor's public, versioned bridge protocol verified in the installed
// build's bridge service (AION Auto Updater/LegionCommander.exe, RuntimeMcpBridgeService):
//   - 8-instance named pipe, access open to the local session (SDDL "D:(A;;GA;;;WD)");
//   - request  = 4-byte little-endian length + UTF-8 JSON BridgeRequest
//                { protocolVersion: 1, requestId, method, params, deadlineUtc };
//   - response = 4-byte little-endian length + UTF-8 JSON BridgeResponse
//                { protocolVersion, requestId, status, result, error { code, message }, completedAtUtc };
//   - max payload 4194304 bytes on both directions; JSON uses web defaults with null ignored and
//     camelCase enum names.
// Hexor uses this bridge only for observation and explicit operator-initiated session controls
// (pause/continue/stop, incident resolve, record reads). Hexor never touches game memory, never
// reads or writes challenge content, and never adds, edits, or logs in game accounts.

public static class RuntimeMcpBridge
{
    public const string PipeName = "Aion2_RuntimeMcp_Commander_v1";
    public const int ProtocolVersion = 1;
    public const int MaxMessageBytes = 4194304;
}

public sealed record RuntimeMcpFailure(string Code, string Message)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Message : $"{Code}: {Message}";
}

public sealed class RuntimeMcpResult
{
    public JsonElement Payload { get; init; }
    public bool Ok { get; init; }
    public RuntimeMcpFailure? Error { get; init; }
}

public sealed record RuntimeSessionRow(
    string RuntimeSessionId,
    int AccountId,
    int ProcessId,
    string ControllerState,
    bool IpcConnected,
    string Readiness,
    int CharacterLevel,
    int MapId,
    bool Direct = false);

public sealed record RuntimeIncidentRow(
    string IncidentId,
    int AccountId,
    string Status,
    string Summary,
    string? Claimant,
    string? ClaimToken);

public sealed record CaptchaOperationRow(
    string OperationId,
    int AccountId,
    string State,
    string Detail,
    DateTimeOffset? UpdatedAt);

public sealed record DailyRecordSummaryRow(
    int AccountId,
    string GameDay,
    string Summary,
    string Detail);

public interface IRuntimeMcpClient : IDisposable
{
    string StatusDescription { get; }
    Task<RuntimeMcpResult> SendAsync(string method, object? parameters, CancellationToken stop);
    Task<bool> ProbeAsync(CancellationToken stop);
}

// One connection per request keeps Hexor from holding one of the eight pipe instances while idle
// and avoids interleaving unrelated calls on the same stream.
public sealed class RuntimeMcpClient : IRuntimeMcpClient
{
    private int requestId;
    // Test seam: fixture tests run against an in-process fake bridge on a private pipe name.
    private readonly string pipeName;
    public RuntimeMcpClient(string? pipeNameOverride = null) => pipeName = pipeNameOverride ?? RuntimeMcpBridge.PipeName;
    public string StatusDescription { get; private set; } = "Runtime bridge not probed yet.";

    public async Task<bool> ProbeAsync(CancellationToken stop)
    {
        var available = await RuntimeMcpPipe.WaitForAvailabilityAsync(pipeName, stop);
        StatusDescription = available
            ? "Optional controller bridge is available."
            : "Optional controller bridge is not running. Live sessions still come from the direct game connection.";
        return available;
    }

    public async Task<RuntimeMcpResult> SendAsync(string method, object? parameters, CancellationToken stop)
    {
        await using var pipe = await RuntimeMcpPipe.ConnectAsync(pipeName, stop).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["protocolVersion"] = RuntimeMcpBridge.ProtocolVersion,
            ["requestId"] = $"hexor-{Interlocked.Increment(ref requestId)}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            ["method"] = method,
            ["params"] = parameters ?? new { },
            ["deadlineUtc"] = DateTimeOffset.UtcNow.AddSeconds(30),
        }, JsonOptions.Default);
        await BridgeFrame.WriteAsync(pipe, body, stop).ConfigureAwait(false);
        var response = await BridgeFrame.ReadAsync(pipe, stop).ConfigureAwait(false);
        return Parse(response);
    }

    internal static RuntimeMcpResult Parse(string response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            return new RuntimeMcpResult
            {
                Ok = true,
                Payload = root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                    ? result.Clone() : JsonSerializer.SerializeToElement(new { }),
            };
        var error = root.TryGetProperty("error", out var errorElement) ? errorElement : default;
        var code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString() ?? "" : "unknown_error";
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString() ?? "" : "The runtime bridge returned no error detail.";
        return new RuntimeMcpResult { Ok = false, Error = new RuntimeMcpFailure(code, message) };
    }

    public void Dispose() { }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal static class BridgeFrame
{
    public static async Task WriteAsync(NamedPipeClientStream pipe, string json, CancellationToken stop)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length is 0 or > RuntimeMcpBridge.MaxMessageBytes)
            throw new InvalidDataException("Runtime bridge payload is outside the allowed size.");
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, stop).ConfigureAwait(false);
        await pipe.WriteAsync(payload, stop).ConfigureAwait(false);
        await pipe.FlushAsync(stop).ConfigureAwait(false);
    }

    public static async Task<string> ReadAsync(NamedPipeClientStream pipe, CancellationToken stop)
    {
        var header = new byte[4];
        await ReadExactlyAsync(pipe, header, stop).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > RuntimeMcpBridge.MaxMessageBytes)
            throw new InvalidDataException($"Invalid Runtime bridge payload length: {length}.");
        var payload = new byte[length];
        await ReadExactlyAsync(pipe, payload, stop).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task ReadExactlyAsync(NamedPipeClientStream pipe, Memory<byte> target, CancellationToken stop)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await pipe.ReadAsync(target[read..], stop).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Runtime bridge stream ended unexpectedly.");
            read += count;
        }
    }
}

// Pipe helpers shared by the client and probe.
internal static class RuntimeMcpPipe
{
    public static async Task<bool> WaitForAvailabilityAsync(string pipeName, CancellationToken stop)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var pipe = ConnectUnsafe(pipeName);
                await pipe.ConnectAsync(2000, stop).ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                try { await Task.Delay(300, stop).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        return false;
    }

    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken stop)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pipe = ConnectUnsafe(pipeName);
            try
            {
                await pipe.ConnectAsync(4000, stop).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                pipe.Dispose();
                try { await Task.Delay(300, stop).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        throw new IOException("The Runtime bridge pipe could not be connected.");
    }

    private static NamedPipeClientStream ConnectUnsafe(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
}

// Raw DTO projections used by the live-control page. Property names follow the vendor's
// serialized contract (camelCase JSON, read case-insensitively).
public static class RuntimeMcpResponses
{
    public static IReadOnlyList<RuntimeSessionRow> Sessions(JsonElement payload)
    {
        var rows = new List<RuntimeSessionRow>();
        if (!TryArray(payload, out var items)) return rows;
        foreach (var item in items)
        {
            rows.Add(new(
                String(item, "runtimeSessionId"),
                Int(item, "accountId"),
                Int(item, "processId"),
                String(item, "controllerState"),
                Bool(item, "ipcConnected"),
                String(item, "readiness"),
                Int(item, "characterLevel"),
                Int(item, "mapId")));
        }
        return rows;
    }

    public static IReadOnlyList<RuntimeIncidentRow> Incidents(JsonElement payload)
    {
        var rows = new List<RuntimeIncidentRow>();
        var data = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("incidents", out var inner)
            ? inner : payload;
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                rows.Add(Incident(item));
            return rows;
        }
        if (TryArray(payload, out var items))
        {
            foreach (var item in items)
                rows.Add(Incident(item));
        }
        return rows;
    }

    private static RuntimeIncidentRow Incident(JsonElement item) => new(
        String(item, "incidentId"),
        Int(item, "accountId"),
        String(item, "status"),
        String(item, "summary") is { Length: > 0 } summary ? summary : String(item, "kind"),
        String(item, "claimant") is { Length: > 0 } claimant ? claimant : null,
        String(item, "claimToken") is { Length: > 0 } token ? token : null);

    public static IReadOnlyList<CaptchaOperationRow> CaptchaOperations(JsonElement payload)
    {
        var rows = new List<CaptchaOperationRow>();
        if (!TryArray(payload, out var items))
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("operations", out var operations))
                items = operations.EnumerateArray().ToArray();
            else
                return rows;
        }
        foreach (var item in items)
        {
            DateTimeOffset? updated = null;
            if (item.TryGetProperty("updatedAtUtc", out var updatedElement) && updatedElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updatedElement.GetString(), out var parsed)) updated = parsed;
            rows.Add(new(
                String(item, "operationId"),
                Int(item, "accountId"),
                String(item, "state") is { Length: > 0 } state ? state : String(item, "status"),
                String(item, "detail"),
                updated));
        }
        return rows;
    }

    // DailyRoleSummary projection from the vendor's daily.records "summary" operation.
    public static IReadOnlyList<DailyRecordSummaryRow> DailySummaries(JsonElement payload)
    {
        var rows = new List<DailyRecordSummaryRow>();
        if (!TryArray(payload, out var items))
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("summaries", out var summaries))
                items = summaries.EnumerateArray().ToArray();
            else
                return rows;
        }
        foreach (var item in items)
        {
            var name = String(item, "name");
            var status = String(item, "status");
            var kina = item.TryGetProperty("dailyKina", out var kinaElement) && kinaElement.ValueKind == JsonValueKind.Number
                ? kinaElement.GetInt64().ToString("N0") : "";
            var gather = item.TryGetProperty("gatherSeconds", out var gatherElement) && gatherElement.ValueKind == JsonValueKind.Number
                ? Math.Round(gatherElement.GetDouble() / 60.0, 1) + " min" : "";
            var power = item.TryGetProperty("combatPower", out var powerElement) && powerElement.ValueKind == JsonValueKind.Number
                ? powerElement.GetUInt32().ToString("N0") : "";
            var detail = string.Join(" · ", new[] { kina.Length > 0 ? "Daily kinah " + kina : "", gather, power }.Where(x => x.Length > 0));
            rows.Add(new(
                Int(item, "accountId"),
                String(item, "gameDay"),
                name.Length > 0 ? name + " — " + status : status,
                detail));
        }
        return rows;
    }

    private static bool TryArray(JsonElement payload, out JsonElement[] items)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            items = payload.EnumerateArray().ToArray();
            return true;
        }
        foreach (var candidate in new[] { "sessions", "items", "incidents", "operations", "summaries", "rows" })
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(candidate, out var found) &&
                found.ValueKind == JsonValueKind.Array)
            {
                items = found.EnumerateArray().ToArray();
                return true;
            }
        items = [];
        return false;
    }

    private static string String(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Int(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static bool Bool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
