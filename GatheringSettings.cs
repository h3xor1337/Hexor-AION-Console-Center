using System.Linq;
using Microsoft.Win32;

namespace Hexor;

public sealed record RegistryStringEntry(string? Value, bool Exists, bool Supported);

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

    // Built-in English fallback for the vendor's resource identifiers: the saved GatherTargetNames
    // lookup is written by the vendor console and can miss entries (observed: 隱藏背包 missing while
    // the game's saved targets still reference it), which left customers looking at raw CJK names.
    // English never falls back to the raw CJK identifier.
    public static readonly IReadOnlyDictionary<string, string> EnglishNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["隱藏背包"] = "Hidden Cube",
        ["主神痕跡"] = "Empyrean Trace",
        ["奧德"] = "Odyle",
        ["紅寶石"] = "Ruby",
        ["藍寶石"] = "Sapphire",
        ["鑽石"] = "Diamond",
        ["奧里哈康"] = "Orichalcum",
        ["雪莉"] = "Inina",
        ["梅拉"] = "Mela",
        ["雲芝"] = "Coriolus",
        ["龍蒿"] = "Targena",
        ["當歸"] = "Aria",
        ["齊爾利"] = "Cypri",
        ["庫庫羅"] = "Kukuru",
        ["天涯草"] = "Azpha",
        ["菩提"] = "Asvata",
        ["麻痺草"] = "Paralyzing Herb",
        ["金盞花"] = "Calendula",
        ["魔爾菇"] = "Morfa",
        ["萊森樹莓"] = "Raydam",
        ["珂尼玳"] = "Conide",
        ["秋葵"] = "Ohkra",
        ["世界樹"] = "Yggdrasil",
        ["波齊鯉"] = "Pujery",
    };

    // Vendor REG_SZ format: "1\t<identifier>" per line.
    public static string TargetsText() => string.Join("\n", Entries.Select(entry => (entry.Enabled ? "1" : "0") + "\t" + entry.Identifier));
}

public sealed partial class RegistrySettingsService
{
    private static RegistryStringEntry InspectString(RegistryKey? key, string name)
    {
        if (key == null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(null, false, true);
        return key.GetValueKind(name) != RegistryValueKind.String ? new(null, true, false)
            : new(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, true, true);
    }
}
