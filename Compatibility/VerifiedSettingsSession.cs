using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace Hexor.Compatibility;

public sealed record ScheduleSettings(bool Enabled = false, int Accounts = 10, int WorkMin = 180,
    int WorkMax = 300, int RestMin = 60, int RestMax = 120)
{
    public static ScheduleSettings Read(ConfigurationSnapshot document)
    {
        int Number(string key, int fallback) => int.TryParse(document.Value(key), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int n) ? n : fallback;
        var work = Ordered(Number("WorkSchedule_MinWorkMinutes", 180), Number("WorkSchedule_MaxWorkMinutes", 300));
        var rest = Ordered(Number("WorkSchedule_MinRestMinutes", 60), Number("WorkSchedule_MaxRestMinutes", 120));
        return new(bool.TryParse(document.Value("WorkSchedule_Enabled"), out bool enabled) && enabled,
            Math.Clamp(Number("WorkSchedule_MaxConcurrent", 10), 1, 16), work.Min, work.Max, rest.Min, rest.Max);
    }
    private static (int Min, int Max) Ordered(int a, int b)
    { a = Math.Clamp(a, 1, 10080); b = Math.Clamp(b, 1, 10080); return (Math.Min(a, b), Math.Max(a, b)); }
    public IReadOnlyDictionary<string, string> ToPatch()
    {
        if (Accounts < 1 || Accounts > 16 || WorkMin < 1 || WorkMax < WorkMin || WorkMax > 10080
            || RestMin < 1 || RestMax < RestMin || RestMax > 10080)
            throw new InvalidDataException("Accounts must be 1–16 and duration ranges must satisfy 1 ≤ minimum ≤ maximum ≤ 10080.");
        string Text(int n) => n.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            ["WorkSchedule_Enabled"] = Enabled ? "true" : "false", ["WorkSchedule_MaxConcurrent"] = Text(Accounts),
            ["WorkSchedule_MinWorkMinutes"] = Text(WorkMin), ["WorkSchedule_MaxWorkMinutes"] = Text(WorkMax),
            ["WorkSchedule_MinRestMinutes"] = Text(RestMin), ["WorkSchedule_MaxRestMinutes"] = Text(RestMax)
        };
    }
}

public sealed class PartialSettingsWriteException() : IOException(
    "JSON was saved, but the registry mirror failed. The stores are not synchronized; use Retry AutoTask mirror.");

