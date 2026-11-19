using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Hexor.Compatibility;

// Syncs Hexor's verified registry writes into the vendor runtime's shared settings documents.
// Contract verified in the installed LegionCommander build (decompiled, deobfuscated): the game
// DLL reads its runtime settings from <D:|C:>\<MachineId>\shared\<group>.json — the console and
// the injected DLL share this store, hence the "SharedSettings" name and per-group cross-process
// guard. Each document is
//   { "schema": 1, "updatedAt": <UTC>, "updatedBy": "commander",
//     "values": { <camelCaseKey>: <bool|int|array|object> },
//     "registryMirror": { <camelCaseKey>: <bool|int|array|object> } }
// written atomically (temp file + File.Move(overwrite), 3 attempts). The registry mirror under
// HKCU\Software\LegionCommander\<section> is the console-side durable copy; the shared document
// is what the running game applies. Writing only the registry leaves the game unaware until the
// vendor console happens to rebuild the document — the reported coin-shop failure mode
// (shop.json did not exist on the target machine despite all five toggles being ON in the
// registry). Hexor therefore keeps both sides in sync for every mapped value it writes.

public sealed record SharedSettingMapping(string Group, string Key, string Section, string ValueName, SharedSettingKind Kind);

public enum SharedSettingKind { Boolean, Integer }

public static class LegionSharedSettings
{
    // Verified group-to-registry mapping of the installed build. Only Boolean/Integer kinds are
    // synced through this path; the string-encoded resource tables (GatherTargets,
    // GatherTargetNames, AutoDungeonPriority) keep their dedicated verified write paths, and the
    // dungeon priority document is rebuilt from the registry string below.
    public static IReadOnlyList<SharedSettingMapping> Mappings { get; } = Array.AsReadOnly(new[]
    {
        new SharedSettingMapping("auto_task", "characterRace", "AutoTask", "CharacterRace", SharedSettingKind.Integer),
        new SharedSettingMapping("auto_task", "characterJob", "AutoTask", "CharacterJob", SharedSettingKind.Integer),
        new SharedSettingMapping("auto_task", "autoDailyQuests", "AutoTask", "AutoDailyQuests", SharedSettingKind.Boolean),
        new SharedSettingMapping("auto_task", "clearSideQuestsBeforeDungeon", "AutoTask", "ClearSideQuestsBeforeDungeon", SharedSettingKind.Boolean),
        new SharedSettingMapping("auto_task", "exploreCavesBeforeDungeon", "AutoTask", "ExploreCavesBeforeDungeon", SharedSettingKind.Boolean),
        new SharedSettingMapping("auto_task", "switchToGatherAfterTasks", "AutoTask", "SwitchToGatherAfterTasks", SharedSettingKind.Boolean),
        new SharedSettingMapping("mini_game", "autoEnter", "MiniGame", "AutoEnter", SharedSettingKind.Boolean),
        new SharedSettingMapping("mini_game", "useTaiwanVillageRules", "MiniGame", "UseTaiwanVillageRules", SharedSettingKind.Boolean),
        new SharedSettingMapping("abyss", "autoEnter", "ResourceGrab", "AutoEnterAbyss", SharedSettingKind.Boolean),
        new SharedSettingMapping("abyss", "layer", "ResourceGrab", "AbyssLayer", SharedSettingKind.Integer),
        new SharedSettingMapping("abyss", "counterAttack", "ResourceGrab", "AbyssCounterAttack", SharedSettingKind.Boolean),
        new SharedSettingMapping("auction", "autoSell", "ResourceGrab", "AuctionAutoSell", SharedSettingKind.Boolean),
        new SharedSettingMapping("auction", "priceMode", "ResourceGrab", "AuctionPriceMode", SharedSettingKind.Integer),
        new SharedSettingMapping("gather", "qualityFilterEnabled", "ResourceGrab", "GatherQualityFilterEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("gather", "qualityFilterLevel", "ResourceGrab", "GatherQualityFilterLevel", SharedSettingKind.Integer),
        new SharedSettingMapping("gather", "ignoreAether", "ResourceGrab", "GatherIgnoreAether", SharedSettingKind.Boolean),
        new SharedSettingMapping("dungeon", "legacyUrugugu", "ResourceGrab", "AutoDungeonUrugugu", SharedSettingKind.Boolean),
        new SharedSettingMapping("dungeon", "mode", "ResourceGrab", "AutoDungeonMode", SharedSettingKind.Integer),
        new SharedSettingMapping("enhance", "autoUpgradeMaxBattlePowerK", "Enhance", "AutoUpgradeMaxBattlePowerK", SharedSettingKind.Integer),
        new SharedSettingMapping("enhance", "enchantBoundKinahOnly", "Enhance", "EnchantBoundKinahOnly", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "autoDailyWave", "Enhance", "AutoDailyWaveEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "autoNightmare", "Enhance", "AutoNightmareEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "autoAwaken", "Enhance", "AutoAwakenEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "autoOdMorph", "Enhance", "AutoOdMorphEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "autoDungeonQuickJoin", "Enhance", "AutoDungeonQuickJoinEnabled", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "destroyInsteadOfExtract", "Enhance", "DestroyInsteadOfExtract", SharedSettingKind.Boolean),
        new SharedSettingMapping("enhance", "genusInsightAutoAnalyze", "Enhance", "GenusInsightAutoAnalyze", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "autoBuyOdEnergy", "Enhance", "AutoBuyOdEnergy", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "autoBuyReviveStone", "Enhance", "AutoBuyReviveStone", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "coinShopNightmareDaevanion", "Enhance", "CoinShopNightmareDaevanion", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "coinShopNightmareStigma", "Enhance", "CoinShopNightmareStigma", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "coinShopFestivalDaevanion", "Enhance", "CoinShopFestivalDaevanion", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "coinShopFestivalStigma", "Enhance", "CoinShopFestivalStigma", SharedSettingKind.Boolean),
        new SharedSettingMapping("shop", "coinShopAbyssStigma", "Enhance", "CoinShopAbyssStigma", SharedSettingKind.Boolean),
    });

    // Keys removed from Hexor entirely: rebuilding a group document deletes them from the
    // merged values so the running game stops applying the removed feature.
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RetiredGroupKeys { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["abyss"] = new[] { "guardianLogoutAvoidance" },
        };

    // Test seam: fixture tests point this at a temp root instead of the real machine layout.
    public static string? SharedDirectoryOverride { get; set; }

    // Machine-id resolution verified in the installed build: the Reliability MachineId under a
    // fixed drive (D: when fixed, otherwise C:), falling back to "AionDefault".
    public static string ResolveSharedDirectory()
    {
        if (SharedDirectoryOverride is { } directory) return directory;
        var root = "C:\\";
        try { if (new DriveInfo("D:").DriveType == DriveType.Fixed) root = "D:\\"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Diagnostics.Log("shared-settings", e.GetType().Name); }
        var machineId = "AionDefault";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Reliability");
            if (key?.GetValue("MachineId") is string value && !string.IsNullOrWhiteSpace(value)) machineId = value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Diagnostics.Log("shared-settings", e.GetType().Name); }
        return Path.Combine(root, machineId, "shared");
    }

