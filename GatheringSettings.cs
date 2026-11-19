using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Hexor.Compatibility;
using Microsoft.Win32;

namespace Hexor;

public sealed record RegistryStringEntry(string? Value, bool Exists, bool Supported);
public sealed record GatheringRegistrySnapshot(RegistryStringEntry Names, RegistryStringEntry Targets)
{
    public bool CanEdit => Names is { Exists: true, Supported: true, Value: not null }
        && Targets is { Exists: true, Supported: true, Value: not null };
    // The installed vendor build falls back to its built-in default selection when GatherTargets
    // was never saved (decompiled _202F defaults). Hexor mirrors that: names present + targets
    // missing shows the editable defaults; the first save persists the full vendor-format list.
    public bool CanEditDefaults => Names is { Exists: true, Supported: true, Value: not null }
        && Targets is { Exists: false, Supported: true };
}

public sealed record GatheringTarget(int LineIndex, string Identifier, string DisplayName, bool? Enabled);

// Built-in default selection of the installed LegionCommander build (decompiled _202F): shown
// when GatherTargets was never saved. Identifiers are the saved GatherTargetNames keys; Hexor
// renders the display names (Odyle, Ruby, ...) through that lookup.
public static class GatheringDefaults
{
    public static (string Identifier, bool Enabled)[] Entries { get; } =
    [
        ("隱藏背包", false), ("主神痕跡", false), ("奧德", true), ("紅寶石", true),
        ("藍寶石", true), ("鑽石", true), ("奧里哈康", true), ("雪莉", false),
        ("梅拉", false), ("雲芝", false), ("龍蒿", false), ("當歸", false),
        ("齊爾利", false), ("庫庫羅", false), ("天涯草", false), ("菩提", false),
        ("麻痺草", false), ("金盞花", false), ("魔爾菇", false), ("萊森樹莓", false),
        ("珂尼玳", false), ("秋葵", false), ("世界樹", true), ("波齊鯉", false),
    ];

    // Vendor REG_SZ format: "1\t<identifier>" per line.
    public static string TargetsText() => string.Join("\n", Entries.Select(entry => (entry.Enabled ? "1" : "0") + "\t" + entry.Identifier));
}

// Names are a lookup only. Targets own ordering, identity and the exact saved text.
public sealed class GatheringDocument
{
    private sealed record Line(string Text, string Ending);
    private readonly List<Line> lines;
    public IReadOnlyList<GatheringTarget> Targets { get; }
    public int UnrecognizedLines { get; }

    public GatheringDocument(string names, string targets)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in SplitLines(names))
        {
            // The saved names value may carry a UTF-8 BOM on its first key (observed on the live machine).
            var key = line.Text.TrimStart('\uFEFF');
            var tab = key.IndexOf('\t');
            if (tab > 0 && tab < key.Length - 1)
                translations.TryAdd(key[..tab], key[(tab + 1)..]);
        }
        lines = SplitLines(targets);
        var entries = new List<GatheringTarget>();
        var unrecognized = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var text = lines[index].Text;
            var tab = text.IndexOf('\t');
            if (tab != 1 || text.Length < 3 || text.IndexOf('\t', 2) >= 0)
            {
                if (text.Length != 0) unrecognized++;
                continue;
            }
            var id = text[2..];
            bool? enabled = text[0] switch { '0' => false, '1' => true, _ => null };
            if (enabled == null) unrecognized++;
            var label = translations.TryGetValue(id, out var translated) ? translated : $"Untranslated resource (entry {entries.Count + 1})";
            entries.Add(new(index, id, label, enabled));
        }
        Targets = entries.AsReadOnly();
        UnrecognizedLines = unrecognized;
    }

    public string Serialize() => string.Concat(lines.Select(x => x.Text + x.Ending));

    public string WithSelection(int lineIndex, bool enabled)
    {
        if (!Targets.Any(x => x.LineIndex == lineIndex && x.Enabled.HasValue))
            throw new InvalidDataException("Unsupported gathering entry preserved.");
        var result = new StringBuilder();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            result.Append(index == lineIndex ? (enabled ? "1" : "0") + line.Text[1..] : line.Text);
            result.Append(line.Ending);
        }
        return result.ToString();
    }

    private static List<Line> SplitLines(string text)
    {
        var result = new List<Line>();
        foreach (Match match in Regex.Matches(text, @"([^\r\n]*)(\r\n|\r|\n|$)"))
            if (match.Length > 0) result.Add(new(match.Groups[1].Value, match.Groups[2].Value));
        return result;
    }
}