public sealed class VerifiedSettingsSession(ILegionConfigurationService service, IRegistrySettingsService registry)
{
    public ILegionConfigurationService Service { get; } = service;
    public ConfigurationSnapshot Document { get; private set; } = service.Read();
    public bool Ready => Document.Availability == ConfigurationAvailability.Ready;
    public void Reload() => Document = Service.Read();
    public string Description => Ready ? Service.Folder + (Document.NeedsWorkflowMigration ? " • workflow migration pending explicit Apply" : " • verified format v1") : Document.Message;
    public static readonly IReadOnlyDictionary<string, string> AutoTaskKeys = new Dictionary<string, string>
    {
        ["CharacterRace"] = "AutoTask_Race", ["CharacterJob"] = "AutoTask_Job",
        ["ClearSideQuestsBeforeDungeon"] = "AutoTask_ClearSide", ["ExploreCavesBeforeDungeon"] = "AutoTask_ExploreCaves",
        ["SwitchToGatherAfterTasks"] = "AutoTask_GatherAfterComplete"
    };
    public int? ReadAutoTask(string registryName)
    {
        if (!Ready) return null;
        var value = Document.Value(AutoTaskKeys[registryName]);
        if (value == null) return registry.Read("AutoTask", registryName) ?? (registryName == "CharacterRace" ? 2 : 1);
        return int.TryParse(value, out int n) ? n : registryName == "CharacterRace" ? 2 : 1;
    }
    public void SaveAutoTask(string registryName, bool value)
    {
        if (registryName is "CharacterRace" or "CharacterJob" || !AutoTaskKeys.ContainsKey(registryName))
            throw new InvalidDataException("Only verified AutoTask booleans are editable.");
        Document = Service.SaveScalars(Document, new Dictionary<string, string> { [AutoTaskKeys[registryName]] = value ? "1" : "0" });
        try { registry.WriteVerified("AutoTask", registryName, value ? 1 : 0); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { throw new PartialSettingsWriteException(); }
    }
    public void SaveAutoTaskNumber(string registryName, int value)
    {
        var options = registryName switch
        {
            "CharacterRace" => LegionCommanderFeatureSettings.Races,
            "CharacterJob" => LegionCommanderFeatureSettings.Jobs,
            _ => throw new InvalidDataException("Unknown character setting.")
        };
        if (!options.Any(option => option.Value == value))
            throw new InvalidDataException("Choose a supported character setting value.");

        var mirror = registry.Inspect("AutoTask", registryName);
        if (!mirror.Supported || (mirror.Exists && !options.Any(option => option.Value == mirror.Value)))
            throw new InvalidDataException("The existing registry value uses an unsupported encoding and was preserved.");

        Document = Service.SaveScalars(Document, new Dictionary<string, string>
        {
            [AutoTaskKeys[registryName]] = value.ToString(CultureInfo.InvariantCulture)
        });
        try { registry.MirrorAutoTaskNumber(registryName, value); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { throw new PartialSettingsWriteException(); }
    }

    public string RetryMirror()
    {
        if (!Ready) throw new InvalidDataException(Document.Message);
        int skipped = 0;
        foreach (var name in AutoTaskKeys.Keys)
        {
            int? value = ReadAutoTask(name);
            var entry = registry.Inspect("AutoTask", name);
            bool Valid(int? number) => name switch
            {
                "CharacterRace" => number is 1 or 2, "CharacterJob" => number >= 0 && number <= 8,
                _ => number is 0 or 1
            };
            if (!Valid(value) || !entry.Supported || (entry.Exists && !Valid(entry.Value))) { skipped++; continue; }
            if (name is "CharacterRace" or "CharacterJob") registry.MirrorAutoTaskNumber(name, value!.Value);
            else registry.WriteVerified("AutoTask", name, value!.Value);
        }
        return $"Verified AutoTask mirrors applied and read back. {skipped} unknown value(s) preserved; no named enum mapping was used.";
    }
    public void SaveSchedule(ScheduleSettings value) => Document = Service.SaveScalars(Document, value.ToPatch());
    public void SaveWorkflow(WorkflowPlan value) => Document = Service.SaveWorkflow(Document, value);
    public void SaveLanguage(string key, string culture) => Document = Service.SaveScalars(Document,
        new Dictionary<string, string> { [key] = culture });
    public ConfigurationSnapshot Machine => Service.Read(true);
    public void SaveMachinePaths(ConfigurationSnapshot expected, IReadOnlyDictionary<string, string> values) =>
        Service.SaveScalars(expected, values, true);
}

public static class LegionConfigurationLocator
{
    // Enumerate only documented metadata candidates, even if missing or inaccessible.
    // A saved legacy character-settings folder must not hide valid portable candidates.
    public static IReadOnlyList<string> Candidates(string? selected, string? machineIdentifier = null, IEnumerable<string>? drives = null)
    {
        string machine = machineIdentifier ?? "AionDefault";
        if (machineIdentifier == null)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Reliability");
                machine = key?.GetValue("MachineId")?.ToString() ?? machine;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Log("configuration", "root metadata unavailable"); }
        }
        if (string.IsNullOrWhiteSpace(machine) || machine is "." or ".." || Path.GetFileName(machine) != machine
            || machine.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) machine = "AionDefault";
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            try { results.Add(Path.GetFullPath(selected)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            { Diagnostics.Log("configuration", "saved folder path invalid"); }
        }
        var roots = (drives ?? new[] { @"D:\", @"C:\" }).Where(Directory.Exists).ToArray();
        results.AddRange(roots
            .SelectMany(root => new[] { machine, "Aion.ONL", "AionDefault" }.Select(name => Path.Combine(root, name))));
        // Existing installations can belong to an earlier Windows profile/MachineId.
        // Inspect only immediate GUID directories on the reference's C/D roots;
        // the resolver still requires the verified JSON schema and handles ambiguity.
        foreach (var root in roots)
        {
            try
            {
                results.AddRange(Directory.EnumerateDirectories(root)
                    .Where(path => Guid.TryParse(Path.GetFileName(path), out _)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Log("configuration", "root enumeration unavailable"); }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
