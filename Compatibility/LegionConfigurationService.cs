using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hexor.Compatibility;

public enum ConfigurationAvailability { Ready, Missing, Invalid, Unsupported, Unavailable }
public sealed record ConfigurationSnapshot(string Path, ConfigurationAvailability Availability,
    JsonObject? Document, string? Fingerprint, string Message)
{
    public JsonObject Values => Document?["Values"] as JsonObject ?? new();
    public string? Value(string name) => Values[name]?.GetValue<string>();
    public bool NeedsWorkflowMigration => Document != null && (Document["WorkflowDefaultVersion"]?.GetValue<int>() ?? 0) < 2;
    public WorkflowPlan StoredWorkflow => WorkflowPlan.Read((JsonObject)Document!["Workflow"]!);
    public WorkflowPlan EffectiveWorkflow
    {
        get
        {
            var plan = StoredWorkflow;
            var steps = plan.Steps;
            bool legacy = plan.Finish == CompletionAction.GatherOnCharacter && steps.Count == 3
                && steps.All(s => s.Enabled) && steps[0].Action == WorkflowAction.AutoTask
                && steps[1].Action == WorkflowAction.FloorGold && steps[1].SmartPatrol
                && steps[2].Action == WorkflowAction.SwitchCharacter;
            if (NeedsWorkflowMigration && (steps.Count == 0 || legacy))
            {
                var replacement = WorkflowPlan.Default();
                return replacement with
                {
                    Revision = plan.Revision, Preserved = plan.Preserved,
                    Steps = replacement.Steps.Select(s => steps.FirstOrDefault(old => old.Action == s.Action) is { } old
                        ? s with { Preserved = old.Preserved } : s).ToArray()
                };
            }
            return plan.Normalize();
        }
    }
}

public sealed class ConfigurationConflictException() : IOException("Configuration changed externally. Reload before saving.");
public interface ILegionConfigurationService
{
    string Folder { get; }
    ConfigurationSnapshot Read(bool machine = false);
    ConfigurationSnapshot SaveScalars(ConfigurationSnapshot expected, IReadOnlyDictionary<string, string> changes, bool machine = false);
    ConfigurationSnapshot SaveWorkflow(ConfigurationSnapshot expected, WorkflowPlan plan);
    ConfigurationSnapshot InitializeMissingScalars(ConfigurationSnapshot expected, IReadOnlyDictionary<string,string> values)
        => throw new NotSupportedException("Initialization is unavailable.");
}

