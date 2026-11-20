using System.IO;
using System.Text.Json.Nodes;

namespace Hexor.Runtime;

// The in-game Target Filter reads the saved gather list from the module's per-character
// settings files (<config root>\<Character>_Setting.json, JSON key GatherConfig.targetNames),
// not from the registry. Those files are saved from the game's own UI and have been observed
// saved with an empty target list, which makes the panel show "No targets (will match all)"
// until the operator presses Reset Default again. The saved defaults file
// (DefaultAppSetting.json in the same folder) is what "apply default settings" re-applies.
//
// The sweep repairs that failure mode and the one that causes it. Live module logs confirmed
// the wipe cycle: the saved defaults file (DefaultAppSetting.json) must carry the module's
// required config nodes (AuctionConfig, AutoCombatConfig, FreeFashionConfig, GatherConfig);
// when it does not, every "apply default" fails (default config invalid), the module's
// in-memory gather config stays empty, and the next settings save writes an empty target list
// over the character file. The sweep therefore
//   1. restores empty per-character gather lists from the fullest saved list on the same
//      installation (or the vendor's built-in selection when none exists), and
//   2. rebuilds an invalid or missing defaults file verbatim from a healthy character file —
//      the same full-config shape the vendor console's "set current config as default"
//      produces. When no healthy reference exists and the defaults file is Hexor's own
//      gather-only seed, it is removed so the module falls back to its built-in default.
// It never removes entries, never changes checked flags on existing lists, and never creates
// character files.
//
// The module has been observed saving an empty gather list mid-run, so the sweep alone is not
// enough: the repair runs the moment the module writes a settings file (filesystem watcher,
// debounced), restoring the list between the module's own wipes.
public static class CharacterSettingsRepairService
{
    // Test seam: unit tests run the sweep against a temp directory directly, so the live
    // discovery path stays disabled there.
    public static bool LiveSweepEnabled { get; set; } = true;

    // Raised after a sweep changed something on disk; the console reacts by asking the running
    // game to reload the restored configuration (ApplyDefaultConfig).
    public static event Action? RepairApplied;

    private static readonly System.Threading.SemaphoreSlim sweepGate = new(1, 1);
    private static readonly object watcherGate = new();
    private static readonly List<FileSystemWatcher> watchers = new();
    private static System.Threading.Timer? debounce;
    private static readonly object debounceLock = new();
    private const int DebounceMilliseconds = 1200;

    // Vendor built-in default selection (order and checked flags verified in the module's
    // embedded default resource; names are the saved identifiers).
    // Single source of truth with the gathering page: same 24 identifiers and default checks.
    public static (string Name, bool Checked)[] VendorDefaultTargets =>
        [.. GatheringDefaults.Entries.Select(entry => (entry.Identifier, entry.Enabled))];

