using System.Reflection;
using System.IO;

namespace Hexor;

public enum RegistrySettingType { BooleanDWord, IntegerDWord }
public enum RegistryOwnership { HexorSupported, VendorManaged }

public sealed record RegistryDefinition(string Section, string Name, RegistrySettingType Type, int InitializationDefault,
    int Minimum, int Maximum, string UiBinding, int MigrationVersion, RegistryOwnership Ownership, bool ResetOnStartup = false)
{
    public bool IsValid(int value) => Section == "ResourceGrab" && Name == "AbyssLayer" ? value is 21 or 23 : value >= Minimum && value <= Maximum;
}

public sealed record RegistrySynchronizationResult(int SettingsChecked, int MissingSettingsInitialized,
    int ExistingSettingsPreserved, int InvalidValuesDetected, int ErrorsEncountered, int PreviousSchemaVersion,
    int SchemaVersion, IReadOnlyList<string> Issues)
{
    public int StartupDefaultsRestored { get; init; }
    public int RetiredValuesRemoved { get; init; }
    public bool Success => ErrorsEncountered == 0;
    public string Message => $"Legion Commander settings synchronized. Checked: {SettingsChecked}; initialized: {MissingSettingsInitialized}; startup defaults restored: {StartupDefaultsRestored}; retired removed: {RetiredValuesRemoved}; preserved: {ExistingSettingsPreserved}; invalid: {InvalidValuesDetected}; errors: {ErrorsEncountered}.";
}

// Reference exports establish names and types only; every newly introduced Boolean initializes OFF.
public static class LegionCommanderRegistrySchema
{
    public const int Version = 4;
    public static IReadOnlyList<RegistryDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        Bool("AutoTask", "ClearSideQuestsBeforeDungeon", "Auto Task / Clear side quests"),
        Bool("AutoTask", "ExploreCavesBeforeDungeon", "Auto Task / Explore caves"),
        Bool("AutoTask", "SwitchToGatherAfterTasks", "Auto Task / Switch to gathering"),
        Number("Enhance", "AutoEnchantMaxPower", 1500, 1100, 1500, "Character Enhancement / Gear enhancement level"),
        Bool("Enhance", "AutoBuyOdEnergy", "Membership Benefits / Buy Od Energy"),
        Bool("Enhance", "AutoBuyReviveStone", "Membership Benefits / Buy Resurrection Stones"),
        Bool("Enhance", "AutoDungeonQuickJoinEnabled", "Auto Dungeon / Quick Join"),
        Bool("Enhance", "AutoDailyWaveEnabled", "Auto Dungeon / Daily Wave"),
        Bool("Enhance", "AutoNightmareEnabled", "Auto Dungeon / Nightmare"),
        Bool("Enhance", "AutoAwakenEnabled", "Auto Dungeon / Awaken"),
        Bool("Enhance", "AutoOdMorphEnabled", "Auto Dungeon / Od Morph"),
        Number("Enhance", "AutoUpgradeMaxBattlePowerK", 100, 1, 100, "Character Enhancement / Maximum battle power"),
        Bool("Enhance", "DestroyInsteadOfExtract", "Equipment Extraction / Destroy instead of extracting"),
        // Coin-shop and remaining Enhance booleans must exist for customers who never opened the
        // vendor console: when a value is missing the game falls back to its own default, which
        // keeps purchases running even though every toggle shows disabled.
        Bool("Enhance", "EnchantBoundKinahOnly", "Enchant using bound Kinah only"),
        Bool("Enhance", "GenusInsightAutoAnalyze", "Genus Insight / Automatically analyze"),
        Bool("Enhance", "CoinShopNightmareDaevanion", "Shop Exchange / Buy Daevanion items with Nightmare coins"),
        Bool("Enhance", "CoinShopNightmareStigma", "Shop Exchange / Buy Stigma items with Nightmare coins"),
        Bool("Enhance", "CoinShopFestivalDaevanion", "Shop Exchange / Buy Daevanion items with Festival coins"),
        Bool("Enhance", "CoinShopFestivalStigma", "Shop Exchange / Buy Stigma items with Festival coins"),
        Bool("Enhance", "CoinShopAbyssStigma", "Shop Exchange / Buy Stigma items with Abyss coins"),
        Bool("ResourceGrab", "AutoDungeonUrugugu", "Auto Dungeon / Prefer Urugugu"),
        Bool("ResourceGrab", "GatherIgnoreAether", "Resource Gathering / Ignore Aether"),
        Bool("ResourceGrab", "GatherQualityFilterEnabled", "Resource Gathering / Quality filter"),
        Number("ResourceGrab", "GatherQualityFilterLevel", 1, 1, 99, "Resource Gathering / Quality filter level"),
        Bool("MiniGame", "AutoEnter", "Mini Games / Auto Enter"),
        Bool("MiniGame", "UseTaiwanVillageRules", "Mini Games / Taiwan village rules"),
        Bool("ResourceGrab", "AutoEnterAbyss", "Resource Gathering / Enter Abyss"),
        Number("ResourceGrab", "AbyssLayer", 21, 21, 23, "Resource Gathering / Abyss layer"),
        Bool("ResourceGrab", "AbyssCounterAttack", "Resource Gathering / Counterattack"),
        Bool("ResourceGrab", "AuctionAutoSell", "Auto Auction / Auto sell") with { ResetOnStartup = true },
        Number("ResourceGrab", "AuctionPriceMode", 1, 0, 1, "Auto Auction / Price mode") with { ResetOnStartup = true },
        Number("ResourceGrab", "AutoDungeonMode", 0, 0, 1, "Auto Dungeon / Dungeon mode")
    });
    // Removed features: values are deleted from every installation on startup and the shared
    // game documents are rebuilt without them, so the running game no longer applies them.
    public static readonly IReadOnlyList<(string Section, string Name)> RetiredValues = new[] { ("ResourceGrab", "GuardianLogoutAvoidance") };
    public static IReadOnlyList<string> RetiredGroups => new[] { "abyss" };
    private static RegistryDefinition Bool(string section, string name, string binding, int value = 0) => new(section, name, RegistrySettingType.BooleanDWord, value, 0, 1, binding, Version, RegistryOwnership.HexorSupported);
    private static RegistryDefinition Number(string section, string name, int value, int minimum, int maximum, string binding) => new(section, name, RegistrySettingType.IntegerDWord, value, minimum, maximum, binding, Version, RegistryOwnership.HexorSupported);
    public static RegistryDefinition? Find(string section, string name) => Definitions.FirstOrDefault(x => x.Section == section && x.Name == name);
}

