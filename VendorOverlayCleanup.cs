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
            return new(VendorOverlayCleanupState.CouldNotVerify, target, reason);
        return new(VendorOverlayCleanupState.Skipped, target, manifest!.OriginalBackupPath == null ? "Verified vendor dxgi.dll can be removed." : "Verified vendor dxgi.dll can be replaced with its verified original backup.");
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
