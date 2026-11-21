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

        File.WriteAllText(first.OverlayPath, "verified-menu"); var updater = Path.Combine(fixture, VendorUpdaterManager.ExecutableName); File.WriteAllText(updater, "reviewed-updater");
        WriteManifest(manifest, first, updater);
        assert(service.Check([first], manifest).State == MenuStatusState.MenuDetected, "Verified dxgi.dll presence reports Menu detected without claiming it is loaded");
        var running = new MenuStatusService(new VendorOverlayCleanupService(_ => false), () => true);
        assert(running.Check([first], manifest).State == MenuStatusState.GameRunning, "Detected menu reports Game running while AION2 is active");

        // Uncertain updater evidence (changed updater on disk) keeps a drifted module unverified;
        // with verified updater evidence the drift is a routine vendor module update and heals.
        File.WriteAllText(first.OverlayPath, "changed-unrelated-dll");
        File.WriteAllText(updater, "changed-updater-fixture");
        assert(service.Check([first], manifest).State == MenuStatusState.VerificationRequired, "Changed or uncertain DLL ownership reports Verification required");
        File.WriteAllText(updater, "reviewed-updater");
        assert(service.Check([first], manifest).State == MenuStatusState.MenuDetected, "A drifted module beside verified updater evidence refreshes ownership and reports Menu detected");
        WriteManifest(manifest, first, updater);
        var second = CreateInstallation(fixture, "two", withMenu: true);
        assert(service.Check([first, second], manifest).State == MenuStatusState.MultipleInstallations, "Multiple verified installations require customer selection");

        var detected = service.CheckAsync([first], manifest).GetAwaiter().GetResult();
        assert(detected.State == MenuStatusState.MenuDetected, "Asynchronous menu refresh reaches the verified detected state");
        var result = service.Unload(detected, manifest);
        assert(result.State == VendorOverlayCleanupState.Removed && !File.Exists(first.OverlayPath), "Unload Menu removes only the verified installation-scoped dxgi.dll");
        assert(service.Check([first], manifest).State == MenuStatusState.NotDetected, "Menu status refreshes to Not detected after successful deletion");

        var unrelated = Path.Combine(first.InstallationDirectory, "unrelated.dll"); File.WriteAllText(unrelated, "preserve");
        var repeat = service.Unload(detected, manifest);
        assert(repeat.State != VendorOverlayCleanupState.Removed && File.ReadAllText(unrelated) == "preserve", "Repeated unload cannot delete unrelated DLLs or an unverified target");
        File.WriteAllText(first.OverlayPath, "verified-menu"); WriteManifest(manifest, first, updater);
        var blocked = running.Unload(running.Check([first], manifest), manifest);
        assert(blocked.State == VendorOverlayCleanupState.BlockedByRunningProcess && File.Exists(first.OverlayPath), "Unload Menu requires AION2 to close and never force-closes the game");
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
