using System.Diagnostics;
using System.IO;

namespace Hexor;

public enum MenuStatusState { Checking, MenuDetected, NotDetected, GameRunning, MultipleInstallations, VerificationRequired, Error }
public sealed record MenuStatusResult(MenuStatusState State, string Text, string? GameExecutablePath = null, string? TargetPath = null);

public sealed class MenuStatusService
{
    private readonly VendorOverlayCleanupService cleanup;
    private readonly Func<bool> gameRunning;

    public MenuStatusService(VendorOverlayCleanupService? cleanup = null, Func<bool>? gameRunning = null)
    {
        this.cleanup = cleanup ?? new VendorOverlayCleanupService();
        this.gameRunning = gameRunning ?? IsGameRunning;
    }

    public Task<MenuStatusResult> CheckAsync(IEnumerable<AionInstallation> installations, string manifestPath, CancellationToken token = default) =>
        Task.Run(() => Check(installations, manifestPath, token), token);

    public MenuStatusResult Check(IEnumerable<AionInstallation> installations, string manifestPath, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var candidates = installations.DistinctBy(x => x.InstallationDirectory, StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length > 1) return new(MenuStatusState.MultipleInstallations, "Multiple installations — select the correct AION2 installation.");
            if (candidates.Length == 0) return new(MenuStatusState.NotDetected, "Not detected");
            var installation = candidates[0];
            if (!File.Exists(installation.OverlayPath)) return new(MenuStatusState.NotDetected, "Not detected", installation.ExecutablePath, installation.OverlayPath);
            var inspection = cleanup.Inspect(installation.ExecutablePath, manifestPath);
            if (inspection.State == VendorOverlayCleanupState.CouldNotVerify)
                return new(MenuStatusState.VerificationRequired, "Verification required — menu ownership or installation path is uncertain.", installation.ExecutablePath, installation.OverlayPath);
            if (inspection.State != VendorOverlayCleanupState.Skipped || !inspection.Message.StartsWith("Verified", StringComparison.Ordinal))
                return new(MenuStatusState.Error, "Error — menu status could not be determined.", installation.ExecutablePath, installation.OverlayPath);
            if (gameRunning()) return new(MenuStatusState.GameRunning, "Game running — close AION2 before unloading the menu.", installation.ExecutablePath, installation.OverlayPath);
            return new(MenuStatusState.MenuDetected, "Menu detected", installation.ExecutablePath, installation.OverlayPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("menu-status", "status-check-failed; type=" + e.GetType().Name);
            return new(MenuStatusState.Error, "Error — menu status could not be determined.");
        }
    }

    public VendorOverlayCleanupResult Unload(MenuStatusResult status, string manifestPath)
    {
        if (status.State == MenuStatusState.GameRunning)
            return new(VendorOverlayCleanupState.BlockedByRunningProcess, status.TargetPath ?? "", "Close AION2 before unloading the menu.");
        if (status.State != MenuStatusState.MenuDetected || string.IsNullOrWhiteSpace(status.GameExecutablePath))
            return new(VendorOverlayCleanupState.CouldNotVerify, status.TargetPath ?? "", "A single verified installed menu is required before unloading.");
        return cleanup.Execute(status.GameExecutablePath, manifestPath);
    }

    private static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("Aion2");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
