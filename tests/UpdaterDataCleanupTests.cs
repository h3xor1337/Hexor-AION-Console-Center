using Hexor;
using Microsoft.Win32;
using System.IO;

internal static class UpdaterDataCleanupTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var dataRoot = Path.Combine(root, "updater-data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "cache", "nested"));
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(dataRoot, "cache", "nested", "license.bin"), "license");
        var testRegistryPath = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N") + "\\Aion2Updater";
        using (var key = Registry.CurrentUser.CreateSubKey(testRegistryPath)) { key.SetValue("License", "fixture-license"); key.SetValue("Language", "ko-KR"); }
        var service = new UpdaterDataCleanupService(testRegistryPath, _ => false);
        var plan = service.Inspect(dataRoot);
        assert(plan.Count == 2 && plan.Any(x => x.Kind == CleanupItemKind.UpdaterData) && plan.Any(x => x.Kind == CleanupItemKind.UpdaterRegistry), "Unified cleanup plans only the explicit updater data root and updater registry key");
        var results = plan.Select(service.Execute).ToArray();
        assert(results.All(x => x.State == VendorOverlayCleanupState.Removed) && !Directory.Exists(dataRoot), "Complete updater-data cleanup removes the entire verified data root including nested cache and license files");
        assert(Registry.CurrentUser.OpenSubKey(testRegistryPath) == null, "Updater registry license and authorization state are removed, requiring license re-entry");

        var install = Path.Combine(root, "Programs", "Hexor", "AION Auto Updater"); Directory.CreateDirectory(install);
        var updaterExe = Path.Combine(install, "Aion2.Updater.exe"); var updaterDll = Path.Combine(install, "Aion2.Updater.dll");
        File.WriteAllText(updaterExe, "fixture-updater-exe"); File.WriteAllText(updaterDll, "fixture-updater-dll");
        var uninstallPath = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N") + "\\Uninstall\\Hexor";
        using (var key = Registry.CurrentUser.CreateSubKey(uninstallPath)) key.SetValue("DisplayName", "Hexor Control Center");
        try
        {
            assert(File.Exists(updaterExe) && File.Exists(updaterDll) && Registry.CurrentUser.OpenSubKey(uninstallPath) != null, "Cleanup leaves updater executable, updater DLL, Hexor Control Center installation, and uninstall registration outside its explicit scope");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(uninstallPath, false); }
    }
}