    public static IEnumerable<SharedSettingMapping> ForRegistryValue(string section, string name) =>
        Mappings.Where(m => m.Section == section && m.ValueName == name);

    // Keeps the shared document for each affected group aligned with the verified registry values.
    // A failure is reported as false and logged; the registry mirror remains the durable copy the
    // vendor console imports on its next read, so a failed sync degrades to the old behavior
    // instead of corrupting anything.
    public static bool SyncFromRegistry(string section, string name, string? registryRoot = null)
    {
        // Only the real vendor mirror root may touch the live shared store; test runs against
        // disposable registry branches are no-ops unless a fixture provides an explicit override.
        if ((registryRoot ?? "Software\\LegionCommander") != "Software\\LegionCommander" && SharedDirectoryOverride is null) return true;
        var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in ForRegistryValue(section, name)) touched.Add(mapping.Group);
        foreach (var group in touched)
        {
            try
            {
                if (!SyncGroup(ResolveSharedDirectory(), group, registryRoot)) return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Log("shared-settings", $"sync failed; group={group}; type={e.GetType().Name}", DiagnosticSeverity.Warning);
                return false;
            }
        }
        return true;
    }

    // Rebuilds the group document from the current registry values so a missing or stale document
    // (the coin-shop failure mode) is repaired wholesale, not only for the last write. Keys that
    // exist in the document but are not registry-backed (gather targets/tables) are preserved.
    public static bool SyncGroup(string sharedDirectory, string group, string? registryRoot = null)
    {
        var mappings = Mappings.Where(m => m.Group == group).ToArray();
        if (mappings.Length == 0) return true;

        var values = new JsonObject();
        var mirror = new JsonObject();
        foreach (var mapping in mappings)
        {
            var entry = RegistryMirrorValue(mapping, registryRoot);
            if (entry is null) continue; // value not present yet; leave absent like the vendor importer
            values[mapping.Key] = entry.DeepClone();
            mirror[mapping.Key] = entry.DeepClone();
        }

        if (group == "dungeon" && ReadDungeonPriorityDocument(registryRoot) is { } priority) values["priority"] = priority.DeepClone();
        if (group == "gather" && ReadGatherTargetsDocument(registryRoot) is { } gather)
        {
            values["targets"] = gather["targets"]!.DeepClone();
            values["targetNames"] = gather["targetNames"]!.DeepClone();
        }

        Directory.CreateDirectory(sharedDirectory);
        var path = Path.Combine(sharedDirectory, group + ".json");

        JsonObject? existing = null;
        try
        {
            if (File.Exists(path)) existing = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            existing = null; // the vendor quarantines corrupt documents and rebuilds the same way
        }
        existing ??= new JsonObject { ["updatedBy"] = "commander" };
        existing["schema"] = 1;
        existing["updatedBy"] = "commander";
        existing["updatedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        var merged = existing["values"] as JsonObject ?? new JsonObject();
        foreach (var (key, node) in values)
        {
            if (node is null) continue;
            merged[key] = node.DeepClone();
        }
        if (RetiredGroupKeys.TryGetValue(group, out var retiredKeys))
            foreach (var retiredKey in retiredKeys) { merged.Remove(retiredKey); mirror.Remove(retiredKey); }
        existing["values"] = merged;
        existing["registryMirror"] = mirror.DeepClone();

        return WriteAtomically(path, existing);
    }

    // Gather targets are string-encoded registry values: lines of "1\t<identifier>". The shared
    // document stores values.targets as a JSON array of those same lines, plus the CJK→display
    // lookup from GatherTargetNames as values.targetNames (verified vendor importer shape).
    public static JsonObject? ReadGatherTargetsDocument(string? registryRoot = null)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey((registryRoot ?? "Software\\LegionCommander") + "\\ResourceGrab");
            if (key?.GetValue("GatherTargets", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text) return null;
            var targets = new JsonArray();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 3 || line[1] != '\t' || (line[0] != '0' && line[0] != '1')) continue;
                targets.Add(line);
            }
            var names = new JsonObject();
            if (key?.GetValue("GatherTargetNames", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string namesText)
            {
                foreach (var raw in namesText.Split('\n'))
                {
                    var line = raw.TrimStart('\uFEFF').TrimEnd('\r');
                    var tab = line.IndexOf('\t');
                    if (tab > 0 && tab < line.Length - 1) names.TryAdd(line[..tab], line[(tab + 1)..]);
                }
            }
            return new JsonObject { ["targets"] = targets, ["targetNames"] = names };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // Dungeon priority is a string-encoded registry value: lines of "1\t600001" (flag TAB mapId).
    // The shared document stores [{ "id": 600001, "checked": true }, ...].
    public static JsonArray? ReadDungeonPriorityDocument(string? registryRoot = null) => ReadDungeonDocument(registryRoot)?["priority"]?.AsArray();

    // Reads the shared dungeon.json document without writing anything.
    public static JsonObject? ReadDungeonDocument(string? registryRoot = null)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey((registryRoot ?? "Software\\LegionCommander") + "\\ResourceGrab");
            if (key?.GetValue("AutoDungeonPriority", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string text) return null;
            var array = new JsonArray();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 3 || line[1] != '\t') continue;
                var mapId = int.TryParse(line[2..], out var id) ? id : -1;
                if (mapId != 600012 && mapId != 600001 && mapId != 600032 && mapId != 600031) continue;
                array.Add(new JsonObject { ["id"] = mapId, ["checked"] = line[0] == '1' });
            }
            return new JsonObject { ["priority"] = array };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static JsonNode? RegistryMirrorValue(SharedSettingMapping mapping, string? registryRoot)
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey((registryRoot ?? "Software\\LegionCommander") + "\\" + mapping.Section);
            if (key is null) return null;
            var raw = key.GetValue(mapping.ValueName);
            if (raw is not int number) return null;
            return mapping.Kind switch
            {
                SharedSettingKind.Boolean => JsonValue.Create(number == 1),
                _ => JsonValue.Create(number),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // Atomic replacement verified in the installed build: PID+GUID temp file in the same
    // directory, flush-to-disk, then File.Move(overwrite) with up to 3 attempts and 20 ms backoff.
    private static bool WriteAtomically(string path, JsonObject document)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(path)!,
            Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true });
                document.WriteTo(writer);
                writer.Flush();
                stream.Flush(true);
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 3)
                {
                    Thread.Sleep(20);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
