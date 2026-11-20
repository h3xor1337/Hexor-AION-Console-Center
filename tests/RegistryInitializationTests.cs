using System.Diagnostics;
using System.IO;
using Hexor;
using Microsoft.Win32;

internal static class RegistryInitializationTests
{
    public static void Run(string temporaryRoot, string registryRoot, Action<bool,string> check)
    {
        var branch = registryRoot + "\\Initialization";
        void Reset() => Registry.CurrentUser.DeleteSubKeyTree(branch, false);
        var registry = new RegistrySettingsService(branch);
        var service = new RegistryInitializationService(registry);
        Reset();

        check(service.HasMissingValues(), "Missing LegionCommander registry root is detected without creating it");
        using (var key = Registry.CurrentUser.OpenSubKey(branch)) check(key == null, "Registry inspection does not create keys");
        var beforeProcesses = Process.GetProcessesByName("LegionCommander").Select(p => p.Id).ToHashSet();
        var result = service.Ensure();
        check(result.Success, "Fresh isolated registry initialization succeeds");
        foreach (var item in RegistryDefaults.Template)
        {
            var entry = registry.Inspect(item.Section, item.Name);
            check(entry.Exists && entry.Supported && entry.Value == item.InitializationDefault, "Missing DWORD is created and read back: " + item.Section + "." + item.Name);
        }
        check(RegistryDefaults.Template.Where(t => t.Type == RegistrySettingType.BooleanDWord).All(t => t.InitializationDefault == 0),
            "Every missing canonical Boolean defaults OFF");
        check(registry.Read("MiniGame", "AutoEnter") == 0, "Fresh customer installation creates Shugo Auto Enter as DWORD OFF");
        check(registry.Read("ResourceGrab", "AbyssLayer") == 21 && registry.Read("ResourceGrab", "AutoDungeonMode") == 0,
            "Missing resource choices default to Lower 21 and Easy 0");
        check(registry.Read("ResourceGrab", "AuctionAutoSell") == 0 && registry.Read("ResourceGrab", "AuctionPriceMode") == 1,
            "Missing auction settings default to OFF and Average");
        check(registry.Read("Enhance", "DestroyInsteadOfExtract") == 0, "Fresh customer installation creates Equipment Extraction as DWORD OFF");
        check(new[]{ "CoinShopNightmareDaevanion", "CoinShopNightmareStigma", "CoinShopFestivalDaevanion", "CoinShopFestivalStigma", "CoinShopAbyssStigma", "EnchantBoundKinahOnly", "GenusInsightAutoAnalyze" }.All(name => registry.Read("Enhance", name) == 0),
            "Fresh installation creates every Shop Exchange and Enhance toggle as DWORD OFF so a customer who never opened the vendor console does not keep buying");
        check(new[]{ "CoinShopNightmareDaevanion", "CoinShopNightmareStigma", "CoinShopFestivalDaevanion", "CoinShopFestivalStigma", "CoinShopAbyssStigma" }.All(name => Hexor.Compatibility.LegionSharedSettings.Mappings.Any(m => m.Group == "shop" && m.Group == "shop" && m.Section == "Enhance" && m.ValueName == name)),
            "Every Shop Exchange purchase toggle is mapped into the shared shop document");
        check(registry.Read("ResourceGrab", "AutoDungeonUrugugu") == 0, "Missing dungeon preference values are created with safe defaults");
        // The in-game Target Filter owns the gather settings; the console neither seeds nor writes them.
        check(registry.ReadAppliedSchemaVersion() == LegionCommanderRegistrySchema.Version, "Successful startup records the embedded schema version in Hexor-owned metadata");

        // One-time legacy gather registry migration: the stale list that fed the wipe cycle is
        // removed so the game module rebuilds its own defaults; the run is marked and never
        // repeats, so values the vendor console saves afterwards are preserved.
        // Simulate an upgrading customer: their install predates the migration marker.
        Registry.CurrentUser.DeleteSubKeyTree(branch + @"\_HexorSchema", false);
        using (var key = Registry.CurrentUser.CreateSubKey(branch + @"\ResourceGrab", writable: true))
        {
            key.SetValue("GatherTargets", "1\t奧德\n0\t紅寶石", RegistryValueKind.String);
            key.SetValue("GatherTargetNames", "奧德\tOdyle", RegistryValueKind.String);
            key.SetValue("GatherQualityFilterEnabled", 1, RegistryValueKind.DWord);
        }
        service.Ensure();
        using (var key = Registry.CurrentUser.OpenSubKey(branch + @"\ResourceGrab"))
        {
            check(key!.GetValue("GatherTargets") == null && key.GetValue("GatherTargetNames") == null && key.GetValue("GatherQualityFilterEnabled") == null,
                "The first startup removes the stale legacy gather list and its filter values");
            check(key.GetValue("AuctionAutoSell") != null, "Non-gather ResourceGrab values survive the migration");
        }
        check(registry.ReadGatherMigrationDone(), "The gather registry migration is recorded as done");
        using (var key = Registry.CurrentUser.CreateSubKey(branch + @"\ResourceGrab", writable: true))
            key.SetValue("GatherTargets", "1\t世界樹", RegistryValueKind.String);
        service.Ensure();
        using (var key = Registry.CurrentUser.OpenSubKey(branch + @"\ResourceGrab"))
            check(key!.GetValue("GatherTargets") as string == "1\t世界樹",
                "After the one-time migration, values the vendor console saves later are never touched");

        // Retired-feature push: a value a customer previously enabled is deleted on startup.
        using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab", writable: true)) key.SetValue("GuardianLogoutAvoidance", 1, RegistryValueKind.DWord);
        check(registry.Inspect("ResourceGrab", "GuardianLogoutAvoidance").Value == 1, "Retired setting is present before the removal pass");
        result = service.Ensure();
        check(result.Success, "Startup succeeds after removing a retired setting");
        check(!registry.Inspect("ResourceGrab", "GuardianLogoutAvoidance").Exists, "Retired Guardian logout-avoidance value is deleted from the registry on startup");
        check(result.RetiredValuesRemoved >= 1, "The removal pass reports the retired values it deleted");
        check(!registry.Inspect("AutoTask", "CharacterRace").Exists && !registry.Inspect("AutoTask", "CharacterJob").Exists && !registry.Inspect("AutoTask", "AutoDailyQuests").Exists,
            "Character identity and unverified daily-task state are never initialized");
        check(Process.GetProcessesByName("LegionCommander").All(p => beforeProcesses.Contains(p.Id)), "Registry initialization does not launch LegionCommander");

        Reset();
        using (var root = Registry.CurrentUser.CreateSubKey(branch)) root.CreateSubKey("AutoTask")!.Dispose();
        result = service.Ensure();
        check(result.Success && RegistryDefaults.Template.Where(t => t.Section == "AutoTask").All(t => registry.Read(t.Section, t.Name) == t.InitializationDefault), "Missing AutoTask subkey is populated with safe OFF defaults");

        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask", writable: true)) key!.DeleteValue("ExploreCavesBeforeDungeon");
        result = service.Ensure();
        check(result.Success && registry.Read("AutoTask", "ExploreCavesBeforeDungeon") == 0, "Missing Boolean value is restored OFF without resetting siblings");

        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\Enhance", writable: true)) key!.DeleteValue("DestroyInsteadOfExtract");
        result = service.Ensure();
        check(result.Success && registry.Read("Enhance", "DestroyInsteadOfExtract") == 0, "Missing Equipment Extraction value is created as DWORD OFF");
        registry.WriteVerified("Enhance", "DestroyInsteadOfExtract", 1);
        result = service.Ensure();
        check(result.Success && registry.Read("Enhance", "DestroyInsteadOfExtract") == 1, "Existing Equipment Extraction DWORD ON is preserved at startup");
        registry.WriteVerified("Enhance", "DestroyInsteadOfExtract", 0);
        result = service.Ensure();
        check(result.Success && registry.Read("Enhance", "DestroyInsteadOfExtract") == 0, "Existing Equipment Extraction DWORD OFF is preserved at startup");
        registry.WriteVerified("Enhance", "DestroyInsteadOfExtract", 1);
        check(registry.Read("Enhance", "DestroyInsteadOfExtract") == 1, "Equipment Extraction toggle writes DWORD ON");
        registry.WriteVerified("Enhance", "DestroyInsteadOfExtract", 0);
        check(registry.Read("Enhance", "DestroyInsteadOfExtract") == 0, "Equipment Extraction toggle writes DWORD OFF");

        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask", writable: true)) { key!.SetValue("ClearSideQuestsBeforeDungeon", 1, RegistryValueKind.DWord); key.SetValue("ExploreCavesBeforeDungeon", 0, RegistryValueKind.DWord); key.SetValue("CharacterRace", 2, RegistryValueKind.DWord); key.SetValue("CharacterJob", 7, RegistryValueKind.DWord); key.SetValue("AutoDailyQuests", 1, RegistryValueKind.DWord); key.SetValue("CustomerOnly", "retain"); }
        using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\Enhance")) key.SetValue("AutoEnchantMaxPower", 1200, RegistryValueKind.DWord);
        using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\Nightmare")) key.SetValue("RuntimeRecord", "opaque", RegistryValueKind.String);
        result = service.Ensure();
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask"))
            check(result.Success && registry.Read("AutoTask", "ClearSideQuestsBeforeDungeon") == 1 && registry.Read("AutoTask", "ExploreCavesBeforeDungeon") == 0 && (int?)key!.GetValue("CharacterRace") == 2 && (int?)key.GetValue("CharacterJob") == 7 && (int?)key.GetValue("AutoDailyQuests") == 1 && (string?)key.GetValue("CustomerOnly") == "retain", "Enabled, disabled, numeric identity, daily-task, and unrelated customer values are preserved");
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\Nightmare")) check((string?)key!.GetValue("RuntimeRecord") == "opaque", "Nightmare runtime records remain untouched");

        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask", writable: true)) key!.SetValue("SwitchToGatherAfterTasks", "opaque", RegistryValueKind.String);
        result = service.Ensure();
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask"))
            check(result.Success && result.InvalidValuesDetected == 1 && key!.GetValueKind("SwitchToGatherAfterTasks") == RegistryValueKind.String, "Malformed registry type is reported and preserved without corrupting synchronization");

        Reset(); result = service.Ensure(); var second = service.Ensure();
        check(result.Success && second.Success && !service.HasMissingValues(), "Repeated startup initialization is idempotent");
        check(second.MissingSettingsInitialized == 0 && second.ExistingSettingsPreserved == RegistryDefaults.Template.Count, "Repeated and manual synchronization perform no unexpected writes");

        var writes = new List<string>();
        var auditedRegistry = new RegistrySettingsService(branch, writes.Add);
        new RegistryInitializationService(auditedRegistry).Ensure();
        check(writes.Count == 0, "Matching startup values cause no setting write attempts");
        foreach (var definition in RegistryDefaults.Template.Where(t => !t.ResetOnStartup && t.Type == RegistrySettingType.BooleanDWord))
            registry.WriteVerified(definition.Section, definition.Name, 1);
        registry.WriteVerified("ResourceGrab", "AuctionAutoSell", 1);
        registry.WriteVerified("ResourceGrab", "AuctionPriceMode", 0);
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true))
        {
            key!.SetValue("AbyssLayer", 22, RegistryValueKind.DWord);
            key.SetValue("AutoDungeonMode", 1, RegistryValueKind.DWord);
            key.SetValue("CustomerResourceValue", "retain", RegistryValueKind.String);
        }
        writes.Clear(); result = new RegistryInitializationService(auditedRegistry).Ensure();
        check(result.Success && result.StartupDefaultsRestored == 2 && writes.Count == 4,
            "Startup changes exactly the two mismatched auction settings with read-back");
        check(RegistryDefaults.Template.Where(t => !t.ResetOnStartup && t.Type == RegistrySettingType.BooleanDWord).All(t => registry.Read(t.Section, t.Name) == 1),
            "Startup preserves every enabled customer Boolean outside Auto Auction");
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab"))
            check((int?)key!.GetValue("AbyssLayer") == 22 && (int?)key.GetValue("AutoDungeonMode") == 1 && (string?)key.GetValue("CustomerResourceValue") == "retain",
                "Startup preserves existing resource choices and unrelated customer settings");
        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab", true))
        {
            key!.SetValue("AuctionAutoSell", "enabled", RegistryValueKind.String);
            key.SetValue("AuctionPriceMode", 0L, RegistryValueKind.QWord);
        }
        result = service.Ensure();
        check(result.Success && result.StartupDefaultsRestored == 2 && registry.Inspect("ResourceGrab", "AuctionAutoSell") == new RegistryEntry(0, true, true)
            && registry.Inspect("ResourceGrab", "AuctionPriceMode") == new RegistryEntry(1, true, true),
            "Auction startup exception replaces malformed types with exact DWORD defaults");
        try { registry.RestoreStartupDefault("MiniGame", "AutoEnter"); check(false, "Non-auction reset rejected"); }
        catch (InvalidOperationException) { check(registry.Read("MiniGame", "AutoEnter") == 1, "Startup reset API rejects settings outside the two auction exceptions"); }
        registry.WriteVerified("ResourceGrab", "AuctionAutoSell", 1); registry.WriteVerified("ResourceGrab", "AuctionPriceMode", 0);
        using (var restarted = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "auction-restart-preferences.json"), new SettingsBackupService()), auditedRegistry))
        {
            check(restarted.Toggles.Single(t => t.Name == "AuctionAutoSell").Value == false && restarted.AuctionPriceMode == 1
                && registry.Read("ResourceGrab", "AuctionAutoSell") == 0 && registry.Read("ResourceGrab", "AuctionPriceMode") == 1,
                "Application restart resets Auto Auction and refreshes UI from the actual registry");
            check(restarted.Toggles.Single(t => t.Name == "AutoEnterAbyss").Value == true && restarted.AbyssLayer == -1
                && restarted.DungeonMode == 1, "Resource UI reflects preserved customer values after initialization");
            restarted.AuctionPriceMode = 0;
            check(registry.Read("ResourceGrab", "AuctionPriceMode") == 0, "Auction price edit writes and verifies Lowest until the next startup");
            restarted.InitializeRegistryDefaults();
            check(restarted.AuctionPriceMode == 1, "Explicit initialization reloads the restored Average selection");
            restarted.AbyssLayer = 1; restarted.DungeonMode = 0;
            check(registry.Read("ResourceGrab", "AbyssLayer") == 23 && registry.Read("ResourceGrab", "AutoDungeonMode") == 0
                && restarted.AbyssLayer == 1 && restarted.DungeonMode == 0, "Middle and Krao Cave selections write exact DWORD encodings and refresh UI");
            restarted.AbyssLayer = 0; restarted.DungeonMode = 1;
            check(registry.Read("ResourceGrab", "AbyssLayer") == 21 && registry.Read("ResourceGrab", "AutoDungeonMode") == 1
                && restarted.AbyssLayer == 0 && restarted.DungeonMode == 1, "Lower and Draupnir Cave selections write exact DWORD encodings and refresh UI");
            restarted.AbyssLayer = 1;
            restarted.InitializeRegistryDefaults();
            check(restarted.AbyssLayer == 1 && restarted.DungeonMode == 1, "Initialization preserves Middle and Draupnir Cave choices");
            var beforeNoChange = writes.Count;
            restarted.AbyssLayer = 1; restarted.DungeonMode = 1;
            check(writes.Count == beforeNoChange, "Matching resource selections avoid redundant writes");
            restarted.AbyssLayer = -1; restarted.DungeonMode = 2;
            check(registry.Read("ResourceGrab", "AbyssLayer") == 23 && registry.Read("ResourceGrab", "AutoDungeonMode") == 1,
                "Invalid resource selection indices cannot alter saved preferences");
            using var resourceKey = Registry.CurrentUser.OpenSubKey(branch + "\\ResourceGrab");
            check(resourceKey!.GetValueKind("AbyssLayer") == RegistryValueKind.DWord && resourceKey.GetValueKind("AutoDungeonMode") == RegistryValueKind.DWord
                && (string?)resourceKey.GetValue("CustomerResourceValue") == "retain" && registry.Read("ResourceGrab", "AutoEnterAbyss") == 1,
                "Resource preference edits preserve unrelated values and keep exact DWORD types");
        }
        using (var restarted = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "resource-restart-preferences.json"), new SettingsBackupService()), registry))
            check(restarted.AbyssLayer == 1 && restarted.DungeonMode == 1 && registry.Read("ResourceGrab", "AbyssLayer") == 23,
                "Application restart loads and preserves existing Middle and Draupnir Cave values");
        try { registry.WriteVerified("ResourceGrab", "AbyssLayer", 22); check(false, "Unverified Abyss layer encoding rejected"); }
        catch (InvalidDataException) { check(registry.Read("ResourceGrab", "AbyssLayer") == 23, "Only exact Abyss layer DWORD encodings 21 and 23 are writable"); }
        using (var deniedVm = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "resource-denied-preferences.json"), new SettingsBackupService()), new DeniedRegistry()))
        {
            deniedVm.AbyssLayer = 1; deniedVm.DungeonMode = 1;
            check(deniedVm.AbyssLayer == -1 && deniedVm.DungeonMode == -1, "Failed resource writes reload actual missing values without displaying a successful selection");
        }

        Reset(); service.Ensure();

        registry.WriteVerified("Enhance", "DestroyInsteadOfExtract", 1);
        using (var restarted = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "equipment-restart-preferences.json"), new SettingsBackupService()), new RegistrySettingsService(branch)))
            check(restarted.Toggles.Single(t => t.Name == "DestroyInsteadOfExtract").Value == true && registry.Read("Enhance", "DestroyInsteadOfExtract") == 1,
                "Application restart reloads and preserves Equipment Extraction ON");

        Reset();
        var concurrent = Enumerable.Range(0, 2).Select(_ => Task.Run(() => service.Ensure())).ToArray(); Task.WaitAll(concurrent);
        check(concurrent.All(t => t.Result.Success) && RegistryDefaults.Template.All(t => registry.Read(t.Section, t.Name) == t.InitializationDefault), "Concurrent startup synchronization is serialized and deterministic");

        Reset();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path unavailable.");
        var processes = Enumerable.Range(0, 2).Select(_ =>
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--registry-sync-child"); start.ArgumentList.Add(branch);
            return Process.Start(start) ?? throw new InvalidOperationException("Registry synchronization child did not start.");
        }).ToArray();
        foreach (var process in processes) { process.WaitForExit(); }
        check(processes.All(p => p.ExitCode == 0) && RegistryDefaults.Template.All(t => registry.Read(t.Section, t.Name) == t.InitializationDefault),
            "Cross-process startup synchronization is mutex-serialized and deterministic");
        foreach (var process in processes) process.Dispose();

        registry.WriteVerified("Enhance", "AutoNightmareEnabled", 1); registry.WriteAppliedSchemaVersion(0);
        result = service.Ensure();
        check(result.Success && result.PreviousSchemaVersion == 0 && registry.Read("Enhance", "AutoNightmareEnabled") == 1 && registry.ReadAppliedSchemaVersion() == LegionCommanderRegistrySchema.Version, "OTA-style schema migration preserves existing values and advances metadata");

        using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\Enhance", writable: true)) key!.SetValue("DestroyInsteadOfExtract", 2, RegistryValueKind.DWord);
        result = service.Ensure();
        check(result.Success && result.InvalidValuesDetected == 1 && registry.Read("Enhance", "DestroyInsteadOfExtract") == 2,
            "Invalid Equipment Extraction DWORD is reported and preserved for customer review");

        var denied = new RegistryInitializationService(new DeniedRegistry());
        result = denied.Ensure();
        check(!result.Success && result.Issues.Count == RegistryDefaults.Template.Count + LegionCommanderRegistrySchema.RetiredValues.Count, "Registry access-denied failures are reported without claiming success");

        Reset();
        using (var vm = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "initializer-preferences.json"), new SettingsBackupService()), new RegistrySettingsService(branch)))
        {
            check(!vm.NeedsRegistryInitialization() && vm.Toggles.Where(t => t.Section == "AutoTask").All(t => t.IsEditable && t.Value != null), "Startup initializes and refreshes Task behavior controls without a configuration folder");
        }
        var logs = new[] { Diagnostics.ActiveLogPath }.Where(File.Exists).Concat(Directory.EnumerateFiles(Diagnostics.Root, "hexor-*.log")).Select(File.ReadAllText).ToArray();
        check(logs.Any(log => log.Contains("registry-synchronization") && log.Contains("schema-version") && log.Contains("settings-refresh")), "Registry synchronization emits versioned sanitized lifecycle checkpoints");
        Reset();
    }

    private sealed class DeniedRegistry : IRegistrySettingsService
    {
        public int? Read(string section, string name) => null;
        public void WriteVerified(string section, string name, int value) => throw new UnauthorizedAccessException();
        public RegistryEntry Inspect(string section, string name) => new(null, false, true);
        public bool CreateMissingDefault(string section, string name, int value) => throw new UnauthorizedAccessException();
    }
}
