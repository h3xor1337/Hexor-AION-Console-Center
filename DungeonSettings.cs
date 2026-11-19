using System.IO;
using System.Text.Json.Nodes;
using Hexor.Compatibility;
using Microsoft.Win32;

namespace Hexor;

public sealed record DungeonPriorityEntry(int MapId, string Label);
public sealed record DungeonRegistrySnapshot(RegistryStringEntry Priority, RegistryEntry Mode, RegistryEntry LegacyUrugugu)
{
    public bool CanEdit => Priority is { Exists: true, Supported: true, Value: not null } && Mode.Supported;
}

// Dungeon settings follow the installed LegionCommander's verified format exactly:
//   AutoDungeonPriority (REG_SZ) — lines of "flag\tmapId" joined with "\n",
//     known dungeons: 600012 Uruguru Cave, 600001 Krao Cave,
//     600032 Draupnir Cave (Conquest), 600031 Draupnir Cave (Easy);
//   AutoDungeonMode (REG_DWORD) — 0 easy, 1 conquest.
// The older "AutoDungeonPreferred" DWORD is not read by the installed build and is left untouched.
public static class DungeonCatalog
{
    public static IReadOnlyList<DungeonPriorityEntry> Known { get; } = Array.AsReadOnly(new[]
    {
        new DungeonPriorityEntry(600012, "Uruguru Cave"),
        new DungeonPriorityEntry(600001, "Krao Cave"),
        new DungeonPriorityEntry(600032, "Draupnir Cave (Conquest)"),
        new DungeonPriorityEntry(600031, "Draupnir Cave (Easy)"),
    });

    public static string Label(int mapId) => Known.FirstOrDefault(k => k.MapId == mapId) is { } entry
        ? entry.Label : $"Unknown dungeon ({mapId})";

    public static string Serialize(IEnumerable<int> orderedMapIds, Func<int, bool> enabled) =>
        string.Join("\n", orderedMapIds.Select(id => (enabled(id) ? "1" : "0") + "\t" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // The shared document stores [{"id":600001,"checked":true},…]; the registry stores "flag<TAB>mapId" lines.
    public static string SerializePriorityDocument(System.Text.Json.Nodes.JsonArray document) =>
        string.Join("\n", document.Select(n => ((n?["checked"]!.GetValue<bool>() == true) ? "1" : "0") + "\t" + (n!["id"]!.GetValue<int>()).ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static List<(int MapId, bool Enabled)> Parse(string text)
    {
        var result = new List<(int MapId, bool Enabled)>();
        var seen = new HashSet<int>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3 || line[1] != '\t' || (line[0] != '0' && line[0] != '1')) continue;
            if (!int.TryParse(line[2..], out var mapId) || seen.Add(mapId) == false) continue;
            if (Known.All(k => k.MapId != mapId)) continue;
            result.Add((mapId, line[0] == '1'));
        }
        return result;
    }
}

public sealed partial class RegistrySettingsService
{
    private static RegistryStringEntry InspectDungeonPriority(RegistryKey? key) => InspectString(key, "AutoDungeonPriority");

    public DungeonRegistrySnapshot ReadDungeon()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(root + "\\ResourceGrab");
        var mode = key is null || !key.GetValueNames().Contains("AutoDungeonMode", StringComparer.OrdinalIgnoreCase)
            ? new RegistryEntry(null, false, true)
            : key.GetValueKind("AutoDungeonMode") != RegistryValueKind.DWord
                ? new RegistryEntry(null, true, false)
                : new RegistryEntry(key.GetValue("AutoDungeonMode") as int?, true, true);
        var urugugu = key is null || !key.GetValueNames().Contains("AutoDungeonUrugugu", StringComparer.OrdinalIgnoreCase)
            ? new RegistryEntry(null, false, true)
            : key.GetValueKind("AutoDungeonUrugugu") != RegistryValueKind.DWord
                ? new RegistryEntry(null, true, false)
                : new RegistryEntry(key.GetValue("AutoDungeonUrugugu") as int?, true, true);
        var priority = InspectDungeonPriority(key);
        // Vendor precedence: the shared document is authoritative; the registry is the fallback.
        if (key is not null)
        {
            var document = LegionSharedSettings.ReadDungeonDocument(root);
            JsonObject? values = document?["values"] as JsonObject;
            var array = document?["priority"] as JsonArray;
            if (array is { Count: > 0 } && priority.Value is null)
                priority = priority with { Value = DungeonCatalog.SerializePriorityDocument(array), Supported = true };
            if (values?["mode"] is { } modeValue && modeValue is JsonValue && int.TryParse(modeValue.ToJsonString().Trim('"'), out var parsedMode) && parsedMode is 0 or 1 && !mode.Exists && mode.Supported)
                mode = new(parsedMode, true, true);
            if (values?["legacyUrugugu"] is { } flagValue && flagValue is JsonValue && bool.TryParse(flagValue.ToJsonString().Trim('"'), out var parsedFlag) && !urugugu.Exists && urugugu.Supported)
                urugugu = new(parsedFlag ? 1 : 0, true, true);
        }
        return new(priority, mode, urugugu);
    }

    public IReadOnlyList<(int MapId, bool Enabled)> DungeonDefaults() =>
        [(600001, true), (600032, true), (600031, true), (600012, false)];

    public void WriteDungeonPriorityVerified(IReadOnlyList<(int MapId, bool Enabled)> entries)
    {
        StorageAudit.Run(StorageTarget.Registry, ["ResourceGrab.AutoDungeonPriority"], () =>
        {
            var snapshot = ReadDungeon();
            if (snapshot.Priority.Exists && !snapshot.Priority.Supported) throw new InvalidDataException("Unsupported dungeon priority type preserved.");
            var known = DungeonCatalog.Known.Select(k => k.MapId).ToHashSet();
            var submitted = entries.Select(e => e.MapId).ToHashSet();
            if (submitted.Count != known.Count || !submitted.SetEquals(known))
                throw new InvalidDataException("Dungeon priority must list every known dungeon exactly once.");
            var updated = DungeonCatalog.Serialize(entries.Select(e => e.MapId), id => entries.First(e => e.MapId == id).Enabled);
            using var mutex = new Mutex(false, "Local\\Hexor.LegionCommander.DungeonEdit");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Dungeon settings are busy. Try again.");
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = hive.CreateSubKey(root + "\\ResourceGrab");
                var current = ReadDungeon();
                if (snapshot.Priority.Exists && current.Priority != snapshot.Priority) throw new ConfigurationConflictException();
                if (updated == snapshot.Priority.Value) return false;
                key.SetValue("AutoDungeonPriority", updated, RegistryValueKind.String);
                var verified = ReadDungeon();
                if (verified.Priority.Value != updated) throw new IOException("Dungeon priority write could not be verified.");
                LegionSharedSettings.SyncFromRegistry("ResourceGrab", "AutoDungeonPriority", root);
                return true;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }, changed => changed, auditSink);
    }
}
