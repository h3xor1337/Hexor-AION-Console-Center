using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hexor;

public enum VendorOverlayCleanupState { Removed, Restored, Skipped, CouldNotVerify, BlockedByRunningProcess, Failed }
public sealed record VendorOverlayCleanupResult(VendorOverlayCleanupState State, string TargetPath, string Message);
public sealed record VendorOverlayOwnershipManifest(string InstallationDirectory, string DxgiSha256, string? OriginalBackupPath, string? OriginalBackupSha256, string UpdaterExecutablePath, string UpdaterSha256);

// Explicitly user-initiated. This class never enumerates or deletes arbitrary game files.
public sealed class VendorOverlayCleanupService
{
    public const string ManifestName = "vendor-overlay-ownership.json";
    private readonly Func<string, bool> processRunning;
    public VendorOverlayCleanupService(Func<string, bool>? processRunning = null) => this.processRunning = processRunning ?? IsProcessRunning;

    public VendorOverlayCleanupResult Inspect(string gameExecutablePath, string manifestPath)
    {
        var target = ResolveTarget(gameExecutablePath);
        if (target == null) return new(VendorOverlayCleanupState.CouldNotVerify, "", "A verified AION2 Win64 installation path is required.");
        if (!File.Exists(target)) return new(VendorOverlayCleanupState.Skipped, target, "No dxgi.dll exists at the verified AION2 Win64 path.");
        if (!TryReadManifest(manifestPath, out var manifest, out var reason) || !Matches(target, manifest!, out reason))
        {
            if (!TryRefreshModuleEvidence(manifestPath, manifest!, target, reason))
                return new(VendorOverlayCleanupState.CouldNotVerify, target, reason);
        }
        return new(VendorOverlayCleanupState.Skipped, target, manifest!.OriginalBackupPath == null ? "Verified vendor dxgi.dll can be removed." : "Verified vendor dxgi.dll can be replaced with its verified original backup.");
    }

    // The reviewed companion updater (Aion2.Updater.exe) is the component that installs the menu
    // module into the game, and the vendor ships module updates regularly. When only the module
    // hash drifted while the recorded updater evidence still verifies, the change is a routine
    // vendor module update and the recorded hash is refreshed; without this every vendor update
    // would leave the menu status on Verification required until the evidence was rewritten by hand.
    // Every other uncertainty (missing or stale updater evidence, wrong installation, reparse point,
    // invalid manifest) still reports CouldNotVerify.
    private static bool TryRefreshModuleEvidence(string manifestPath, VendorOverlayOwnershipManifest manifest, string target, string reason)
    {
        if (reason != "dxgi.dll does not match the recorded vendor hash; it was retained.") return false;
        try
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.IsNullOrWhiteSpace(manifest.UpdaterExecutablePath) ||
                !Path.GetFileName(manifest.UpdaterExecutablePath).Equals(VendorUpdaterManager.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(manifest.UpdaterExecutablePath) ||
                string.IsNullOrWhiteSpace(manifest.UpdaterSha256) ||
                !Hash(manifest.UpdaterExecutablePath).Equals(manifest.UpdaterSha256, StringComparison.OrdinalIgnoreCase)) return false;
            var refreshed = manifest with { InstallationDirectory = Path.GetDirectoryName(target)!, DxgiSha256 = Hash(target) };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(refreshed, new JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Log("menu-status", "module-evidence-refresh; reason=vendor-module-update; dxgi=" + refreshed.DxgiSha256[..12].ToLowerInvariant());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("menu-status", "module-evidence-refresh-failed; type=" + e.GetType().Name);
            return false;
        }
    }

