using System.IO;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;
using Microsoft.Win32;

internal static class SharedSettingsSyncTests
{
    // Validates the vendor shared-settings document sync: Hexor's verified registry writes also
    // update <machineId>\shared\<group>.json in the installed LegionCommander's exact format, so
    // the running game DLL sees coin-shop toggles without waiting for the vendor console to
    // rebuild the document.
    public static void Run(string temporaryRoot, RegistrySettingsService registry, Action<bool, string> check)
    {
        var shared = Path.Combine(temporaryRoot, "shared-test", "shared");
        LegionSharedSettings.SharedDirectoryOverride = shared;
        try
        {
            var branch = "Software\\HexorTests\\SharedSettingsSync-" + Guid.NewGuid().ToString("N");
            using (Registry.CurrentUser.CreateSubKey(branch)) { }
            var service = new RegistrySettingsService(branch);

            // Coin-shop failure mode: registry has the toggles ON, the shared shop.json is missing.
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\Enhance"))
            {
                key.SetValue("AutoBuyOdEnergy", 1, RegistryValueKind.DWord);
                key.SetValue("AutoBuyReviveStone", 0, RegistryValueKind.DWord);
                key.SetValue("CoinShopNightmareDaevanion", 1, RegistryValueKind.DWord);
                key.SetValue("CoinShopNightmareStigma", 1, RegistryValueKind.DWord);
                key.SetValue("CoinShopFestivalDaevanion", 0, RegistryValueKind.DWord);
                key.SetValue("CoinShopFestivalStigma", 1, RegistryValueKind.DWord);
                key.SetValue("CoinShopAbyssStigma", 0, RegistryValueKind.DWord);
            }
            var shopPath = Path.Combine(shared, "shop.json");
            service.WriteVerified("Enhance", "CoinShopFestivalDaevanion", 1);
            check(File.Exists(shopPath), "Verified registry write creates the missing shop.json shared document");
            var shop = JsonNode.Parse(File.ReadAllText(shopPath))!.AsObject();
            check(shop["schema"]!.GetValue<int>() == 1 && shop["updatedBy"]!.GetValue<string>() == "commander", "shop.json follows the vendor document envelope");
            var shopValues = shop["values"]!.AsObject();
            check(shopValues["coinShopFestivalDaevanion"]!.GetValue<bool>()
                && shopValues["coinShopNightmareDaevanion"]!.GetValue<bool>()
                && shopValues["coinShopNightmareStigma"]!.GetValue<bool>()
                && shopValues["coinShopFestivalStigma"]!.GetValue<bool>()
                && !shopValues["coinShopAbyssStigma"]!.GetValue<bool>()
                && shopValues["autoBuyOdEnergy"]!.GetValue<bool>()
                && !shopValues["autoBuyReviveStone"]!.GetValue<bool>(),
                "shop.json values mirror the registry bools in the vendor camelCase keys");
            var mirror = shop["registryMirror"]!.AsObject();
            check(mirror["coinShopFestivalDaevanion"]!.GetValue<bool>() == true, "registryMirror matches values after sync");
            check(shopValues.Count == 7 && mirror.Count == 7, "shop.json contains exactly the seven shop-group registry keys");

            // Drift watchdog: the game's menu rewrites a group document with stale values; the
            // reconcile pass re-asserts the registry truth without touching clean documents.
            var driftPath = Path.Combine(shared, "shop.json");
            var correctShop = File.ReadAllText(driftPath);
            File.WriteAllText(driftPath, "{\"schema\":1,\"updatedBy\":\"game\",\"values\":{\"coinShopAbyssStigma\":true},\"registryMirror\":{}}");
            LegionSharedSettings.ReconcileAllGroups(branch);
            var reconciled = JsonNode.Parse(File.ReadAllText(driftPath))!["values"]!.AsObject();
            check(reconciled["coinShopAbyssStigma"]!.GetValue<bool>() == false,
                "Reconcile corrects a shop document the game rewrote with stale values");
            check(reconciled["coinShopFestivalStigma"]!.GetValue<bool>() == true,
                "Reconcile keeps the toggles the customer left on");
            var reconciledText = File.ReadAllText(driftPath);
            LegionSharedSettings.ReconcileAllGroups(branch);
            check(File.ReadAllText(driftPath) == reconciledText,
                "Reconcile leaves a clean document untouched");

            // Retired-feature purge: a document that still carries a removed key loses it on rebuild.
            var abyssPath = Path.Combine(shared, "abyss.json");
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab"))
            {
                key.SetValue("AutoEnterAbyss", 0, RegistryValueKind.DWord);
                key.SetValue("AbyssLayer", 21, RegistryValueKind.DWord);
                key.SetValue("AbyssCounterAttack", 0, RegistryValueKind.DWord);
            }
            Directory.CreateDirectory(shared);
            File.WriteAllText(abyssPath, "{\"schema\":1,\"updatedBy\":\"commander\",\"values\":{\"autoEnter\":true,\"layer\":23,\"counterAttack\":false,\"guardianLogoutAvoidance\":true},\"registryMirror\":{\"guardianLogoutAvoidance\":true}}");
            check(LegionSharedSettings.SyncGroup(shared, "abyss", branch), "abyss group rebuild succeeds over a stale document");
            var abyss = JsonNode.Parse(File.ReadAllText(abyssPath))!.AsObject();
            var abyssValues = abyss["values"]!.AsObject();
            check(abyssValues["guardianLogoutAvoidance"] == null && abyss["registryMirror"]!.AsObject()["guardianLogoutAvoidance"] == null,
                "Rebuilding the abyss document deletes the retired guardian logout-avoidance key");
            check(abyssValues["layer"]!.GetValue<int>() == 21, "Retired-key purge preserves the live abyss values");

            // A second group sync: dungeon documents rebuild from the registry string + DWORDs.
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab"))
            {
                key.SetValue("AutoDungeonMode", 1, RegistryValueKind.DWord);
                key.SetValue("AutoDungeonUrugugu", 0, RegistryValueKind.DWord);
                key.SetValue("AutoDungeonPriority", "1\t600001\n0\t600012\n1\t600031\n1\t600032", RegistryValueKind.String);
            }
            service.WriteVerified("ResourceGrab", "AutoDungeonMode", 0);
            var dungeon = JsonNode.Parse(File.ReadAllText(Path.Combine(shared, "dungeon.json")))!.AsObject();
            var dungeonValues = dungeon["values"]!.AsObject();
            check(dungeonValues["mode"]!.GetValue<int>() == 0 && dungeonValues["legacyUrugugu"]!.GetValue<bool>() == false, "dungeon.json mirrors mode and legacyUrugugu DWORDs");
            var priority = dungeonValues["priority"]!.AsArray();
            check(priority.Count == 4 && priority[0]!["id"]!.GetValue<int>() == 600001 && priority[0]!["checked"]!.GetValue<bool>()
                && priority[1]!["id"]!.GetValue<int>() == 600012 && priority[1]!["checked"]!.GetValue<bool>() == false
                && priority[2]!["id"]!.GetValue<int>() == 600031 && priority[3]!["id"]!.GetValue<int>() == 600032,
                "dungeon.json priority follows the vendor flag\\tmapId string with id/checked objects");

            // Corruption recovery: a broken document is replaced wholesale like the vendor does.
            File.WriteAllText(shopPath, "{corrupt");
            service.WriteVerified("Enhance", "CoinShopAbyssStigma", 1);
            var repaired = JsonNode.Parse(File.ReadAllText(shopPath))!.AsObject();
            check(repaired["values"]!["coinShopAbyssStigma"]!.GetValue<bool>(), "Corrupt shop.json is rebuilt from the registry mirror");

            // Preserve foreign keys: a document with an unrelated key keeps it.
            var withExtra = JsonNode.Parse(File.ReadAllText(shopPath))!.AsObject();
            ((JsonObject)withExtra["values"]!)["futureKey"] = true;
            File.WriteAllText(shopPath, withExtra.ToJsonString());
            service.WriteVerified("Enhance", "CoinShopNightmareDaevanion", 0);
            var preserved = JsonNode.Parse(File.ReadAllText(shopPath))!.AsObject();
            check(preserved["values"]!["futureKey"]!.GetValue<bool>() && preserved["values"]!["coinShopNightmareDaevanion"]!.GetValue<bool>() == false,
                "Sync preserves document keys that are not registry-backed");

            check(LegionSharedSettings.SyncFromRegistry("Enhance", "CoinShopNightmareDaevanion"), "Explicit sync succeeds");

            // Gather targets: the vendor REG_SZ string syncs as values.targets lines plus the
            // CJK→display lookup as values.targetNames (verified vendor importer shape).
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\ResourceGrab"))
            {
                key.SetValue("GatherTargetNames", "\uFEFF奧德\tOdyle\n紅寶石\tRuby", RegistryValueKind.String);
                key.SetValue("GatherTargets", "1\t奧德\n0\t紅寶石", RegistryValueKind.String);
            }
            service.WriteVerified("ResourceGrab", "GatherQualityFilterLevel", 1);
            var gatherPath = Path.Combine(shared, "gather.json");
            check(File.Exists(gatherPath), "Verified gather write creates the gather.json shared document");
            var gather = JsonNode.Parse(File.ReadAllText(gatherPath))!.AsObject();
            var gatherValues = gather["values"]!.AsObject();
            var targets = gatherValues["targets"]!.AsArray();
            var names = gatherValues["targetNames"]!.AsObject();
            check(targets.Count == 2 && targets[0]!.GetValue<string>() == "1\t奧德" && targets[1]!.GetValue<string>() == "0\t紅寶石"
                && names["奧德"]!.GetValue<string>() == "Odyle" && names["紅寶石"]!.GetValue<string>() == "Ruby"
                && gatherValues["qualityFilterLevel"]!.GetValue<int>() == 1,
                "gather.json mirrors the vendor flag\tidentifier targets and the names lookup");

            Registry.CurrentUser.DeleteSubKeyTree(branch, false);
        }
        finally
        {
            LegionSharedSettings.SharedDirectoryOverride = null;
        }
    }
}
