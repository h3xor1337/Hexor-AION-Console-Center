using System.IO;
using System.Linq;
using System.Text.Json;
using Hexor;
using Hexor.Compatibility;
using Microsoft.Win32;

internal static class LcRegistryFeatureTests
{
    internal sealed record GatheringFixture(string Names, string Targets);
    internal static GatheringFixture Fixture()
    {
        using var stream = typeof(LcRegistryFeatureTests).Assembly.GetManifestResourceStream("Hexor.Tests.LcGathering.json")
            ?? throw new InvalidOperationException("Gathering fixture missing.");
        return JsonSerializer.Deserialize<GatheringFixture>(stream)!;
    }

    internal static void SeedGathering(string branch)
    {
        if (!branch.StartsWith("Software\\HexorTests\\", StringComparison.Ordinal)) throw new InvalidOperationException("Isolated fixture key required.");
        var fixture = Fixture();
        using var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab");
        key.SetValue("GatherTargetNames", fixture.Names, RegistryValueKind.String);
        key.SetValue("GatherTargets", fixture.Targets, RegistryValueKind.String);
        key.SetValue("GatherQualityFilterLevel", 1, RegistryValueKind.DWord);
    }

    public static void Run(string root, Action<bool, string> check)
    {
        var fixture = Fixture();
        var document = new GatheringDocument(fixture.Names, fixture.Targets);
        check(document.Targets.Count == 24 && document.Targets.Count(t => t.Enabled == true) == 8, "Actual 24-resource reference parses all saved enabled/disabled flags");
        check(document.Targets.First().DisplayName == "Hidden Cube" && document.Targets.Last().DisplayName == "Pujery", "English names follow original target order");
        check(document.Targets[1].DisplayName.Contains("Empyrean Trace: Morheim"), "Long English display names survive parsing");
        check(document.Serialize() == fixture.Targets, "Actual gathering multiline text round-trips exactly including CRLF");
        var edited = document.WithSelection(0, false);
        check(edited == "0" + fixture.Targets[1..], "Single selection edit changes exactly one flag character");
        check(new GatheringDocument(fixture.Names, edited).WithSelection(0, true) == fixture.Targets, "Edit and reversal restore exact original Unicode text");
        var renamed = new GatheringDocument(fixture.Names.Replace("Hidden Cube", "Updated English name"), fixture.Targets);
        check(renamed.Targets[0].DisplayName == "Updated English name" && renamed.Serialize() == fixture.Targets, "English translation updates cannot change identifiers or selected states");
        // Built-in fallback: the vendor names lookup can miss entries (observed: 隱藏背包 missing); the
        // display must use the built-in English name and never the raw CJK identifier.
        var missingHiddenCube = new GatheringDocument(string.Join("\n", fixture.Names.Split("\r\n").Where(line => !line.StartsWith("隱藏背包", StringComparison.Ordinal))), fixture.Targets);
        check(missingHiddenCube.Targets[0].DisplayName == "Hidden Cube", "Missing vendor name entries fall back to the built-in English name");
        var unknownOnly = new GatheringDocument("\n", fixture.Targets + "\r\n1\t未知資源\n");
        check(unknownOnly.Targets.Last().DisplayName == "Untranslated resource (entry 25)", "Unknown identifiers keep the untranslated fallback and never show raw CJK");
        var reversedNames = string.Join("\n", fixture.Names.Split("\r\n").Reverse());
        check(new GatheringDocument(reversedNames, fixture.Targets).Targets.Select(t => t.Identifier).SequenceEqual(document.Targets.Select(t => t.Identifier)), "Name lookup reordering does not reorder target identities");
        var unusual = fixture.Targets + "\r\n1\t未知資源\n9\t未支援\r\nopaque future data\r\n\rduplicate\n0\t未知資源\r\n";
        var extended = new GatheringDocument(fixture.Names, unusual);
        check(extended.Serialize() == unusual && extended.Targets.Count == 27, "Unknown IDs, duplicate IDs, invalid flags, opaque lines and mixed newlines survive exact round-trip");
        check(extended.Targets[24].DisplayName.StartsWith("Untranslated resource") && extended.Targets[25].Enabled == null, "Unknown resources use English fallback; unsupported flags stay read-only");
        check(extended.WithSelection(extended.Targets[26].LineIndex, true).EndsWith("1\t未知資源\r\n"), "Duplicate identifiers are edited by original line without collapsing entries");
        check(new GatheringDocument("", "").Serialize() == "" && new GatheringDocument("", "opaque").Targets.Count == 0, "Empty and unrecognized lists remain lossless");

        var branch = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N");
        var audit = new List<string>();
        var registry = new RegistrySettingsService(branch, audit.Add);
        try
        {
            check(!registry.ReadGathering().CanEdit, "Missing gathering values are readable without fabrication");
            using (var key = Registry.CurrentUser.OpenSubKey(branch)) check(key == null, "Gathering read does not create missing registry keys");
            using var model = new MainViewModel(new JsonConfigurationService(Path.Combine(root, "lc-features.json"), new SettingsBackupService()), registry);
            var templatePairs = RegistryDefaults.Template.Select(t => (t.Section, t.Name)).ToHashSet();
            check(LegionCommanderFeatureSettings.Booleans.All(f => templatePairs.Contains((f.Section, f.Name)) == registry.Inspect(f.Section, f.Name).Exists),
                "Startup seeds exactly the supported template values (purchase toggles default OFF) and never fabricates a control beyond the template");
            check(new[]{ "CoinShopNightmareDaevanion", "CoinShopNightmareStigma", "CoinShopFestivalDaevanion", "CoinShopFestivalStigma", "CoinShopAbyssStigma" }.All(name => registry.Read("Enhance", name) == 0),
                "Startup seeds every Shop Exchange purchase toggle as DWORD OFF so the game cannot keep buying on its own defaults");
            check(!registry.ReadGathering().Targets.Exists,
                "Startup never seeds resource lists from the sample export");
            foreach (var feature in LegionCommanderFeatureSettings.Booleans)
            {
                var toggle = model.Toggles.Single(t => t.Section == feature.Section && t.Name == feature.Name);
                check(toggle.Value == false, "Missing optional Boolean displays OFF: " + feature.Name);
                toggle.Value = true;
                check(registry.Inspect(feature.Section, feature.Name) is { Supported: true, Value: 1 }, "Exact verified DWORD ON: " + feature.Name);
                toggle.Value = false;
                check(registry.Read(feature.Section, feature.Name) == 0, "Exact verified DWORD OFF: " + feature.Name);
                registry.WriteVerified(feature.Section, feature.Name, 1);
                model.ReloadRegistry();
                check(toggle.Value == true, "Existing ON value reloads: " + feature.Name);
                using var key = Registry.CurrentUser.OpenSubKey(branch + "\\" + feature.Section, true);
                key!.SetValue(feature.Name, "preserve", RegistryValueKind.String);
                toggle.Reload(); toggle.Value = false;
                check((string?)key.GetValue(feature.Name) == "preserve" && toggle.Value == null, "Unsupported Boolean type preserved: " + feature.Name);
                key.SetValue(feature.Name, 99, RegistryValueKind.DWord); toggle.Reload();
                check(toggle.Value == null && registry.Read(feature.Section, feature.Name) == 99, "Unknown Boolean DWORD preserved on load: " + feature.Name);
                key.SetValue(feature.Name, 1, RegistryValueKind.DWord);
            }
            check(model.Toggles.Single(t => t.Name == "AutoDungeonUrugugu").Label == "Uruguru Cave", "Uruguru Cave retains exact AutoDungeonUrugugu storage identifier");
            SeedGathering(branch);
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab")) key.SetValue("UnknownSibling", "保留\nunknown", RegistryValueKind.String);
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\Nightmare")) key.SetValue("id.synthetic.run", "unchanged", RegistryValueKind.String);
            model.ReloadRegistry();
            check(model.Gathering.Resources.Count == 24 && model.Gathering.Resources.Count(t => t.Enabled == true) == 8, "Gathering view model loads actual 24 saved selections");
            var firstRow = model.Gathering.Resources[0];
            model.Gathering.QualityLevel = "7";
            firstRow.Enabled = false;
            check(registry.ReadGathering().Targets.Value == edited && model.Gathering.Resources[0].Enabled == false, "Resource edit persists exact REG_SZ and reloads verified state");
            check(ReferenceEquals(firstRow, model.Gathering.Resources[0]) && model.Gathering.QualityLevel == "7", "Resource edits retain row identity and pending quality-level input");
            check(registry.ReadGathering().Names.Value == fixture.Names, "Resource edit never rewrites English/Chinese name dictionary");
            var unchanged = registry.ReadGathering();
            registry.WriteGatheringSelectionVerified(unchanged, 0, false);
            check(registry.ReadGathering() == unchanged && audit[^1].Contains("NoChange"), "Unchanged resource selection is verified without another write");
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab"))
                check(key!.GetValueKind("GatherTargets") == RegistryValueKind.String && (string?)key.GetValue("UnknownSibling") == "保留\nunknown", "Multiline REG_SZ type and unrelated Unicode sibling preserved");
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\Nightmare")) check((string?)key!.GetValue("id.synthetic.run") == "unchanged", "Nightmare runtime records untouched by feature edits");
            model.Gathering.SaveQualityLevel();
            check(registry.Read("ResourceGrab", "GatherQualityFilterLevel") == 7 && model.Gathering.QualityLevel == "7", "Numeric quality level writes exact DWORD and verifies");
            model.Gathering.QualityLevel = "4294967295"; model.Gathering.SaveQualityLevel();
            check(registry.Read("ResourceGrab", "GatherQualityFilterLevel") == -1 && model.Gathering.QualityLevel == "4294967295", "Unknown unsigned quality DWORD preserves full numeric range without invented enum");
            model.Gathering.QualityLevel = "4294967296"; model.Gathering.SaveQualityLevel();
            check(registry.Read("ResourceGrab", "GatherQualityFilterLevel") == -1, "Overflow quality input cannot change registry");
            check(model.CustomerStatus.StartsWith("Enter a numeric quality level"), "Rejected quality input never displays a successful save status");
            model.Gathering.QualityLevel = "-1"; model.Gathering.SaveQualityLevel();
            check(registry.Read("ResourceGrab", "GatherQualityFilterLevel") == -1, "Negative quality text rejected without a write");
            model.Gathering.Reload();
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true)) key!.SetValue("GatherQualityFilterLevel", 2, RegistryValueKind.DWord);
            model.Gathering.QualityLevel = "3"; model.Gathering.SaveQualityLevel();
            check(registry.Read("ResourceGrab", "GatherQualityFilterLevel") == 2 && model.Gathering.QualityLevel == "2" && model.Status.Contains("changed externally"), "Concurrent quality edits refresh the external value instead of overwriting it");

