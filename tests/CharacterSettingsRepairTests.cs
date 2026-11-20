using System.IO;
using System.Text.Json.Nodes;

internal static class CharacterSettingsRepairTests
{
    // The in-game Target Filter reads <config root>\<Character>_Setting.json (GatherConfig
    // targetNames). Saved files with an empty list must be repaired from the fullest saved
    // list on the same installation, or from the vendor default selection, without touching
    // files that already hold a list.
    public static void Run(string root, Action<bool, string> check)
    {
        var configRoot = Path.Combine(root, "20ccce88-da0d-4324-adf5-260460cf23da");
        Directory.CreateDirectory(configRoot);

        // A character file saved with an empty gather list (the customer failure mode), next to
        // a healthy file with a full list: the empty one is repaired from the healthy donor,
        // checked flags included, and the healthy one is left byte-for-byte untouched.
        var donorPath = Path.Combine(configRoot, "Healthy_Setting.json");
        var donorDoc = JsonNode.Parse("""
        {
          "AuctionConfig": {"enableBagTrickPickup": false},
          "AutoCombatConfig": {"pullDistance": 2500},
          "FreeFashionConfig": {"enabled": false},
          "GatherConfig": {
            "counterAttackPickup": true,
            "targetNames": [
              {"checked": true, "name": "奧德", "tids": [1000011]},
              {"checked": true, "name": "紅寶石", "tids": [1000012]},
              {"checked": false, "name": "世界樹", "tids": [1000013]},
              {"checked": false, "name": "波齊鯉", "tids": [1000014]},
              {"checked": false, "name": "隱藏背包", "tids": [1000015]},
              {"checked": false, "name": "主神痕跡", "tids": [1000016]},
              {"checked": false, "name": "藍寶石", "tids": [1000017]},
              {"checked": false, "name": "鑽石", "tids": [1000018]},
              {"checked": false, "name": "奧里哈康", "tids": [1000019]},
              {"checked": false, "name": "雪莉", "tids": [1000020]},
              {"checked": false, "name": "梅拉", "tids": [1000021]},
              {"checked": false, "name": "雲芝", "tids": [1000022]}
            ]
          }
        }
        """)!.AsObject();
        File.WriteAllText(donorPath, donorDoc.ToJsonString());

        var emptyPath = Path.Combine(configRoot, "Empty_Setting.json");
        var emptyDoc = JsonNode.Parse("""
        {
          "AuctionConfig": {"enableBagTrickPickup": false},
          "AutoCombatConfig": {"pullDistance": 2500},
          "FreeFashionConfig": {"enabled": false},
          "GatherConfig": {
            "autoCounterAttack": true,
            "ignoreAether": true,
            "qualityFilter": 1,
            "targetNames": []
          }
        }
        """)!.AsObject();
        File.WriteAllText(emptyPath, emptyDoc.ToJsonString());

        var (repaired, seeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(configRoot);
        check(repaired == 1 && seeded == 1, "Sweep repairs one empty saved list and seeds saved defaults");

        var repairedDoc = JsonNode.Parse(File.ReadAllText(emptyPath))!.AsObject();
        var names = repairedDoc["GatherConfig"]!["targetNames"]!.AsArray();
        check(names.Count == 12 && names[0]!["name"]!.GetValue<string>() == "奧德" && names[0]!["checked"]!.GetValue<bool>()
            && repairedDoc["GatherConfig"]!["ignoreAether"]!.GetValue<bool>(),
            "Repaired list follows the donor order and checked flags while keeping the file's other gather options");

        var untouched = File.ReadAllText(donorPath);
        check(JsonNode.Parse(untouched)!["GatherConfig"]!["targetNames"]!.AsArray().Count == 12,
            "Files that already hold a gather list are not rewritten");

        check(File.Exists(Path.Combine(configRoot, "DefaultAppSetting.json")), "The saved defaults file is rebuilt in the config root");
        var defaults = JsonNode.Parse(File.ReadAllText(Path.Combine(configRoot, "DefaultAppSetting.json")))!.AsObject();
        check(Hexor.Runtime.CharacterSettingsRepairService.RequiredDefaultNodes.All(node => defaults[node] is System.Text.Json.Nodes.JsonObject)
            && (defaults["GatherConfig"]!["targetNames"]!.AsArray()).Count == 24,
            "Rebuilt defaults carry every required config node and the module's own gather-default shape");
        check(defaults["AutoCombatConfig"]!["pullDistance"]!.GetValue<int>() == 2500
            && defaults["GatherConfig"]!["targetNames"]![0]!["tids"] == null
            && defaults["GatherConfig"]!["qualityFilter"]!.GetValue<int>() == 2,
            "Rebuilt defaults preserve the reference's non-gather config while the gather entries use the module's minimal shape");

        // No donor anywhere: the empty list is repaired from the vendor default selection.
        var lonelyRoot = Path.Combine(root, "lonely");
        Directory.CreateDirectory(lonelyRoot);
        var lonelyPath = Path.Combine(lonelyRoot, "Solo_Setting.json");
        File.WriteAllText(lonelyPath, """{"GatherConfig":{"qualityFilter":3,"targetNames":[]}}""");
        var (lonelyRepaired, lonelySeeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(lonelyRoot);
        check(lonelyRepaired == 1 && lonelySeeded == 0, "An installation with no saved list falls back to the vendor default without writing an incomplete defaults file");
        check(!File.Exists(Path.Combine(lonelyRoot, "DefaultAppSetting.json")), "No defaults file is written when no healthy reference exists");
        var lonelyNames = JsonNode.Parse(File.ReadAllText(lonelyPath))!["GatherConfig"]!["targetNames"]!.AsArray();
        check(lonelyNames.Count == 24 && lonelyNames[0]!["name"]!.GetValue<string>() == "隱藏背包" && lonelyNames[0]!["checked"]!.GetValue<bool>() == false
            && lonelyNames[2]!["checked"]!.GetValue<bool>() == true,
            "Vendor default fallback keeps the original order and checked flags");

        // Nothing is fabricated in folders without saved character settings, and a file without
        // a GatherConfig section is not treated as a failure mode.
        var otherRoot = Path.Combine(root, "other");
        Directory.CreateDirectory(otherRoot);
        var (noneRepaired, noneSeeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(otherRoot);
        check(noneRepaired == 0 && noneSeeded == 0, "Nothing is created in a folder without saved character settings");
        var noGather = Path.Combine(configRoot, "NoGather_Setting.json");
        File.WriteAllText(noGather, """{"AuctionConfig":{"enableBagTrickPickup":false}}""");
        var (noGatherRepaired, noGatherSeeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(configRoot);
        check(noGatherRepaired == 0, "Files without a gather section are left alone");

        // Hexor's previous gather-only defaults seed is invalid for the module; with no healthy
        // reference anywhere it is removed so the module falls back to its built-in default.
        var seedRoot = Path.Combine(root, "gather-only-seed");
        Directory.CreateDirectory(seedRoot);
        var seedPath = Path.Combine(seedRoot, "Solo_Setting.json");
        File.WriteAllText(seedPath, """{"GatherConfig":{"qualityFilter":3,"targetNames":[]}}""");
        File.WriteAllText(Path.Combine(seedRoot, "DefaultAppSetting.json"), """{"GatherConfig":{"counterAttackPickup":true,"targetNames":[]}}""");
        var (seedRepaired, seedSeeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(seedRoot);
        check(seedRepaired == 1 && seedSeeded == 1 && !File.Exists(Path.Combine(seedRoot, "DefaultAppSetting.json")),
            "A gather-only defaults seed is removed when no healthy reference can rebuild it");

        // A valid defaults file is left byte-for-byte untouched.
        var validRoot = Path.Combine(root, "valid-default");
        Directory.CreateDirectory(validRoot);
        var validDefault = Path.Combine(validRoot, "DefaultAppSetting.json");
        File.WriteAllText(validDefault, """{"AuctionConfig":{"enableBagTrickPickup":false},"AutoCombatConfig":{"pullDistance":1},"FreeFashionConfig":{"enabled":false},"GatherConfig":{"targetNames":[]}}""");
        var (validRepaired, validSeeded) = Hexor.Runtime.CharacterSettingsRepairService.Sweep(validRoot);
        check(validSeeded == 0 && File.ReadAllText(validDefault) == """{"AuctionConfig":{"enableBagTrickPickup":false},"AutoCombatConfig":{"pullDistance":1},"FreeFashionConfig":{"enabled":false},"GatherConfig":{"targetNames":[]}}""",
            "A valid saved defaults file is not rewritten");
    }
}
