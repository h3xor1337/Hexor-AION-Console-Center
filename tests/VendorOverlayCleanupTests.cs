using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using Hexor;

internal static class VendorOverlayCleanupTests
{
    public static void Run(string root, Action<bool, string> assert)
    {
        var fixture = Path.Combine(root, "overlay-cleanup");
        var game = Path.Combine(fixture, "AION2", "Aion2", "Binaries", "Win64");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "Aion2.exe"); File.WriteAllText(executable, "game");
        var target = Path.Combine(game, "dxgi.dll");
        var updater = Path.Combine(fixture, "AION Auto Updater", VendorUpdaterManager.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(updater)!); File.WriteAllText(updater, "reviewed updater fixture");
        var manifestPath = Path.Combine(fixture, VendorOverlayCleanupService.ManifestName);
        var service = new VendorOverlayCleanupService(_ => false);

        File.WriteAllText(target, "vendor overlay");
        WriteManifest(manifestPath, game, target, updater, null);
        var inspected = service.Inspect(executable, manifestPath);
        assert(inspected.State == VendorOverlayCleanupState.Skipped && inspected.Message.StartsWith("Verified"), "Hash and updater-evidence manifest verifies only the exact AION2 Win64 dxgi.dll");
        var result = service.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.Removed && !File.Exists(target), "Verified updater-owned dxgi.dll with no original backup is removed");

        File.WriteAllText(target, "vendor overlay two"); var backup = Path.Combine(fixture, "original-dxgi.dll"); File.WriteAllText(backup, "original game dxgi");
        WriteManifest(manifestPath, game, target, updater, backup);
        result = service.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.Restored && File.ReadAllText(target) == "original game dxgi", "Verified original dxgi.dll backup is restored instead of deleted");

        // A replaced module beside verified updater evidence is a routine vendor module update:
        // the reviewed updater is the component that installs the module, so the recorded hash
        // is refreshed instead of parking the menu status on Verification required.
        File.WriteAllText(target, "vendor module update");
        var healed = service.Inspect(executable, manifestPath);
        assert(healed.State == VendorOverlayCleanupState.Skipped && healed.Message.StartsWith("Verified"), "A replaced module beside verified updater evidence refreshes the recorded hash");
        assert(ReadManifest(manifestPath).DxgiSha256 == Hash(target), "Refreshed ownership evidence records the current module hash");

        File.WriteAllText(target, "unrelated replacement");
        File.WriteAllText(updater, "changed updater");
        result = service.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.ReadAllText(target) == "unrelated replacement", "Hash mismatch without verified updater evidence retains the replacement");

        File.WriteAllText(target, "vendor overlay three");
        File.WriteAllText(updater, "reviewed updater fixture");
        WriteManifest(manifestPath, game, target, updater, null);
        File.WriteAllText(updater, "changed updater");
        result = service.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.Exists(target), "Updater hash mismatch blocks cleanup");

        File.WriteAllText(updater, "reviewed updater fixture"); WriteManifest(manifestPath, game, target, updater, null);
        var running = new VendorOverlayCleanupService(name => name == "Aion2");
        result = running.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.BlockedByRunningProcess && File.Exists(target), "Running game or updater blocks every overlay change");

        result = service.Execute(Path.Combine(fixture, "other", "Aion2.exe"), manifestPath);
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.Exists(target), "Non-Win64 or unverified game paths cannot target arbitrary DLLs");
        File.WriteAllText(Path.Combine(game, "unrelated.dll"), "keep");
        result = service.Execute(executable, Path.Combine(fixture, "missing-manifest.json"));
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.Exists(Path.Combine(game, "unrelated.dll")), "Missing ownership evidence changes no unrelated game file");

        // Ownership evidence resolution follows the installed updater, not the running binary:
        // a debug build elsewhere must still find the installed manifest.
        var resolvedRoot = Path.Combine(root, "ownership-resolution");
        var resolvedControl = Path.Combine(resolvedRoot, VendorUpdaterInstallation.ControlCenterFolder); Directory.CreateDirectory(resolvedControl);
        var resolvedUpdater = Path.Combine(resolvedRoot, VendorUpdaterInstallation.UpdaterFolder); Directory.CreateDirectory(resolvedUpdater);
        File.WriteAllText(Path.Combine(resolvedUpdater, VendorUpdaterManager.ExecutableName), "updater");
        File.WriteAllText(Path.Combine(resolvedUpdater, VendorOverlayCleanupService.ManifestName), "{}");
        var resolved = VendorUpdaterInstallation.OwnershipManifestPath(resolvedControl, null);
        assert(resolved != null && Path.GetFullPath(resolved).Equals(Path.GetFullPath(Path.Combine(resolvedUpdater, VendorOverlayCleanupService.ManifestName)), StringComparison.OrdinalIgnoreCase), "Ownership evidence resolves beside the updater sibling of the console");
        var localRoot = Path.Combine(root, "ownership-local");
        var standardControl = Path.Combine(localRoot, "Programs", VendorUpdaterInstallation.ProductFolder, VendorUpdaterInstallation.ControlCenterFolder); Directory.CreateDirectory(standardControl);
        var standardUpdater = Path.Combine(localRoot, "Programs", VendorUpdaterInstallation.ProductFolder, VendorUpdaterInstallation.UpdaterFolder); Directory.CreateDirectory(standardUpdater);
        File.WriteAllText(Path.Combine(standardUpdater, VendorUpdaterManager.ExecutableName), "updater");
        File.WriteAllText(Path.Combine(standardUpdater, VendorOverlayCleanupService.ManifestName), "{}");
        var isolated = Directory.CreateDirectory(Path.Combine(root, "ownership-isolated", "console")).FullName;
        var resolvedStandard = VendorUpdaterInstallation.OwnershipManifestPath(isolated, localRoot);
        assert(resolvedStandard != null && Path.GetFullPath(resolvedStandard).Equals(Path.GetFullPath(Path.Combine(standardUpdater, VendorOverlayCleanupService.ManifestName)), StringComparison.OrdinalIgnoreCase), "Ownership evidence resolves from the standard per-user installation");

        // The companion-updater sync refreshes the recorded updater evidence after installing a
        // verified release, so ownership verification follows the signed OTA chain.
        var staleManifest = Path.Combine(resolvedUpdater, VendorOverlayCleanupService.ManifestName);
        File.WriteAllText(staleManifest, JsonSerializer.Serialize(new VendorOverlayOwnershipManifest(game, Hash(target), null, null, "missing-updater", "0".PadLeft(64, '0'))));
        VendorOverlayCleanupService.RefreshUpdaterEvidence(staleManifest, Path.Combine(resolvedUpdater, VendorUpdaterManager.ExecutableName));
        var refreshed = ReadManifest(staleManifest);
        assert(refreshed.UpdaterExecutablePath.EndsWith(VendorUpdaterManager.ExecutableName, StringComparison.OrdinalIgnoreCase) && refreshed.UpdaterSha256 == Hash(Path.Combine(resolvedUpdater, VendorUpdaterManager.ExecutableName)), "RefreshUpdaterEvidence re-records the installed updater hash and path");
    }

    private static VendorOverlayOwnershipManifest ReadManifest(string path) => JsonSerializer.Deserialize<VendorOverlayOwnershipManifest>(File.ReadAllText(path))!;
    private static void WriteManifest(string path, string gameDirectory, string target, string updater, string? backup)
    {
        var manifest = new VendorOverlayOwnershipManifest(gameDirectory, Hash(target), backup, backup == null ? null : Hash(backup), updater, Hash(updater));
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