    public static IReadOnlyList<string> CandidateDirectories()
    {
        var results = new List<string>();
        foreach (var root in new[] { @"D:\", @"C:\" })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    var looksLikeConfigRoot = System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9a-fA-F-]{36}$")
                        || name is "AionDefault" || name.StartsWith("ef0bd386", StringComparison.OrdinalIgnoreCase);
                    if (!looksLikeConfigRoot) continue;
                    if (Directory.GetFiles(dir, "*_Setting.json").Length > 0 || File.Exists(Path.Combine(dir, "DefaultAppSetting.json")))
                        results.Add(dir);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Log("settings-repair", "enumerate-failed; root=" + root + "; type=" + e.GetType().Name); }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Returns how many saved files were repaired and how many defaults were seeded.
    public static (int RepairedFiles, int SeededDefaults) Sweep(string? directory = null)
    {
        if (directory is null && LiveSweepEnabled) EnsureWatchers();
        sweepGate.Wait();
        try { return SweepCore(directory); }
        finally { sweepGate.Release(); }
    }

    private static (int RepairedFiles, int SeededDefaults) SweepCore(string? directory)
    {
        var roots = directory is { } single ? new[] { single } : CandidateDirectories();
        var repaired = 0;
        var seeded = 0;
        foreach (var rootDir in roots)
        {
            try
            {
                var files = Directory.GetFiles(rootDir, "*_Setting.json");
                if (files.Length == 0) continue;
                var donor = FindDonorList(files);
                foreach (var file in files)
                {
                    if (RepairFile(file, donor)) { repaired++; Diagnostics.Log("settings-repair", "repaired; file=" + Path.GetFileName(file)); }
                }
                seeded += RepairSavedDefaults(rootDir, files);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
            { Diagnostics.Log("settings-repair", "sweep-failed; dir=" + rootDir + "; type=" + e.GetType().Name); }
        }
        if ((repaired > 0 || seeded > 0) && directory is null)
        {
            try { RepairApplied?.Invoke(); }
            catch (Exception e) { Diagnostics.Log("settings-repair", "notify-failed; type=" + e.GetType().Name); }
        }
        return (repaired, seeded);
    }

    // The module wipes a character file's gather list while it runs; watching the config roots
    // lets Hexor restore the list the moment it happens instead of at the next periodic sweep.
    private static void EnsureWatchers()
    {
        lock (watcherGate)
        {
            if (watchers.Count > 0) return;
            foreach (var root in CandidateDirectories())
            {
                var watcher = new FileSystemWatcher(root)
                {
                    Filter = "*_Setting.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += (_, e) => ScheduleSweep();
                watcher.Created += (_, e) => ScheduleSweep();
                watcher.Renamed += (_, e) => ScheduleSweep();
                watcher.Error += (_, e) =>
                {
                    Diagnostics.Log("settings-repair", "watcher-error; root=" + Path.GetFileName(root) + "; type=" + e.GetException().GetType().Name);
                    try { watcher.Dispose(); } catch { }
                    lock (watcherGate) { watchers.Remove(watcher); watchers.Clear(); } // recreated on the next sweep
                };
                try { watcher.EnableRaisingEvents = true; watchers.Add(watcher); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { Diagnostics.Log("settings-repair", "watcher-start-failed; root=" + Path.GetFileName(root) + "; type=" + e.GetType().Name); }
            }
            if (watchers.Count > 0)
                Diagnostics.Log("settings-repair", "watchers-started; roots=" + watchers.Count);
        }
    }

    private static void ScheduleSweep()
    {
        lock (debounceLock)
        {
            if (debounce == null)
                debounce = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (LiveSweepEnabled) Sweep();
                    }
                    catch (Exception e)
                    { Diagnostics.Log("settings-repair", "watch-sweep-failed; type=" + e.GetType().Name); }
                });
            debounce.Change(DebounceMilliseconds, System.Threading.Timeout.Infinite);
        }
    }

    // The module rejects a defaults file that lacks any required node ("default config
    // invalid"), which is what empties the running game's gather config. Required nodes were
    // confirmed from the module's own validation and from healthy saved files;
    // GuardianVerificationConfig appears only in some files and is optional.
    public static readonly string[] RequiredDefaultNodes =
        ["AuctionConfig", "AutoCombatConfig", "FreeFashionConfig", "GatherConfig"];

    // Returns how many defaults files were rebuilt or removed.
    private static int RepairSavedDefaults(string rootDir, IReadOnlyList<string> files)
    {
        var defaultPath = Path.Combine(rootDir, "DefaultAppSetting.json");
        var existing = ReadConfig(defaultPath);
        if (existing is JsonObject && HasRequiredNodes(existing) && ReferenceTargetCount(existing) >= 0)
        {
            return 0; // valid persisted default; the module applies it as-is
        }
        var reference = FindReferenceConfig(files);
        if (reference is JsonObject full)
        {
            if (existing is JsonObject) Backup(defaultPath, ".bak-invalid-default");
            WriteConfig(defaultPath, full);
            Diagnostics.Log("settings-repair", "default-rebuilt; source=" + Path.GetFileName(_referenceSource ?? "character file") + "; had=" + DescribeNodes(existing));
            return 1;
        }
        if (existing is JsonObject && IsGatherOnlySeed(existing))
        {
            // Hexor's own gather-only seed: invalid for the module and no reference exists to
            // rebuild from. Removing it lets the module fall back to its built-in default.
            try { File.Delete(defaultPath); Diagnostics.Log("settings-repair", "default-removed; reason=gather-only-seed; fallback=built-in"); return 1; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Log("settings-repair", "default-remove-failed; type=" + e.GetType().Name); return 0; }
        }
        if (existing is not null)
            Diagnostics.Log("settings-repair", "default-invalid; had=" + DescribeNodes(existing) + "; action=left-untouched");
        return 0;
    }

    private static JsonNode? ReadConfig(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        { return null; }
    }

    private static bool HasRequiredNodes(JsonNode node) =>
        node is JsonObject obj && RequiredDefaultNodes.All(n => obj[n] is JsonObject);

    private static int ReferenceTargetCount(JsonNode node) =>
        node is JsonObject obj && obj["GatherConfig"]?["targetNames"] is JsonArray names ? names.Count : 0;

    private static bool IsGatherOnlySeed(JsonNode node) =>
        node is JsonObject obj && obj.All(property => property.Key == "GatherConfig");

    private static string DescribeNodes(JsonNode? node) =>
        node is JsonObject obj ? string.Join("+", obj.Select(property => property.Key)) : "unparsable";

    // A reference is a character file with every required node present; the fullest gather
    // list wins. The reference contributes its non-gather configuration verbatim; the gather
    // section is rebuilt in the module's own defaults shape (minimal {name, checked} entries —
    // the module's deserializer drops the character-file entries with tids/bindingSource, which
    // made every apply-default save an empty gather list back over the character file).
    private static JsonObject? FindReferenceConfig(IReadOnlyList<string> files)
    {
        JsonObject? best = null; var bestCount = -1; string? bestFile = null;
        foreach (var file in files)
        {
            if (ReadConfig(file) is not JsonObject doc || !HasRequiredNodes(doc)) continue;
            var count = ReferenceTargetCount(doc);
            if (count > bestCount) { bestCount = count; best = doc; bestFile = file; }
        }
        if (best is JsonObject reference)
        {
            _referenceSource = bestFile;
            var defaults = reference.DeepClone().AsObject();
            defaults["GatherConfig"] = DefaultConfigNode();
            return defaults;
        }
        _referenceSource = null;
        return null;
    }

    private static string? _referenceSource;

    private static void WriteConfig(string path, JsonObject node)
    {
        var temp = path + ".hexor-tmp";
        File.WriteAllText(temp, Json(node), new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static void Backup(string path, string suffix)
    {
        try { File.Copy(path, path + suffix, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Diagnostics.Log("settings-repair", "backup-failed; file=" + Path.GetFileName(path) + "; type=" + e.GetType().Name); }
    }

    // A donor is any saved character file whose target list is populated; the fullest wins.
    private static JsonArray? FindDonorList(IReadOnlyList<string> files)
    {
        JsonArray? best = null;
        foreach (var file in files)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject doc) continue;
                if (doc["GatherConfig"]?["targetNames"] is not JsonArray names || names.Count == 0) continue;
                if (best == null || names.Count > best.Count) best = names.DeepClone().AsArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
            { /* unreadable files are skipped; they are repaired from the default below */ }
        }
        return best is { Count: >= 10 } ? best : null; // a partial custom list must not overwrite the default selection
    }

    private static bool RepairFile(string path, JsonArray? donor)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject doc) return false;
            if (doc["GatherConfig"] is not JsonObject gather) return false; // no gather section; not this failure mode
            if (gather["targetNames"] is JsonArray existing && existing.Count > 0) return false;
            var replacement = donor;
            if (replacement == null)
            {
                var fallback = DefaultList();
                if (fallback.Count == 0) return false; // no saved list and no default: cannot repair
                replacement = fallback.DeepClone().AsArray();
            }
            if (replacement is null || replacement.Count == 0) return false;
            gather["targetNames"] = replacement;
            var temp = path + ".hexor-tmp";
            File.WriteAllText(temp, Json(doc), new System.Text.UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Text.Json.JsonException)
        { Diagnostics.Log("settings-repair", "repair-failed; file=" + Path.GetFileName(path) + "; type=" + e.GetType().Name); return false; }
        catch (Exception e)
        {
            // Never let a repair abort the sweep; report the unexpected shape so it can be reproduced.
            Diagnostics.Log("settings-repair", "repair-unexpected; file=" + Path.GetFileName(path) + "; type=" + e.GetType().Name + "; detail=" + (e.InnerException ?? e).Message);
            return false;
        }
    }

    // Built fresh per call so a malformed default can never break a sweep; the swept file is
    // written only after the replacement is verified non-empty.
    public static JsonArray DefaultList()
    {
        var names = new JsonArray();
        foreach (var (name, isChecked) in VendorDefaultTargets)
            names.Add(new JsonObject { ["checked"] = isChecked, ["name"] = name });
        return names;
    }

    // The saved defaults' gather section, in the module's own shape (verified against the
    // module's embedded default resource and live behavior).
    public static JsonObject DefaultConfigNode()
    {
        return new JsonObject
        {
            ["autoEnterAbyss"] = false,
            ["autoCounterAttack"] = true,
            ["counterAttackPickup"] = true,
            ["enableQualityFilter"] = false,
            ["qualityFilter"] = 2,
            ["patrolGatherEnabled"] = false,
            ["patrolWaypointsByMap"] = new JsonObject(),
            ["targetNames"] = DefaultList(),
        };
    }

    private static string Json(JsonNode node) => node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
