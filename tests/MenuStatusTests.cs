using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using Hexor;

internal static class MenuStatusTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var fixture = Path.Combine(root, "menu-status"); Directory.CreateDirectory(fixture);
        var first = CreateInstallation(fixture, "one", withMenu: false);
        var manifest = Path.Combine(fixture, VendorOverlayCleanupService.ManifestName);
        var service = new MenuStatusService(new VendorOverlayCleanupService(_ => false), () => false);

        assert(service.Check([], manifest).State == MenuStatusState.NotDetected, "Menu status reports Not detected when no verified installation exists");
        assert(service.Check([first], manifest).State == MenuStatusState.NotDetected, "Menu status reports Not detected when the verified installation has no dxgi.dll");

        File.WriteAllText(first.OverlayPath, "verified-menu");
        // Ownership evidence no longer gates the status: file presence decides it.
        assert(service.Check([first], manifest).State == MenuStatusState.MenuDetected, "Verified dxgi.dll presence reports Menu detected without claiming it is loaded");
        var running = new MenuStatusService(new VendorOverlayCleanupService(_ => false), () => true);
        assert(running.Check([first], manifest).State == MenuStatusState.GameRunning, "Detected menu reports Game running while AION2 is active");

        var second = CreateInstallation(fixture, "two", withMenu: true);
        assert(service.Check([first, second], manifest).State == MenuStatusState.MultipleInstallations, "Multiple verified installations require customer selection");

        // Unload is temporary: the module is renamed beside the game and Restore moves it back.
        var detected = service.CheckAsync([first], manifest).GetAwaiter().GetResult();
        assert(detected.State == MenuStatusState.MenuDetected, "Asynchronous menu refresh reaches the verified detected state");
        var backupPath = first.OverlayPath + VendorOverlayCleanupService.UnloadedSuffix;
        var result = service.UnloadOrRestore(detected);
        assert(result.State == VendorOverlayCleanupState.Removed && !File.Exists(first.OverlayPath) && File.Exists(backupPath) && File.ReadAllText(backupPath) == "verified-menu", "Unload renames the verified dxgi.dll beside the game without deleting it");
        assert(service.Check([first], manifest).State == MenuStatusState.MenuUnloaded, "Menu status refreshes to Menu unloaded after a temporary unload");
        var restored = service.UnloadOrRestore(service.Check([first], manifest));
        assert(restored.State == VendorOverlayCleanupState.Restored && File.Exists(first.OverlayPath) && File.ReadAllText(first.OverlayPath) == "verified-menu" && !File.Exists(backupPath), "Restore moves the unloaded module back unchanged");

        var unrelated = Path.Combine(first.InstallationDirectory, "unrelated.dll"); File.WriteAllText(unrelated, "preserve");
        var repeat = service.UnloadOrRestore(service.Check([first, second], manifest));
        assert(repeat.State != VendorOverlayCleanupState.Removed && File.ReadAllText(unrelated) == "preserve", "Unload cannot touch unrelated DLLs without a single verified installation");
        var blocked = running.UnloadOrRestore(running.Check([first], manifest));
        assert(blocked.State == VendorOverlayCleanupState.BlockedByRunningProcess && File.Exists(first.OverlayPath), "Unload requires AION2 to close and never force-closes the game");
    }

    private static AionInstallation CreateInstallation(string root, string name, bool withMenu)
    {
        var installation = Path.Combine(root, name); var win64 = Path.Combine(installation, "Aion2", "Binaries", "Win64"); Directory.CreateDirectory(win64);
        var executable = Path.Combine(win64, "Aion2.exe"); File.WriteAllText(executable, "game"); if (withMenu) File.WriteAllText(Path.Combine(win64, "dxgi.dll"), "menu");
        return new(installation, executable, AionInstallationClient.UserSelected, "test");
    }

    private static void WriteManifest(string path, AionInstallation installation, string updater)
    {
        var manifest = new VendorOverlayOwnershipManifest(Path.GetDirectoryName(installation.OverlayPath)!, Hash(installation.OverlayPath), null, null, updater, Hash(updater));
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
