namespace Hexor;

// Explicit-edit settings: these never participate in startup initialization.
public static class LegionCommanderFeatureSettings
{
    public static IReadOnlyList<(string Section, string Name, string Label)> Booleans { get; } = Array.AsReadOnly(new[]
    {
        ("Enhance", "EnchantBoundKinahOnly", "Enchant using bound Kinah only"),
        ("Enhance", "CoinShopNightmareDaevanion", "Buy Daevanion items with Nightmare coins"),
        ("Enhance", "CoinShopNightmareStigma", "Buy Stigma items with Nightmare coins"),
        ("Enhance", "CoinShopFestivalDaevanion", "Buy Daevanion items with Festival coins"),
        ("Enhance", "CoinShopFestivalStigma", "Buy Stigma items with Festival coins"),
        ("Enhance", "CoinShopAbyssStigma", "Buy Stigma items with Abyss coins"),
        ("Enhance", "GenusInsightAutoAnalyze", "Automatically analyze Genus Insight"),
        ("ResourceGrab", "AutoDungeonUrugugu", "Uruguru Cave"),
        ("ResourceGrab", "GatherQualityFilterEnabled", "Filter gathering resources by quality"),
        ("ResourceGrab", "GatherIgnoreAether", "Ignore Aether while gathering")
    });

    public static IReadOnlyList<(int Value, string Label)> Races { get; } =
        [(1, "Elyos"), (2, "Asmodian")];

    public static IReadOnlyList<(int Value, string Label)> Jobs { get; } =
        [(0, "Random"), (1, "Gladiator"), (2, "Templar"), (3, "Assassin"), (4, "Cleric"),
         (5, "Ranger"), (6, "Spiritmaster"), (7, "Sorcerer"), (8, "Chanter")];

    public static IReadOnlyList<(string Value, string Label)> Qualities { get; } =
        [("1", "White"), ("2", "Green"), ("3", "Blue")];

    public static bool IsDWord(string section, string name) =>
        Booleans.Any(x => x.Section == section && x.Name == name) || IsQualityLevel(section, name);
    public static bool IsQualityLevel(string section, string name) => section == "ResourceGrab" && name == "GatherQualityFilterLevel";
    public static bool IsSetting(string key) => Booleans.Any(x => key == x.Section + "." + x.Name)
        || key is "ResourceGrab.GatherQualityFilterLevel" or "ResourceGrab.GatherTargetNames" or "ResourceGrab.GatherTargets";
}