            var beforeExternal = registry.ReadGathering();
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true))
                key!.SetValue("GatherTargets", unusual, RegistryValueKind.String);
            try { registry.WriteGatheringSelectionVerified(beforeExternal, 0, true); throw new Exception("Stale gathering write accepted"); }
            catch (ConfigurationConflictException) { check(registry.ReadGathering().Targets.Value == unusual, "Stale resource edits cannot overwrite external selections"); }
            model.Gathering.Resources[0].Enabled = true;
            check(model.Status.Contains("changed externally") && model.Gathering.Resources.Count == 27 && registry.ReadGathering().Targets.Value == unusual, "UI conflict refreshes actual external values without silent overwrite");
            var staleNames = registry.ReadGathering();
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true))
                key!.SetValue("GatherTargetNames", fixture.Names.Replace("Hidden Cube", "New English name"), RegistryValueKind.String);
            try { registry.WriteGatheringSelectionVerified(staleNames, 0, false); throw new Exception("Stale name mapping accepted"); }
            catch (ConfigurationConflictException) { check(registry.ReadGathering().Targets.Value == unusual, "Concurrent translation changes also reject stale resource writes"); }
            model.Gathering.Reload();
            check(model.Gathering.Resources[0].DisplayName == "New English name" && model.Gathering.Resources[0].Enabled == true, "Updated translation still resolves original selected Chinese identity");

            // Vendor defaults parity: when GatherTargets was never saved, the built-in default
            // selection (decompiled _202F) is shown and the first save persists the full list.
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true)) key!.DeleteValue("GatherTargets", false);
            model.Gathering.Reload();
            var defaultRows = model.Gathering.Resources;
            check(defaultRows.Count == 24 && defaultRows.Count(r => r.Enabled == true) == 6
                && defaultRows[2].DisplayName == "Odyle" && defaultRows[2].Enabled == true
                && model.Gathering.Summary.Contains("Vendor default"),
                "Missing GatherTargets shows the vendor default selection with full display names");
            defaultRows[7].Enabled = true; // 雪莉 defaults to disabled; enabling it forces a write
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab"))
            {
                var saved = (string?)key!.GetValue("GatherTargets", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                check(key.GetValueKind("GatherTargets") == RegistryValueKind.String && saved != null
                    && saved.Split('\n').Length == 24 && saved.Split('\n')[0] == "0\t隱藏背包"
                    && saved.Split('\n').Any(line => line == "1\t雪莉"),
                    "Saving a default-list edit persists the complete vendor REG_SZ target list");
            }

            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true))
            {
                key!.SetValue("GatherQualityFilterLevel", "preserve", RegistryValueKind.String);
                key.SetValue("GatherTargets", new[] { "1\t隱藏背包" }, RegistryValueKind.MultiString);
            }
            model.Gathering.Reload();
            check(model.Gathering.Resources.Count == 0 && !model.Gathering.CanEditQuality, "Unsupported list and quality types remain read-only");
            model.Gathering.QualityLevel = "1"; model.Gathering.SaveQualityLevel();
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab"))
                check(key!.GetValueKind("GatherTargets") == RegistryValueKind.MultiString && (string?)key.GetValue("GatherQualityFilterLevel") == "preserve", "Invalid saved types survive attempted UI edits");
            check(audit.Any(line => line.Contains("ResourceGrab.GatherTargets")) && !audit.Any(line => line.Contains("隱藏背包") || line.Contains("Hidden Cube")), "New registry operations audit setting names without resource contents");
            var refused = new GatheringSettingsViewModel(new RefusedGathering(registry.ReadGathering() with { Targets = new(fixture.Targets, true, true) }), _ => { });
            refused.Reload(); refused.Resources[0].Enabled = false;
            check(refused.Resources[0].Enabled == true, "Failed gathering write restores actual state in UI");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(branch, false); }
    }

    private sealed class RefusedGathering(GatheringRegistrySnapshot snapshot) : IRegistrySettingsService
    {
        public int? Read(string section, string name) => null;
        public void WriteVerified(string section, string name, int value) => throw new IOException("Fixture access denied");
        public GatheringRegistrySnapshot ReadGathering() => snapshot;
        public void WriteGatheringSelectionVerified(GatheringRegistrySnapshot expected, int targetIndex, bool enabled) => throw new IOException("Fixture access denied");
    }
}