public sealed class LegionConfigurationService : ILegionConfigurationService
{
    private readonly ISettingsBackupService backup;
    private readonly Action<string>? auditSink;
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Output = new() { WriteIndented = true };
    public static readonly IReadOnlySet<string> PortableKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "UserLanguage", "GameLanguage", "AccountGridColumnWidths", "AccountGridColumnVisibility",
        "WorkSchedule_Enabled", "WorkSchedule_MaxConcurrent", "WorkSchedule_MinWorkMinutes", "WorkSchedule_MaxWorkMinutes",
        "WorkSchedule_MinRestMinutes", "WorkSchedule_MaxRestMinutes", "AutoTask_Race", "AutoTask_Job",
        "AutoTask_ClearSide", "AutoTask_ExploreCaves", "AutoTask_GatherAfterComplete"
    };
    public static readonly IReadOnlySet<string> MachineKeys = new HashSet<string>(StringComparer.Ordinal)
    { "GamePath_TW", "GamePath_KR", "GamePath_Global" };
    public string Folder { get; }
    public LegionConfigurationService(string rootOrConfigFolder, ISettingsBackupService? backup = null, Action<string>? auditSink = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootOrConfigFolder));
        Folder = Path.GetFileName(full).Equals("config", StringComparison.OrdinalIgnoreCase)
            ? full : Path.Combine(full, "config");
        this.backup = backup ?? new SettingsBackupService();
        this.auditSink = auditSink;
    }
    private string FilePath(bool machine) => Path.Combine(Folder, machine ? "machine.json" : "settings.json");
    public ConfigurationSnapshot InitializeMissingScalars(ConfigurationSnapshot expected, IReadOnlyDictionary<string,string> values)
        => StorageAudit.Run(StorageTarget.PortableJson, values.Keys, () => Commit(expected, false, root =>
        {
            var target = (JsonObject)root["Values"]!;
            foreach(var pair in values)
            {
                var name = VerifiedSettingsSession.AutoTaskKeys.FirstOrDefault(p => p.Value == pair.Key).Key;
                if(name == null || !int.TryParse(pair.Value, out var number) || !RegistryDefaults.IsValid("AutoTask", name, number))
                    throw new InvalidDataException("Unsupported initialization value.");
                if(!target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value;
            }
        }), result => result.Fingerprint != expected.Fingerprint, auditSink);
    public ConfigurationSnapshot Read(bool machine = false)
    {
        var path = FilePath(machine);
        try
        {
            if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Configuration exceeds the supported size.");
            var bytes = File.ReadAllBytes(path);
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using var parsed = JsonDocument.Parse(bytes.AsMemory(start));
            RejectDuplicateProperties(parsed.RootElement);
            var root = JsonNode.Parse(parsed.RootElement.GetRawText()) as JsonObject ?? throw new InvalidDataException("Expected a JSON object.");
            if (root["FormatVersion"]?.GetValue<int>() != 1)
                return new(path, ConfigurationAvailability.Unsupported, null, null, "Unsupported configuration format; no changes allowed.");
            if (root["Values"] is not JsonObject values || root["Workflow"] is not JsonObject workflow)
                throw new InvalidDataException("Required configuration fields are missing.");
            foreach (var pair in values)
            {
                if (pair.Value is not JsonValue v || !v.TryGetValue<string>(out _))
                    throw new InvalidDataException("Scalar values must be non-null JSON strings.");
                if ((machine ? PortableKeys : MachineKeys).Contains(pair.Key))
                    throw new InvalidDataException("A known setting is stored in the wrong document.");
            }
            var plan = WorkflowPlan.Read(workflow);
            if (machine && plan.Steps.Count != 0) throw new InvalidDataException("Machine workflow must be empty.");
            if (root.ContainsKey("WorkflowDefaultVersion") && (root["WorkflowDefaultVersion"] is not JsonValue version || !version.TryGetValue<int>(out _)))
                throw new InvalidDataException("Workflow default version must be an integer.");
            return new(path, ConfigurationAvailability.Ready, root, Hash(bytes),
                "Verified document loaded; unknown fields are retained, not interpreted.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { return new(path, ConfigurationAvailability.Missing, null, null, "Configuration file not found; reads create nothing."); }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        { return new(path, ConfigurationAvailability.Invalid, null, null, "Invalid configuration; original retained. Restore it in LegionCommander before editing."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(path, ConfigurationAvailability.Unavailable, null, null, "Configuration cannot be read; check access permissions."); }
    }

    public ConfigurationSnapshot SaveScalars(ConfigurationSnapshot expected, IReadOnlyDictionary<string, string> changes, bool machine = false)
        => StorageAudit.Run(machine ? StorageTarget.MachineJson : StorageTarget.PortableJson, changes.Keys,
            () => SaveScalarsCore(expected, changes, machine), result => result.Fingerprint != expected.Fingerprint, auditSink);
    private ConfigurationSnapshot SaveScalarsCore(ConfigurationSnapshot expected, IReadOnlyDictionary<string, string> changes, bool machine)
    {
        var allowed = machine ? MachineKeys : PortableKeys;
        foreach (var pair in changes)
        {
            if (!allowed.Contains(pair.Key)) throw new InvalidDataException("Setting is not a verified writable scalar.");
            ValidateScalar(pair.Key, pair.Value);
        }
        return Commit(expected, machine, root =>
        {
            var values = (JsonObject)root["Values"]!;
            foreach (var pair in changes) values[pair.Key] = pair.Value;
            if (changes.Keys.Any(k => k.StartsWith("WorkSchedule_", StringComparison.Ordinal)))
            {
                int Number(string key, int fallback) => int.TryParse(values[key]?.GetValue<string>(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int number) ? number : fallback;
                if (Number("WorkSchedule_MinWorkMinutes", 180) > Number("WorkSchedule_MaxWorkMinutes", 300)
                    || Number("WorkSchedule_MinRestMinutes", 60) > Number("WorkSchedule_MaxRestMinutes", 120))
                    throw new InvalidDataException("Schedule minimum must not exceed maximum.");
            }
        });
    }

    public ConfigurationSnapshot SaveWorkflow(ConfigurationSnapshot expected, WorkflowPlan plan)
        => StorageAudit.Run(StorageTarget.PortableJson, ["Workflow"], () => SaveWorkflowCore(expected,plan),
            result => result.Fingerprint != expected.Fingerprint, auditSink);
    private ConfigurationSnapshot SaveWorkflowCore(ConfigurationSnapshot expected, WorkflowPlan plan)
    {
        var normalized = plan.Normalize();
        if (expected.Availability != ConfigurationAvailability.Ready) throw new InvalidDataException(expected.Message);
        var original = expected.StoredWorkflow;
        // Keep the original workflow/step extension data; never let a UI-only representation discard it.
        normalized = normalized with
        {
            Preserved = original.Preserved,
            Steps = normalized.Steps.Select(s => original.Steps.FirstOrDefault(old => old.Id == s.Id) is { } old
                ? s with { Preserved = old.Preserved } : s).ToArray()
        };
        var unchanged = !expected.NeedsWorkflowMigration && JsonNode.DeepEquals(normalized.ToJson(), original.ToJson());
        if (unchanged) return expected;
        normalized = normalized with { Revision = Guid.NewGuid().ToString("N") };
        return Commit(expected, false, root =>
        {
            if (root["Workflow"]?["Revision"]?.GetValue<string>() != original.Revision) throw new ConfigurationConflictException();
            root["Workflow"] = normalized.ToJson(); root["WorkflowDefaultVersion"] = 2;
        });
    }

    private ConfigurationSnapshot Commit(ConfigurationSnapshot expected, bool machine, Action<JsonObject> patch)
    {
        var path = FilePath(machine);
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.Path, path) || expected.Availability != ConfigurationAvailability.Ready)
            throw new InvalidDataException("Load a valid existing document before saving.");
        lock (Locks.GetOrAdd(path, _ => new()))
        {
            var latest = Read(machine);
            if (latest.Availability != ConfigurationAvailability.Ready || latest.Fingerprint != expected.Fingerprint)
                throw new ConfigurationConflictException();
            var root = (JsonObject)latest.Document!.DeepClone();
            patch(root);
            if (JsonNode.DeepEquals(root, latest.Document)) return latest;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(Output));
                    stream.Write(bytes); stream.Flush(true);
                }
                backup.Backup(path); // Failure to back up aborts the write.
                if (Hash(File.ReadAllBytes(path)) != latest.Fingerprint) throw new ConfigurationConflictException();
                // Keep both the original application's previous-version file and Hexor's unique backup.
                File.Replace(temp, path, path + ".bak", true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            var persisted = Read(machine);
            if (persisted.Availability != ConfigurationAvailability.Ready || !JsonNode.DeepEquals(root, persisted.Document))
                throw new IOException("Configuration read-back verification failed.");
            Diagnostics.Log("compatibility", "backed-up document write verified");
            return persisted;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate JSON property."); RejectDuplicateProperties(p.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void ValidateScalar(string key, string value)
    {
        if (value == null) throw new InvalidDataException("Scalar cannot be null.");
        if (key is "GameLanguage" && value is not ("en-US" or "zh-TW" or "ko-KR")
            || key is "UserLanguage" && value is not ("en-US" or "zh-CN" or "ko-KR"))
            throw new InvalidDataException("Unsupported language identifier.");
        if (key is "AutoTask_Race" or "AutoTask_Job")
        {
            var options = key == "AutoTask_Race" ? LegionCommanderFeatureSettings.Races : LegionCommanderFeatureSettings.Jobs;
            if (!options.Any(option => value == option.Value.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Unsupported character default value.");
        }
        else if (key.StartsWith("AutoTask_", StringComparison.Ordinal) && value is not ("0" or "1"))
            throw new InvalidDataException("AutoTask booleans use 0/1 strings.");
        if (key == "WorkSchedule_Enabled" && value is not ("true" or "false"))
            throw new InvalidDataException("Schedule boolean must be a true/false string.");
        if (key.StartsWith("WorkSchedule_", StringComparison.Ordinal) && key != "WorkSchedule_Enabled")
        {
            int max = key == "WorkSchedule_MaxConcurrent" ? 16 : 10080;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > max)
                throw new InvalidDataException("Schedule values are outside verified bounds.");
        }
    }
}
