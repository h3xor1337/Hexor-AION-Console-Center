using System.Diagnostics;
using System.IO;

namespace Hexor;

public enum MenuStatusState { Checking, MenuDetected, MenuUnloaded, NotDetected, GameRunning, MultipleInstallations, VerificationRequired, Error }
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
            if (candidates.Length > 1) return new(MenuStatusState.MultipleInstallations, Translations.Localize("Multiple installations — select the correct AION2 installation."));
            if (candidates.Length == 0) return new(MenuStatusState.NotDetected, Translations.Localize("Not detected"));
            var installation = candidates[0];
            var backupPath = installation.OverlayPath + VendorOverlayCleanupService.UnloadedSuffix;
            // File presence decides the status; the ownership manifest only ever gated the old
            // updater-chain evidence and must not hide an installed module from the customer.
            var dllPresent = File.Exists(installation.OverlayPath);
            var backupPresent = File.Exists(backupPath);
            if (dllPresent && gameRunning()) return new(MenuStatusState.GameRunning, Translations.Localize("Game running — close AION2 before unloading the menu."), installation.ExecutablePath, installation.OverlayPath);
            if (dllPresent) return new(MenuStatusState.MenuDetected, Translations.Localize("Menu detected"), installation.ExecutablePath, installation.OverlayPath);
            if (backupPresent) return new(MenuStatusState.MenuUnloaded, Translations.Localize("Menu unloaded — click Restore to bring it back."), installation.ExecutablePath, backupPath);
            return new(MenuStatusState.NotDetected, Translations.Localize("Not detected"), installation.ExecutablePath, installation.OverlayPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("menu-status", "status-check-failed; type=" + e.GetType().Name);
            return new(MenuStatusState.Error, Translations.Localize("Error — menu status could not be determined."));
        }
    }

    // Unload is a temporary, reversible removal: the module file is renamed beside the game so
    // AION2 can no longer load it, and Restore moves it back. No ownership manifest is required.
    public VendorOverlayCleanupResult UnloadOrRestore(MenuStatusResult status)
    {
        if (status.State == MenuStatusState.GameRunning)
            return new(VendorOverlayCleanupState.BlockedByRunningProcess, status.TargetPath ?? "", "Close AION2 before unloading the menu.");
        if (status.State == MenuStatusState.MenuDetected && !string.IsNullOrWhiteSpace(status.GameExecutablePath))
            return cleanup.UnloadTemporary(status.GameExecutablePath);
        if (status.State == MenuStatusState.MenuUnloaded && !string.IsNullOrWhiteSpace(status.GameExecutablePath))
            return cleanup.RestoreTemporary(status.GameExecutablePath);
        return new(VendorOverlayCleanupState.CouldNotVerify, status.TargetPath ?? "", "A single verified installed menu is required before unloading.");
    }

    private static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("Aion2");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
