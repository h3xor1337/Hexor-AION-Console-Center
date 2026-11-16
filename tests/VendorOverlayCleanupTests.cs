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

        File.WriteAllText(target, "unrelated replacement");
        result = service.Execute(executable, manifestPath);
        assert(result.State == VendorOverlayCleanupState.CouldNotVerify && File.ReadAllText(target) == "unrelated replacement", "Hash mismatch retains an unrelated or replaced dxgi.dll");

        File.WriteAllText(target, "vendor overlay three");
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
    }

    private static void WriteManifest(string path, string gameDirectory, string target, string updater, string? backup)
    {
        var manifest = new VendorOverlayOwnershipManifest(gameDirectory, Hash(target), backup, backup == null ? null : Hash(backup), updater, Hash(updater));
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