// Compatibility facade retained for existing callers and publishing-contract tests.
public static class RegistryDefaults
{
    public static IReadOnlyList<RegistryDefinition> Template => LegionCommanderRegistrySchema.Definitions;
    public static bool IsValid(string section, string name, int value) => LegionCommanderRegistrySchema.Find(section, name)?.IsValid(value) == true;
}

public sealed class RegistryInitializationService(IRegistrySettingsService registry)
{
    private const string MutexName = "Local\\Hexor.LegionCommander.RegistrySynchronization";
    public bool HasMissingValues() => RegistryDefaults.Template.Any(t => !registry.Inspect(t.Section, t.Name).Exists);

    // The running game reads its behavior from the shared documents; rebuilding the affected
    // groups without retired keys is what actually disables the removed feature in-game.
    private void RebuildRetiredSharedGroups(IRegistrySettingsService registry)
    {
        var liveRoot = registry.RegistryRoot == @"Software\LegionCommander";
        if (!liveRoot && Hexor.Compatibility.LegionSharedSettings.SharedDirectoryOverride is null) return; // disposable test branches never touch the live store
        foreach (var group in LegionCommanderRegistrySchema.RetiredGroups)
        {
            try
            {
                if (Hexor.Compatibility.LegionSharedSettings.SyncGroup(Hexor.Compatibility.LegionSharedSettings.ResolveSharedDirectory(), group, registry.RegistryRoot)) Diagnostics.Log("registry-synchronization", "retired-shared-group-rebuilt; group=" + group);
                else Diagnostics.Log("registry-synchronization", "retired-shared-group-rebuild-failed; group=" + group, DiagnosticSeverity.Warning);
            }
            catch (Exception e) { Diagnostics.Log("registry-synchronization", "retired-shared-group-rebuild-failure; group=" + group + "; type=" + e.GetType().Name, DiagnosticSeverity.Warning); }
        }
    }