    // Called by the companion updater sync after it installed a verified release: the recorded
    // updater evidence is refreshed so ownership verification follows the signed OTA chain.
    public static void RefreshUpdaterEvidence(string manifestPath, string updaterExecutablePath)
    {
        try
        {
            if (!File.Exists(manifestPath) || !File.Exists(updaterExecutablePath)) return;
            var manifest = JsonSerializer.Deserialize<VendorOverlayOwnershipManifest>(File.ReadAllText(manifestPath));
            if (manifest == null) return;
            var refreshed = manifest with { UpdaterExecutablePath = Path.GetFullPath(updaterExecutablePath), UpdaterSha256 = Hash(updaterExecutablePath) };
            if (refreshed == manifest) return;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(refreshed, new JsonSerializerOptions { WriteIndented = true }));
            Diagnostics.Log("menu-status", "updater-evidence-refresh; updater=" + refreshed.UpdaterSha256[..12].ToLowerInvariant());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Log("menu-status", "updater-evidence-refresh-failed; type=" + e.GetType().Name);
        }
    }

    public VendorOverlayCleanupResult Execute(string gameExecutablePath, string manifestPath)
    {
        var inspection = Inspect(gameExecutablePath, manifestPath);
        if (inspection.State == VendorOverlayCleanupState.CouldNotVerify || inspection.State == VendorOverlayCleanupState.Skipped && !inspection.Message.StartsWith("Verified", StringComparison.Ordinal)) return inspection;
        if (new[] { "Aion2", "Aion2.Updater", "Aion2Updater.Launcher", "LegionCommander" }.Any(processRunning))
            return new(VendorOverlayCleanupState.BlockedByRunningProcess, inspection.TargetPath, "Close AION2, the original updater, its launcher, and Legion Commander before cleanup.");
        try
        {
            var manifest = JsonSerializer.Deserialize<VendorOverlayOwnershipManifest>(File.ReadAllText(manifestPath));
            if (manifest == null)
                return new(VendorOverlayCleanupState.CouldNotVerify, inspection.TargetPath, "Vendor ownership manifest is invalid; dxgi.dll was retained.");
            if (manifest.OriginalBackupPath is { Length: > 0 } backup)
            {
                if (!File.Exists(backup) || !Hash(backup).Equals(manifest.OriginalBackupSha256, StringComparison.OrdinalIgnoreCase))
                    return new(VendorOverlayCleanupState.CouldNotVerify, inspection.TargetPath, "The recorded original backup is unavailable or does not match its verified hash.");
                File.Copy(backup, inspection.TargetPath, true);
                return new(VendorOverlayCleanupState.Restored, inspection.TargetPath, "Verified original dxgi.dll was restored.");
            }
            File.Delete(inspection.TargetPath);
            return File.Exists(inspection.TargetPath)
                ? new(VendorOverlayCleanupState.Failed, inspection.TargetPath, "The verified vendor dxgi.dll could not be removed.")
                : new(VendorOverlayCleanupState.Removed, inspection.TargetPath, "Verified vendor dxgi.dll was removed.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(VendorOverlayCleanupState.Failed, inspection.TargetPath, "The verified vendor dxgi.dll could not be changed: " + e.GetType().Name + "."); }
    }

    public static string? ResolveTarget(string gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath) || !File.Exists(gameExecutablePath)) return null;
        var executable = Path.GetFullPath(gameExecutablePath);
        var directory = Path.GetDirectoryName(executable);
        var binariesDirectory = directory == null ? null : Path.GetDirectoryName(directory);
        if (directory == null || binariesDirectory == null || !Path.GetFileName(executable).Equals("Aion2.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(directory).Equals("Win64", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(binariesDirectory).Equals("Binaries", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.Combine(directory, "dxgi.dll");
    }

    private static bool TryReadManifest(string path, out VendorOverlayOwnershipManifest? manifest, out string reason)
    {
        manifest = null; reason = "No vendor ownership manifest was found; dxgi.dll was retained.";
        try { if (!File.Exists(path)) return false; manifest = JsonSerializer.Deserialize<VendorOverlayOwnershipManifest>(File.ReadAllText(path)); if (manifest == null) { reason = "Vendor ownership manifest is invalid; dxgi.dll was retained."; return false; } return true; }
        catch (JsonException) { reason = "Vendor ownership manifest is invalid; dxgi.dll was retained."; return false; }
        catch (IOException) { reason = "Vendor ownership manifest could not be read; dxgi.dll was retained."; return false; }
    }
    private static bool Matches(string target, VendorOverlayOwnershipManifest manifest, out string reason)
    {
        reason = "";
        var gameDir = Path.GetDirectoryName(target)!;
        if (!PathsEqual(gameDir, manifest.InstallationDirectory)) { reason = "Ownership manifest does not match this verified AION2 installation; dxgi.dll was retained."; return false; }
        if (string.IsNullOrWhiteSpace(manifest.DxgiSha256) || !Hash(target).Equals(manifest.DxgiSha256, StringComparison.OrdinalIgnoreCase)) { reason = "dxgi.dll does not match the recorded vendor hash; it was retained."; return false; }
        if (string.IsNullOrWhiteSpace(manifest.UpdaterExecutablePath) ||
            !Path.GetFileName(manifest.UpdaterExecutablePath).Equals(VendorUpdaterManager.ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(manifest.UpdaterExecutablePath) ||
            string.IsNullOrWhiteSpace(manifest.UpdaterSha256) ||
            !Hash(manifest.UpdaterExecutablePath).Equals(manifest.UpdaterSha256, StringComparison.OrdinalIgnoreCase))
        { reason = "Ownership manifest lacks matching reviewed updater evidence; dxgi.dll was retained."; return false; }
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
        { reason = "dxgi.dll is a reparse point and was retained."; return false; }
        return true;
    }
    private static bool PathsEqual(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool IsProcessRunning(string name) { var processes = Process.GetProcessesByName(name); try { return processes.Length != 0; } finally { foreach (var process in processes) process.Dispose(); } }
}
public static class AppLayout
{
    // AppContext.BaseDirectory ends with a separator; Directory.GetParent on that returns the
    // directory itself, which once made the console look for "AION Auto Updater" inside its own
    // install folder. Always trim before resolving the parent.
    public static string? InstallParent()
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return Directory.GetParent(baseDirectory)?.FullName;
    }
}
