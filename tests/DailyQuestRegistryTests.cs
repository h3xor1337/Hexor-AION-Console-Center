using System.IO;
using Hexor;
using Microsoft.Win32;

internal static class DailyQuestRegistryTests
{
    public static void Run(string temporaryRoot, Action<bool, string> check)
    {
        var branch = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N");
        try
        {
            var registry = new RegistrySettingsService(branch);
            using var model = new MainViewModel(new JsonConfigurationService(Path.Combine(temporaryRoot, "daily.json"), new SettingsBackupService()), registry);
            var daily = model.Toggles.Single(t => t.Section == "AutoTask" && t.Name == "AutoDailyQuests");
            check(daily.IsEditable && daily.Value == false && !registry.Inspect("AutoTask", "AutoDailyQuests").Exists,
                "Missing daily quest setting displays OFF without creating a sample default");
            using (var key = Registry.CurrentUser.CreateSubKey(branch + "\\AutoTask"))
            {
                key.SetValue("CharacterRace", unchecked((int)0xffffffff), RegistryValueKind.DWord);
                key.SetValue("CharacterJob", 777, RegistryValueKind.DWord);
                key.SetValue("Unknown", "保留\r\nvalue", RegistryValueKind.String);
            }
            daily.Value = true;
            check(registry.Read("AutoTask", "AutoDailyQuests") == 1, "Daily quests ON writes exact DWORD 1");
            daily.Value = false;
            check(registry.Read("AutoTask", "AutoDailyQuests") == 0, "Daily quests OFF writes exact DWORD 0");
            model.ReloadRegistry();
            check(model.RegistryCharacterRace == "4294967295" && model.RegistryCharacterJob == "777", "Unknown identity DWORDs display unsigned raw values without invented enums");
            using (var key = Registry.CurrentUser.OpenSubKey(branch + "\\AutoTask", true))
            {
                check((string?)key!.GetValue("Unknown") == "保留\r\nvalue" && (int?)key.GetValue("CharacterJob") == 777 && (int?)key.GetValue("CharacterRace") == -1,
                    "Daily edits preserve unknown Unicode multiline data and identity DWORDs");
                key.SetValue("AutoDailyQuests", 99, RegistryValueKind.DWord);
                daily.Reload();
                check(daily.Value == null && registry.Read("AutoTask", "AutoDailyQuests") == 99, "Unknown daily DWORD is preserved on reload");
                key.SetValue("AutoDailyQuests", "retain", RegistryValueKind.String);
                daily.Reload(); daily.Value = true;
                check((string?)key.GetValue("AutoDailyQuests") == "retain" && daily.Value == null, "Unsupported daily registry type cannot be overwritten by a toggle");
            }
            try { registry.WriteVerified("AutoTask", "AutoDailyQuests", 2); throw new Exception("Invalid Boolean accepted"); }
            catch (InvalidDataException) { check(true, "Daily quests rejects non-Boolean writes"); }
            foreach (var name in new[] { "CharacterRace", "CharacterJob" })
            {
                try { registry.WriteVerified("AutoTask", name, 1); throw new Exception("Unverified identity write accepted"); }
                catch (InvalidOperationException) { check(true, "Unverified identity write blocked: " + name); }
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(branch, false); }
    }
}