    public RegistrySynchronizationResult Ensure()
    {
        var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        Diagnostics.Log("registry-synchronization", $"started; application-version={appVersion}; schema-version={LegionCommanderRegistrySchema.Version}");
        var issues = new List<string>(); var created = 0; var preserved = 0; var invalid = 0; var errors = 0; var previousVersion = 0; var restored = 0; var retiredRemoved = 0;
        using var mutex = new Mutex(false, MutexName); var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { issues.Add("Synchronization lock timed out; no registry values were changed."); errors++; return Result(); }
            previousVersion = registry.ReadAppliedSchemaVersion();
            foreach (var item in RegistryDefaults.Template)
            {
                var label = item.Section + "." + item.Name;
                try
                {
                    var entry = registry.Inspect(item.Section, item.Name);
                    if (entry.Exists)
                    {
                        if (item.ResetOnStartup && (!entry.Supported || entry.Value != item.InitializationDefault))
                        {
                            registry.RestoreStartupDefault(item.Section, item.Name);
                            var verifiedDefault = registry.Inspect(item.Section, item.Name);
                            if (!verifiedDefault.Exists || !verifiedDefault.Supported || verifiedDefault.Value != item.InitializationDefault)
                                throw new IOException("Startup default read-back verification failed.");
                            restored++;
                            Diagnostics.Log("registry-synchronization", "startup-default-restored; setting=" + label);
                            continue;
                        }
                        preserved++;
                        if (!entry.Supported || entry.Value == null || !item.IsValid(entry.Value.Value))
                        { invalid++; issues.Add(label + ": invalid type or value preserved for customer review."); Diagnostics.Log("registry-synchronization", "invalid-value-preserved; setting=" + label, DiagnosticSeverity.Warning); }
                        continue;
                    }
                    if (registry.CreateMissingDefault(item.Section, item.Name, item.InitializationDefault))
                    { created++; Diagnostics.Log("registry-synchronization", "missing-value-created; setting=" + label + "; default=" + item.InitializationDefault); }
                    var verified = registry.Inspect(item.Section, item.Name);
                    if (!verified.Exists || !verified.Supported || verified.Value != item.InitializationDefault) throw new IOException("Registry read-back verification failed.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
                { errors++; issues.Add(label + ": synchronization failed (" + e.GetType().Name + ")."); Diagnostics.Log("registry-synchronization", "failure; setting=" + label + "; type=" + e.GetType().Name, DiagnosticSeverity.Error); }
            }
            foreach (var retired in LegionCommanderRegistrySchema.RetiredValues)
            {
                var label = retired.Section + "." + retired.Name + " (retired)";
                try
                {
                    if (!registry.DeleteRetired(retired.Section, retired.Name)) continue; // value already absent; removal is complete
                    Diagnostics.Log("registry-synchronization", "retired-value-removed; setting=" + label);
                    retiredRemoved++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or NotSupportedException)
                { errors++; issues.Add(label + ": removal failed (" + e.GetType().Name + ")."); Diagnostics.Log("registry-synchronization", "retired-removal-failure; setting=" + label + "; type=" + e.GetType().Name, DiagnosticSeverity.Error); }
            }
            RebuildRetiredSharedGroups(registry);
            if (errors == 0)
            {
                try { registry.WriteAppliedSchemaVersion(LegionCommanderRegistrySchema.Version); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
                { errors++; issues.Add("Schema version could not be recorded (" + e.GetType().Name + ")."); }
            }
            return Result();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
            Diagnostics.Log("registry-synchronization", $"completed; checked={RegistryDefaults.Template.Count}; created={created}; restored={restored}; preserved={preserved}; invalid={invalid}; errors={errors}; migration={previousVersion}->{LegionCommanderRegistrySchema.Version}", errors > 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Information);
        }
        RegistrySynchronizationResult Result() => new(RegistryDefaults.Template.Count, created, preserved, invalid, errors, previousVersion, LegionCommanderRegistrySchema.Version, issues) { StartupDefaultsRestored = restored, RetiredValuesRemoved = retiredRemoved };
    }
}