public sealed partial class RegistrySettingsService
{
    private static RegistryStringEntry InspectString(RegistryKey? key, string name)
    {
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(null, false, true);
        return key.GetValueKind(name) != RegistryValueKind.String ? new(null, true, false)
            : new(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, true, true);
    }

    public void WriteGatheringTargetsVerified(GatheringRegistrySnapshot expected, string targets) =>
        StorageAudit.Run(StorageTarget.Registry, ["ResourceGrab.GatherTargets"], () =>
        {
            if (!expected.CanEdit && !expected.CanEditDefaults) throw new InvalidDataException("Missing or unsupported gathering values preserved.");
            using var mutex = new Mutex(false, "Local\\Hexor.LegionCommander.GatheringEdit");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Gathering settings are busy. Try again.");
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = hive.OpenSubKey(root + "\\ResourceGrab", writable: true);
                var current = new GatheringRegistrySnapshot(InspectString(key, "GatherTargetNames"), InspectString(key, "GatherTargets"));
                if (current != expected) throw new ConfigurationConflictException();
                if (expected.Targets.Exists && expected.Targets.Value == targets) return false;
                key!.SetValue("GatherTargets", targets, RegistryValueKind.String);
                var verified = ReadGathering();
                if (verified.Names != expected.Names || verified.Targets != new RegistryStringEntry(targets, true, true))
                    throw new IOException("Gathering write could not be verified. Reload settings.");
                LegionSharedSettings.SyncFromRegistry("ResourceGrab", "GatherTargets", root);
                return true;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }, changed => changed, auditSink);

    public GatheringRegistrySnapshot ReadGathering()
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(root + "\\ResourceGrab");
        return new(InspectString(key, "GatherTargetNames"), InspectString(key, "GatherTargets"));
    }

    public void WriteGatheringSelectionVerified(GatheringRegistrySnapshot expected, int targetIndex, bool enabled) =>
        StorageAudit.Run(StorageTarget.Registry, ["ResourceGrab.GatherTargets"], () =>
        {
            if (!expected.CanEdit && !expected.CanEditDefaults) throw new InvalidDataException("Missing or unsupported gathering values preserved.");
            var targetsValue = expected.Targets.Value ?? GatheringDefaults.TargetsText();
            var updated = new GatheringDocument(expected.Names.Value!, targetsValue).WithSelection(targetIndex, enabled);
            using var mutex = new Mutex(false, "Local\\Hexor.LegionCommander.GatheringEdit");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("Gathering settings are busy. Try again.");
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = hive.OpenSubKey(root + "\\ResourceGrab", writable: true);
                var current = new GatheringRegistrySnapshot(InspectString(key, "GatherTargetNames"), InspectString(key, "GatherTargets"));
                if (current != expected) throw new ConfigurationConflictException();
                if (expected.Targets.Exists && updated == expected.Targets.Value) return false;
                key!.SetValue("GatherTargets", updated, RegistryValueKind.String);
                var verified = ReadGathering();
                if (verified.Names != expected.Names || verified.Targets != new RegistryStringEntry(updated, true, true))
                    throw new IOException("Gathering write could not be verified. Reload settings.");
                LegionSharedSettings.SyncFromRegistry("ResourceGrab", "GatherTargets", root);
                return true;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }, changed => changed, auditSink);
}
