using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Hexor;

public enum VendorUpdaterState
{
    UpdaterStarting,
    WaitingForUpdater,
    UpdaterUnavailable,
    PermissionMismatch,
    CompletionClosed,
    CompletionLeftOpen,
    CompletionVerified,
    CheckUpdaterStatus
}

public sealed record VendorUpdaterResult(VendorUpdaterState State, string Message);

public interface IVendorUpdaterProcessAdapter
{
    VendorUpdaterProcess? Find(string expectedPath);
    VendorUpdaterProcess? Launch(string executablePath);
    // The Windows implementation looks up this PID directly, independently of other updater instances.
    VendorUpdaterProcess? FindOwned(string expectedPath, VendorUpdaterProcess identity)
    {
        var process = Find(expectedPath);
        return process != null && identity.SameInstance(process) ? process : null;
    }
    IReadOnlyList<VendorUpdaterHandoffCandidate> FindHandoffCandidates(string expectedPath, VendorUpdaterProcess predecessor) => [];
}

public sealed record VendorUpdaterProcess(int Id, IntPtr MainWindowHandle, string? ExecutablePath = null, DateTime? StartTimeUtc = null,
    int WindowOwnerProcessId = 0)
{
    public bool SameInstance(VendorUpdaterProcess other) => Id == other.Id && StartTimeUtc != null && StartTimeUtc == other.StartTimeUtc;
}

public sealed record VendorUpdaterHandoffCandidate(VendorUpdaterProcess Process,
    int WindowOwnerProcessId, string? RejectionReason = null);

public interface IVendorUpdaterAutomationAdapter
{
    VendorUpdaterUi Probe(VendorUpdaterProcess process);
    void CloseWindow(VendorUpdaterUi ui);
    void CloseWindow(VendorUpdaterUi ui, Func<bool> canClose)
    {
        if (!canClose()) throw new ElementNotAvailableException();
        CloseWindow(ui);
    }
}

// The elements are opaque so tests can supply harmless stand-ins and no vendor state is inspected.
public sealed record VendorUpdaterUi(bool WindowFound, bool PermissionDenied, bool CompletionReady = false, object? Window = null, string Diagnostic = "",
    int TargetProcessId = 0, IntPtr TargetWindowHandle = default, bool TargetWindowVerified = false,
    bool CompletionControlsVisible = false, DateTime? TargetProcessStartTimeUtc = null, string? TargetExecutablePath = null,
    bool UpdateInProgress = false, bool RequiresAttention = false);

public sealed record VendorUpdaterLocation(string? ExecutablePath, string Message);

public static class VendorUpdaterInstallation
{
    public const string ProductFolder = "Hexor";
    public const string ControlCenterFolder = "Hexor Control Center";
    public const string UpdaterFolder = "AION Auto Updater";

    public static VendorUpdaterLocation Resolve(string? applicationBaseDirectory = null, string? localApplicationData = null)
    {
        // AppContext.BaseDirectory is the executable's directory; deliberately never use Environment.CurrentDirectory.
        var applicationDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        var sibling = ResolveUnderRoot(Directory.GetParent(applicationDirectory)?.FullName, applicationDirectory);
        if (sibling != null) return new(sibling, "Original updater resolved beside Hexor.");

        var local = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var standardRoot = Path.GetFullPath(Path.Combine(local, "Programs", ProductFolder));
        var standard = ResolveUnderRoot(standardRoot, expectedControlCenterDirectory: null);
        if (standard != null) return new(standard, "Original updater resolved from the standard per-user installation.");

        return new(null, "Original updater is unavailable. Expected AION Auto Updater beside Hexor Control Center or in this user's LocalAppData Programs folder.");
    }

    // Resolves the ownership manifest beside the resolved updater instead of beside the running
    // console binary: a debug or portable build elsewhere on disk must still find the installed
    // evidence. Falls back to the legacy sibling-folder layout when the updater is unavailable.
    public static string? OwnershipManifestPath(string? applicationBaseDirectory = null, string? localApplicationData = null)
    {
        var updater = Resolve(applicationBaseDirectory, localApplicationData).ExecutablePath;
        if (updater != null) return Path.Combine(Path.GetDirectoryName(updater)!, VendorOverlayCleanupService.ManifestName);
        var parent = AppLayout.InstallParent();
        return parent == null ? null : Path.Combine(parent, UpdaterFolder, VendorOverlayCleanupService.ManifestName);
    }

    private static string? ResolveUnderRoot(string? root, string? expectedControlCenterDirectory)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var fullRoot = Path.GetFullPath(root);
        var controlCenter = Path.Combine(fullRoot, ControlCenterFolder);
        var updaterDirectory = Path.Combine(fullRoot, UpdaterFolder);
        if (expectedControlCenterDirectory != null && !PathsEqual(controlCenter, expectedControlCenterDirectory)) return null;
        if (!Directory.Exists(fullRoot) || !Directory.Exists(controlCenter) || !Directory.Exists(updaterDirectory)) return null;
        var executable = Path.Combine(updaterDirectory, VendorUpdaterManager.ExecutableName);
        return File.Exists(executable) ? Path.GetFullPath(executable) : null;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}

public sealed class VendorUpdaterManager
{
    public const string ExecutableName = "Aion2.Updater.exe";
    public static readonly TimeSpan CompletionPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan AutomationProbeTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ProcessHandoffTimeout = TimeSpan.FromSeconds(10);
    private readonly IVendorUpdaterProcessAdapter processes;
    private readonly IVendorUpdaterAutomationAdapter automation;
    private readonly string? executablePath;
    private readonly string resolutionMessage;
    private readonly TimeSpan completionPollInterval;
    private readonly TimeSpan automationProbeTimeout;
    private readonly TimeSpan retryInterval;
    private readonly TimeSpan handoffTimeout;
    private readonly int completionConfirmationSamples;
    private readonly int hexorProcessId;
    private readonly Func<IntPtr> hexorMainWindowHandle;
    private readonly object sessionLock = new();
    private VendorUpdaterProcess? launchedProcess;
    private VendorUpdaterProcess? sessionOrigin;
    private int observerActive;
    private int completionCloseAttempted;
    private VerifiedCompletion? verifiedCompletion;
    private sealed record VerifiedCompletion(VendorUpdaterProcess Identity, VendorUpdaterProcess Origin, VendorUpdaterProcess Process, VendorUpdaterUi Ui);
    // WaitAsync does not cancel a blocked COM call. Keep its worker across timeouts, cancellation,
    // and subsequent sessions; no replacement worker may start until this one actually finishes.
    private Task<VendorUpdaterUi>? automationWorker;
    private bool discardWorkerResult;
    private AutomationOperation? automationOperation;
    private long automationSequence;

    public VendorUpdaterManager(IVendorUpdaterProcessAdapter? processes = null, IVendorUpdaterAutomationAdapter? automation = null,
        string? executablePath = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null, int? hexorProcessId = null,
        Func<IntPtr>? hexorMainWindowHandle = null, TimeSpan? completionPollInterval = null, TimeSpan? handoffTimeout = null,
        int completionConfirmationSamples = 3)
    {
        this.processes = processes ?? new WindowsVendorUpdaterProcessAdapter();
        this.automation = automation ?? new WindowsVendorUpdaterAutomationAdapter();
        var location = executablePath == null ? VendorUpdaterInstallation.Resolve() : new VendorUpdaterLocation(Path.GetFullPath(executablePath), "Original updater resolved for test or explicit integration.");
        this.executablePath = location.ExecutablePath;
        resolutionMessage = location.Message;
        this.completionPollInterval = BoundDelay(completionPollInterval ?? CompletionPollInterval);
        retryInterval = BoundDelay(pollInterval ?? TimeSpan.FromMilliseconds(250));
        automationProbeTimeout = timeout ?? AutomationProbeTimeout;
        if (automationProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.handoffTimeout = handoffTimeout ?? ProcessHandoffTimeout;
        if (this.handoffTimeout <= TimeSpan.Zero || this.handoffTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(handoffTimeout));
        if (completionConfirmationSamples is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(completionConfirmationSamples));
        this.completionConfirmationSamples = completionConfirmationSamples;
        this.hexorProcessId = hexorProcessId ?? Environment.ProcessId;
        this.hexorMainWindowHandle = hexorMainWindowHandle ?? (() => Process.GetCurrentProcess().MainWindowHandle);
    }

    public static string? InstalledExecutablePath() => VendorUpdaterInstallation.Resolve().ExecutablePath;

    public VendorUpdaterResult DetectOrStart()
    {
        lock (sessionLock)
        {
            if (executablePath == null) return new(VendorUpdaterState.UpdaterUnavailable, resolutionMessage);
            try
            {
                if (launchedProcess is { } owned && processes.FindOwned(executablePath, owned) != null)
                    return new(VendorUpdaterState.WaitingForUpdater, "Waiting for original updater.");
                if (launchedProcess != null && Volatile.Read(ref observerActive) != 0)
                    return new(VendorUpdaterState.WaitingForUpdater, "Waiting for the original updater process handoff.");
                // A user-started updater is never adopted as an auto-close target.
                if (processes.Find(executablePath) != null)
                {
                    Diagnostics.Log("updater-session", "existing-process-not-owned");
                    return new(VendorUpdaterState.WaitingForUpdater, "The original updater is already running; Hexor will leave it open.");
                }
                if (!File.Exists(executablePath)) return new(VendorUpdaterState.UpdaterUnavailable, "Original updater is unavailable. Check the installation.");
                var launched = processes.Launch(executablePath);
                if (launched == null) return new(VendorUpdaterState.UpdaterUnavailable, "Original updater could not be started.");
                if (launched.StartTimeUtc == null || !PathsEqual(launched.ExecutablePath, executablePath))
                    return new(VendorUpdaterState.CompletionLeftOpen, "Original updater started, but its process identity could not be verified. It will be left open.");
                Volatile.Write(ref sessionOrigin, launched);
                Volatile.Write(ref launchedProcess, launched);
                Volatile.Write(ref verifiedCompletion, null);
                Interlocked.Exchange(ref completionCloseAttempted, 0);
                Diagnostics.Log("updater-session", $"launch-succeeded; {ProcessDiagnostic(launched)}");
                return new(VendorUpdaterState.UpdaterStarting, "Updater starting. Windows may request permission.");
            }
            catch (Win32Exception) { return Permission(); }
            catch (UnauthorizedAccessException) { return Permission(); }
            catch (InvalidOperationException) { return new(VendorUpdaterState.UpdaterUnavailable, "Original updater could not be started."); }
        }
    }

    // Kept for the existing integration contract. The customer workflow uses the explicit
    // observation/consent methods below and never calls this automatic-close entry point.
    public Task<VendorUpdaterResult?> ObserveCompletionAndCloseAsync(CancellationToken cancellationToken = default,
        Func<string>? cancellationOrigin = null) => ObserveCompletionCoreAsync(true, cancellationToken, cancellationOrigin, null);

    public Task<VendorUpdaterResult?> ObserveCompletionAsync(CancellationToken cancellationToken = default,
        Func<string>? cancellationOrigin = null, Action<VendorUpdaterResult>? progress = null)
        => ObserveCompletionCoreAsync(false, cancellationToken, cancellationOrigin, progress);

    public VendorUpdaterProcess? GetOwnedProcess()
    {
        var identity = Volatile.Read(ref launchedProcess);
        if (identity == null || executablePath == null) return null;
        try
        {
            var current = processes.FindOwned(executablePath, identity);
            return current != null && ValidProcess(identity, current) ? current : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Diagnostics.Log("updater-window", "identity-unavailable; type=" + e.GetType().Name);
            return null;
        }
    }

    private async Task<VendorUpdaterResult?> ObserveCompletionCoreAsync(bool closeAutomatically, CancellationToken cancellationToken,
        Func<string>? cancellationOrigin, Action<VendorUpdaterResult>? progress)
    {
        if (Interlocked.CompareExchange(ref observerActive, 1, 0) != 0)
        {
            Diagnostics.Log("updater-monitor", "observer-stopped; reason=already-active");
            return null;
        }
        Volatile.Write(ref verifiedCompletion, null);
        VendorUpdaterState? lastProgressState = null;
        void ReportProgress(bool updating)
        {
            if (closeAutomatically || progress == null) return;
            var state = updating ? VendorUpdaterState.WaitingForUpdater : VendorUpdaterState.CheckUpdaterStatus;
            if (state == lastProgressState) return;
            lastProgressState = state;
            try { progress(new(state, updating ? "Updating..." : "Check Updater Status")); }
            catch (Exception e) { Diagnostics.Log("updater-monitor", "progress-notification-failed; type=" + e.GetType().Name); }
        }
        var reason = "faulted";
        var observerTimer = Stopwatch.StartNew();
        Diagnostics.Log("updater-monitor", "observer-started");
        var cancellationLogLock = new object();
        var cancellationLogged = false;
        void LogCancellationRequested()
        {
            lock (cancellationLogLock)
            {
                if (cancellationLogged) return;
                Diagnostics.Log("updater-monitor", $"cancellation-requested; source={CancellationSource(cancellationOrigin)}; elapsed-ms={(long)observerTimer.Elapsed.TotalMilliseconds}");
                cancellationLogged = true;
            }
        }
        using var cancellationRegistration = cancellationToken.Register(LogCancellationRequested);
        try
        {
            // The complete observer, including graceful close, runs outside the WPF dispatcher.
            return await Task.Run(async () =>
            {
                var identity = Volatile.Read(ref launchedProcess);
                if (executablePath == null || identity == null)
                {
                    reason = "not-owned";
                    return new VendorUpdaterResult(VendorUpdaterState.CompletionLeftOpen, "Hexor only automatically closes an updater that it launched.");
                }
                var origin = Volatile.Read(ref sessionOrigin) ?? identity;
                var failures = 0;
                var lastWindow = IntPtr.Zero;
                var completionEvidenceCount = 0;
                var completionEvidenceWindow = IntPtr.Zero;
                var identityLogged = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(identity, Volatile.Read(ref launchedProcess))) { reason = "session-replaced"; return null; }
                    VendorUpdaterProcess? process;
                    try { process = processes.FindOwned(executablePath, identity); }
                    catch (UnauthorizedAccessException) { reason = "permission-denied"; return Permission(); }
                    if (process == null)
                    {
                        Diagnostics.Log("updater-monitor", $"process-exited; {ProcessDiagnostic(identity)}; completion=false");
                        var replacement = await DiscoverHandoffAsync(identity, origin, cancellationToken).ConfigureAwait(false);
                        if (replacement == null)
                        {
                            lock (sessionLock)
                            {
                                if (ReferenceEquals(identity, launchedProcess))
                                {
                                    Volatile.Write(ref launchedProcess, null);
                                    Volatile.Write(ref sessionOrigin, null);
                                }
                            }
                            reason = "handoff-unverified";
                            return new(VendorUpdaterState.CompletionLeftOpen, "Updater monitoring ended because no unique replacement could be verified. Any running updater was left open.");
                        }
                        lock (sessionLock)
                        {
                            if (!ReferenceEquals(identity, launchedProcess)) { reason = "session-replaced"; return null; }
                            if (automationWorker != null) discardWorkerResult = true;
                            Volatile.Write(ref launchedProcess, replacement);
                            identity = replacement;
                            Interlocked.Exchange(ref completionCloseAttempted, 0);
                        }
                        Diagnostics.Log("updater-handoff", $"monitoring-transferred; original-pid={origin.Id}; current-pid={replacement.Id}; start={replacement.StartTimeUtc:O}");
                        identityLogged = false;
                        lastWindow = IntPtr.Zero;
                        completionEvidenceCount = 0;
                        completionEvidenceWindow = IntPtr.Zero;
                        continue;
                    }
                    if (!ValidProcess(identity, process))
                    {
                        reason = "identity-unverified";
                        return new(VendorUpdaterState.CompletionLeftOpen, "Original updater process identity could not be verified. It was left open.");
                    }
                    if (!identityLogged)
                    {
                        identityLogged = true;
                        Diagnostics.Log("updater-monitor", $"process-identity-verified; {ProcessDiagnostic(process)}");
                    }
                    if (automationWorker != null && discardWorkerResult)
                    {
                        if (!automationWorker.IsCompleted)
                        {
                            ReportProgress(false);
                            LogRetainedWorker();
                            await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        // A late completion belongs to a stale snapshot and must never authorize closing.
                        try { await automationWorker.ConfigureAwait(false); }
                        catch (Exception e) { LogAutomationException(e, failures); }
                        Diagnostics.Log("updater-monitor", $"uia-worker-recovered; {OperationDiagnostic()}; stale-result-discarded=true");
                        ClearAutomationWorker();
                        discardWorkerResult = false;
                    }
                    VendorUpdaterUi ui;
                    try
                    {
                        automationWorker = StartAutomationWorker(process, "probe", () => automation.Probe(process));
                        ObserveFault(automationWorker);
                        ui = await automationWorker.WaitAsync(automationProbeTimeout, cancellationToken).ConfigureAwait(false);
                        ClearAutomationWorker();
                    }
                    catch (TimeoutException)
                    {
                        ReportProgress(false);
                        discardWorkerResult = true;
                        failures++;
                        Diagnostics.Log("updater-monitor", $"uia-probe-timeout; {OperationDiagnostic()}; timeout-ms={(long)automationProbeTimeout.TotalMilliseconds}; worker-still-running={!automationWorker!.IsCompleted}");
                        await RetryAsync(failures, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception e) when (Recoverable(e))
                    {
                        ReportProgress(false);
                        ClearAutomationWorker();
                        failures++;
                        LogAutomationException(e, failures);
                        await RetryAsync(failures, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (UnauthorizedAccessException) { reason = "permission-denied"; return Permission(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        LogAutomationException(e, failures);
                        reason = "unsupported-uia-failure";
                        return new(VendorUpdaterState.CompletionLeftOpen, "Original updater UI could not be inspected safely. It was left open.");
                    }
                    // The process may have exited or recreated its window while the probe was running.
                    // Its old success result must not authorize a close or a handoff completion claim.
                    VendorUpdaterProcess? currentProcess;
                    try { currentProcess = processes.FindOwned(executablePath, identity); }
                    catch (UnauthorizedAccessException) { reason = "permission-denied"; return Permission(); }
                    if (currentProcess == null || currentProcess.MainWindowHandle != process.MainWindowHandle) continue;
                    if (failures > 0)
                    {
                        Diagnostics.Log("updater-monitor", $"observer-recovered; pid={process.Id}; failures={failures}");
                        failures = 0;
                    }
                    if (ui.PermissionDenied) { reason = "permission-denied"; return Permission(); }
                    if (ui.WindowFound && lastWindow != process.MainWindowHandle)
                    {
                        lastWindow = process.MainWindowHandle;
                        Diagnostics.Log("updater-monitor", $"window-discovered; {ProcessDiagnostic(process)}; uia-owner-pid={ui.TargetProcessId}");
                    }
                    if (!ReferenceEquals(identity, Volatile.Read(ref launchedProcess))) { reason = "session-replaced"; return null; }
                    if (!ui.CompletionReady || ui.UpdateInProgress || ui.RequiresAttention)
                    {
                        ReportProgress(ui.WindowFound && ui.UpdateInProgress && !ui.RequiresAttention);
                        if (completionEvidenceCount > 0)
                            Diagnostics.Log("updater-monitor", $"completion-evidence-reset; pid={process.Id}; prior-samples={completionEvidenceCount}; reason=state-changed");
                        completionEvidenceCount = 0;
                        completionEvidenceWindow = IntPtr.Zero;
                        await Task.Delay(completionPollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    var hostHandle = hexorMainWindowHandle();
                    if (!ui.WindowFound || !ui.CompletionControlsVisible || !ui.TargetWindowVerified
                        || ui.TargetProcessId != process.Id || ui.TargetWindowHandle != process.MainWindowHandle
                        || ui.TargetProcessStartTimeUtc != identity.StartTimeUtc || !PathsEqual(ui.TargetExecutablePath, executablePath)
                        || process.MainWindowHandle == IntPtr.Zero
                        || (hostHandle != IntPtr.Zero && ui.TargetWindowHandle == hostHandle))
                    {
                        reason = "window-identity-unverified";
                        return new(VendorUpdaterState.CompletionLeftOpen, "Original updater window identity could not be verified. It was left open.");
                    }
                    if (!identity.SameInstance(origin) && !UniqueReplacement(origin, process))
                    {
                        reason = "replacement-identity-ambiguous";
                        return new(VendorUpdaterState.CompletionLeftOpen, "Updater completion was detected, but its replacement identity is ambiguous. It was left open.");
                    }
                    if (!CanCloseVerifiedVendorWindow(identity, origin, process, ui))
                    {
                        // A recreated window is recoverable: the next probe reacquires its controls.
                        await RetryAsync(1, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (completionEvidenceWindow != process.MainWindowHandle)
                    {
                        completionEvidenceWindow = process.MainWindowHandle;
                        completionEvidenceCount = 0;
                    }
                    completionEvidenceCount++;
                    Diagnostics.Log("updater-monitor", $"completion-evidence; pid={process.Id}; samples={completionEvidenceCount}/{completionConfirmationSamples}; controls=verified; active-ui=false");
                    if (completionEvidenceCount < completionConfirmationSamples)
                    {
                        await Task.Delay(completionPollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    Diagnostics.Log("updater-monitor", $"installation-completion-confirmed; pid={process.Id}; start={identity.StartTimeUtc:O}");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!closeAutomatically)
                    {
                        lock (sessionLock)
                        {
                            if (!ReferenceEquals(identity, launchedProcess)) { reason = "session-replaced"; return null; }
                            Volatile.Write(ref verifiedCompletion, new(identity, origin, process, ui));
                        }
                        reason = "completion-awaiting-customer";
                        Diagnostics.Log("updater-monitor", $"completion-awaiting-customer; pid={process.Id}; automatic-close=false");
                        return new(VendorUpdaterState.CompletionVerified, "Update Complete");
                    }
                    if (Interlocked.CompareExchange(ref completionCloseAttempted, 1, 0) != 0) { reason = "close-already-requested"; return null; }
                    var closeAuthorized = 1;
                    try
                    {
                        Diagnostics.Log("updater-monitor", $"close-requested; pid={process.Id}; method=window-pattern");
                        automationWorker = StartAutomationWorker(process, "close", () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!ReferenceEquals(identity, Volatile.Read(ref launchedProcess))
                                || !CanCloseVerifiedVendorWindow(identity, origin, process, ui)) throw new ElementNotAvailableException();
                            automation.CloseWindow(ui with { TargetProcessStartTimeUtc = identity.StartTimeUtc, TargetExecutablePath = executablePath },
                                () => Volatile.Read(ref closeAuthorized) != 0 && !cancellationToken.IsCancellationRequested
                                    && ReferenceEquals(identity, Volatile.Read(ref launchedProcess))
                                    && CanCloseVerifiedVendorWindow(identity, origin, process, ui));
                            return ui;
                        });
                        ObserveFault(automationWorker);
                        await automationWorker.WaitAsync(automationProbeTimeout, cancellationToken).ConfigureAwait(false);
                        ClearAutomationWorker();
                    }
                    catch (Exception e) when (Recoverable(e))
                    {
                        ClearAutomationWorker();
                        Interlocked.Exchange(ref completionCloseAttempted, 0);
                        failures++;
                        Diagnostics.Log("updater-monitor", $"close-result; outcome=recoverable-error; pid={process.Id}; type={e.GetType().Name}; hresult=0x{e.HResult:X8}");
                        LogAutomationException(e, failures);
                        await RetryAsync(failures, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        Diagnostics.Log("updater-monitor", $"close-result; outcome=cancelled; pid={process.Id}; {OperationDiagnostic()}");
                        throw;
                    }
                    catch (Exception e)
                    {
                        LogAutomationException(e, failures);
                        reason = e is TimeoutException ? "graceful-close-timeout" : "graceful-close-failed";
                        Diagnostics.Log("updater-monitor", $"close-result; outcome={(e is TimeoutException ? "timeout" : "failed")}; pid={process.Id}; {OperationDiagnostic()}");
                        return new(VendorUpdaterState.CompletionLeftOpen, "Original updater completed but did not accept a graceful close request. It was left open.");
                    }
                    finally { Interlocked.Exchange(ref closeAuthorized, 0); }
                    reason = "graceful-close-finished";
                    return await WaitForVendorExitAsync(identity, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = "cancelled";
            // A later cancellation callback can finish this observer and dispose the earlier
            // registration before it runs. Preserve the request record on either path, once.
            LogCancellationRequested();
            Diagnostics.Log("updater-monitor", $"observer-cancelled; source={CancellationSource(cancellationOrigin)}; elapsed-ms={(long)observerTimer.Elapsed.TotalMilliseconds}");
            return null;
        }
        finally
        {
            if (automationWorker != null)
            {
                discardWorkerResult = true;
                Diagnostics.Log("updater-monitor", $"uia-worker-retained; {OperationDiagnostic()}; worker-still-running={!automationWorker.IsCompleted}");
            }
            Diagnostics.Log("updater-monitor", $"observer-stopped; reason={reason}; elapsed-ms={(long)observerTimer.Elapsed.TotalMilliseconds}");
            Volatile.Write(ref observerActive, 0);
        }
    }

    // A verified observation is only permission to offer the button. The customer click
    // consumes that observation and rechecks both process identity and the current UI.
    public async Task<VendorUpdaterResult> CloseVerifiedUpdaterAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref observerActive, 1, 0) != 0)
            return CheckStatus("Updater monitoring is busy. Show the updater to check its status.");
        var closeAuthorized = 0;
        try
        {
            var verified = Interlocked.Exchange(ref verifiedCompletion, null);
            if (verified == null || executablePath == null || cancellationToken.IsCancellationRequested)
                return CheckStatus("Completion must be verified before requesting closure.");
            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (automationWorker != null)
                {
                    if (!automationWorker.IsCompleted)
                        return CheckStatus("An updater inspection is still running. The updater was left open.");
                    // A late COM result is never reused as completion evidence or authorization.
                    try { await automationWorker.ConfigureAwait(false); }
                    catch (Exception e) { LogAutomationException(e, 0); }
                    ClearAutomationWorker();
                    discardWorkerResult = false;
                }
                var identity = verified.Identity;
                if (!ReferenceEquals(identity, Volatile.Read(ref launchedProcess)))
                    return CheckStatus("The updater session changed. It was left open.");
                var current = processes.FindOwned(executablePath, identity);
                if (current == null || current.MainWindowHandle != verified.Process.MainWindowHandle
                    || !CanCloseVerifiedVendorWindow(identity, verified.Origin, current, verified.Ui))
                    return CheckStatus("The updater window or process changed. It was left open.");

                automationWorker = StartAutomationWorker(current, "consent-probe", () => automation.Probe(current));
                ObserveFault(automationWorker);
                var freshUi = await automationWorker.WaitAsync(automationProbeTimeout, cancellationToken).ConfigureAwait(false);
                ClearAutomationWorker();
                if (cancellationToken.IsCancellationRequested || !ReferenceEquals(identity, Volatile.Read(ref launchedProcess))
                    || !CanCloseVerifiedVendorWindow(identity, verified.Origin, current, freshUi))
                    return CheckStatus("Update completion could not be revalidated. The updater was left open.");
                if (Interlocked.CompareExchange(ref completionCloseAttempted, 1, 0) != 0)
                    return CheckStatus("A graceful close was already requested. Check the updater window.");
                Volatile.Write(ref closeAuthorized, 1);
                Diagnostics.Log("updater-monitor", $"customer-close-requested; pid={identity.Id}; method=window-pattern");
                automationWorker = StartAutomationWorker(current, "consent-close", () =>
                {
                    bool CanClose() => Volatile.Read(ref closeAuthorized) != 0 && !cancellationToken.IsCancellationRequested
                        && ReferenceEquals(identity, Volatile.Read(ref launchedProcess))
                        && CanCloseVerifiedVendorWindow(identity, verified.Origin, current, freshUi);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanClose()) throw new ElementNotAvailableException();
                    automation.CloseWindow(freshUi, CanClose);
                    return freshUi;
                });
                ObserveFault(automationWorker);
                await automationWorker.WaitAsync(automationProbeTimeout, cancellationToken).ConfigureAwait(false);
                ClearAutomationWorker();
                return await WaitForVendorExitAsync(identity, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Diagnostics.Log("updater-monitor", "customer-close-result; outcome=cancelled");
            return CheckStatus("The close request was cancelled. Check the updater window.");
        }
        catch (Exception e)
        {
            Diagnostics.Log("updater-monitor", $"customer-close-result; outcome=unverified; type={e.GetType().Name}; hresult=0x{e.HResult:X8}");
            return CheckStatus("The updater did not accept a verified graceful close. Check its window.");
        }
        finally
        {
            Interlocked.Exchange(ref closeAuthorized, 0);
            if (automationWorker != null)
            {
                discardWorkerResult = true;
                Diagnostics.Log("updater-monitor", $"uia-worker-retained; {OperationDiagnostic()}; worker-still-running={!automationWorker.IsCompleted}");
            }
            Volatile.Write(ref observerActive, 0);
        }
    }

    private static VendorUpdaterResult CheckStatus(string message) => new(VendorUpdaterState.CheckUpdaterStatus, message);

    private async Task<VendorUpdaterProcess?> DiscoverHandoffAsync(VendorUpdaterProcess predecessor, VendorUpdaterProcess origin, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var logged = new HashSet<string>(StringComparer.Ordinal);
        var outcome = "faulted";
        var attempts = 0;
        var candidatesObserved = 0;
        var acceptedPid = 0;
        Diagnostics.Log("updater-handoff", $"discovery-started; {ProcessDiagnostic(predecessor)}; timeout-ms={(long)handoffTimeout.TotalMilliseconds}");
        try
        {
            while (timer.Elapsed < handoffTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(predecessor, Volatile.Read(ref launchedProcess))) { outcome = "session-replaced"; return null; }
                attempts++;
                IReadOnlyList<VendorUpdaterHandoffCandidate> candidates;
                try { candidates = processes.FindHandoffCandidates(executablePath!, predecessor); }
                catch (Exception e) when (e is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
                {
                    var message = $"candidate-discovery-rejected; reason=process-inspection-unavailable; type={e.GetType().Name}";
                    if (logged.Add(message)) Diagnostics.Log("updater-handoff", message);
                    await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                candidatesObserved += candidates.Count;
                var verified = new List<VendorUpdaterProcess>();
                var related = new List<VendorUpdaterProcess>();
                foreach (var candidate in candidates)
                {
                    var process = candidate.Process;
                    var rejection = CandidateRejection(origin, candidate);
                    var message = $"replacement-discovered; decision={(rejection ?? "eligible")}; pid={process.Id}; start={process.StartTimeUtc:O}; handle={process.MainWindowHandle.ToInt64()}; window-owner-pid={candidate.WindowOwnerProcessId}";
                    if (logged.Add(message))
                    {
                        Diagnostics.Log("updater-handoff", message);
                        Diagnostics.Log("updater-handoff", $"candidate-path; pid={process.Id}; path={DiagnosticPath(process.ExecutablePath)}");
                    }
                    if (PlausibleCandidate(rejection) && !related.Any(p => p.SameInstance(process))) related.Add(process);
                    if (rejection == null && !verified.Any(p => p.SameInstance(process))) verified.Add(process);
                }
                if (related.Count > 1)
                {
                    outcome = "ambiguous";
                    Diagnostics.Log("updater-handoff", $"handoff-rejected; reason=ambiguous-session-candidates; count={related.Count}");
                    return null;
                }
                if (verified.Count == 1)
                {
                    var replacement = verified[0];
                    outcome = "accepted";
                    acceptedPid = replacement.Id;
                    Diagnostics.Log("updater-handoff", $"replacement-identity-verified; evidence=unique-installed-path-session-time-window-owner; original-pid={origin.Id}; predecessor-pid={predecessor.Id}; pid={replacement.Id}; start={replacement.StartTimeUtc:O}");
                    return replacement;
                }
                await Task.Delay(retryInterval, cancellationToken).ConfigureAwait(false);
            }
            outcome = "timeout";
            Diagnostics.Log("updater-handoff", $"handoff-rejected; predecessor-pid={predecessor.Id}; reason=discovery-timeout; completion=false");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            Diagnostics.Log("updater-handoff", $"discovery-completed; outcome={outcome}; predecessor-pid={predecessor.Id}; accepted-pid={acceptedPid}; elapsed-ms={(long)timer.Elapsed.TotalMilliseconds}; attempts={attempts}; candidates-observed={candidatesObserved}");
        }
    }

    private string? CandidateRejection(VendorUpdaterProcess origin, VendorUpdaterHandoffCandidate candidate)
    {
        var process = candidate.Process;
        if (process.Id == hexorProcessId) return "hexor-process";
        if (process.SameInstance(origin)) return "original-session-process";
        if (candidate.RejectionReason != null) return candidate.RejectionReason;
        if (!PathsEqual(process.ExecutablePath, executablePath)) return "executable-path-mismatch";
        if (process.StartTimeUtc == null || origin.StartTimeUtc == null) return "creation-time-unverified";
        if (process.StartTimeUtc < origin.StartTimeUtc || process.StartTimeUtc > DateTime.UtcNow) return "creation-time-outside-session";
        if (process.MainWindowHandle == IntPtr.Zero) return "window-not-ready";
        if (candidate.WindowOwnerProcessId != process.Id) return "window-owner-mismatch";
        if (process.MainWindowHandle == hexorMainWindowHandle()) return "hexor-window";
        return null;
    }

    private static bool PlausibleCandidate(string? rejection) => rejection == null
        || rejection is "window-not-ready" or "window-owner-mismatch" or "candidate-inspection-denied" or "creation-time-unverified";

    private bool UniqueReplacement(VendorUpdaterProcess origin, VendorUpdaterProcess current)
    {
        try
        {
            var candidates = processes.FindHandoffCandidates(executablePath!, current);
            var plausible = candidates.Where(c => PlausibleCandidate(CandidateRejection(origin, c)))
                .GroupBy(c => (c.Process.Id, c.Process.StartTimeUtc)).Select(g => g.First()).ToArray();
            var unique = plausible.Length == 1 && plausible[0].Process.SameInstance(current)
                && CandidateRejection(origin, plausible[0]) == null
                && plausible[0].Process.MainWindowHandle == current.MainWindowHandle;
            if (!unique) Diagnostics.Log("updater-handoff", $"close-revalidation-rejected; reason=ambiguous-or-changed-replacement; pid={current.Id}; plausible-count={plausible.Length}");
            return unique;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Diagnostics.Log("updater-handoff", $"close-revalidation-rejected; reason=process-inspection-unavailable; type={e.GetType().Name}");
            return false;
        }
    }

    private static string ProcessDiagnostic(VendorUpdaterProcess process) => $"pid={process.Id}; start={process.StartTimeUtc:O}; handle={process.MainWindowHandle.ToInt64()}; window-owner-pid={process.WindowOwnerProcessId}; path={DiagnosticPath(process.ExecutablePath)}";
    private static string DiagnosticPath(string? path)
    {
        var value = path ?? "unavailable";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) value = value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return value.Replace('\r', '_').Replace('\n', '_');
    }

    private async Task<VendorUpdaterResult> WaitForVendorExitAsync(VendorUpdaterProcess identity, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(1))
        {
            try
            {
                if (processes.FindOwned(executablePath!, identity) == null)
                {
                    Diagnostics.Log("updater-monitor", $"process-exited; pid={identity.Id}; completion=true");
                    Diagnostics.Log("updater-monitor", $"close-result; outcome=exited; pid={identity.Id}; elapsed-ms={(long)timer.Elapsed.TotalMilliseconds}");
                    return new(VendorUpdaterState.CompletionClosed, "Original updater completed and was closed. Hexor remains open.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                Diagnostics.Log("updater-monitor", $"close-result; outcome=permission-denied; pid={identity.Id}; elapsed-ms={(long)timer.Elapsed.TotalMilliseconds}");
                return Permission();
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Diagnostics.Log("updater-monitor", $"close-result; outcome=cancelled; pid={identity.Id}; elapsed-ms={(long)timer.Elapsed.TotalMilliseconds}");
                throw;
            }
        }
        Diagnostics.Log("updater-monitor", $"close-result; outcome=still-running; pid={identity.Id}; elapsed-ms={(long)timer.Elapsed.TotalMilliseconds}");
        return new(VendorUpdaterState.CompletionLeftOpen, "Original updater completed but remained open. You can close it manually.");
    }

    private bool ValidProcess(VendorUpdaterProcess identity, VendorUpdaterProcess process) => identity.SameInstance(process)
        && process.Id != hexorProcessId && PathsEqual(process.ExecutablePath, executablePath);

    private bool CanCloseVerifiedVendorWindow(VendorUpdaterProcess identity, VendorUpdaterProcess origin, VendorUpdaterProcess process, VendorUpdaterUi ui)
    {
        var hostHandle = hexorMainWindowHandle();
        if (!ValidProcess(identity, process) || !ui.CompletionReady || ui.UpdateInProgress || ui.RequiresAttention
            || !ui.WindowFound || !ui.CompletionControlsVisible || !ui.TargetWindowVerified
            || process.MainWindowHandle == IntPtr.Zero || ui.TargetProcessId != process.Id || ui.TargetWindowHandle != process.MainWindowHandle
            || ui.TargetProcessStartTimeUtc != identity.StartTimeUtc || !PathsEqual(ui.TargetExecutablePath, executablePath)
            || (hostHandle != IntPtr.Zero && (process.MainWindowHandle == hostHandle || ui.TargetWindowHandle == hostHandle))) return false;
        try
        {
            var current = processes.FindOwned(executablePath!, identity);
            return current != null && ValidProcess(identity, current) && current.MainWindowHandle == process.MainWindowHandle
                && (identity.SameInstance(origin) || UniqueReplacement(origin, current));
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private Task<VendorUpdaterUi> StartAutomationWorker(VendorUpdaterProcess process, string kind, Func<VendorUpdaterUi> action)
    {
        var operation = new AutomationOperation(Interlocked.Increment(ref automationSequence), process.Id, kind);
        automationOperation = operation;
        Diagnostics.Log("updater-monitor", $"uia-{kind}-start; operation={operation.Id}; pid={process.Id}; timeout-ms={(long)automationProbeTimeout.TotalMilliseconds}");
        return Task.Run(() =>
        {
            Diagnostics.Log("updater-monitor", $"uia-worker-started; operation={operation.Id}; pid={process.Id}; kind={kind}; queue-ms={(long)operation.Timer.Elapsed.TotalMilliseconds}");
            var execution = Stopwatch.StartNew();
            try
            {
                var result = action();
                execution.Stop();
                operation.Timer.Stop();
                Diagnostics.Log("updater-monitor", $"uia-{kind}-completed; operation={operation.Id}; pid={process.Id}; duration-ms={(long)operation.Timer.Elapsed.TotalMilliseconds}; execution-ms={(long)execution.Elapsed.TotalMilliseconds}");
                return result;
            }
            catch (Exception e)
            {
                execution.Stop();
                operation.Timer.Stop();
                Diagnostics.Log("updater-monitor", $"uia-{kind}-failed; operation={operation.Id}; pid={process.Id}; duration-ms={(long)operation.Timer.Elapsed.TotalMilliseconds}; execution-ms={(long)execution.Elapsed.TotalMilliseconds}; type={e.GetType().Name}; hresult=0x{e.HResult:X8}");
                throw;
            }
        });
    }

    private void ClearAutomationWorker()
    {
        automationWorker = null;
        automationOperation = null;
    }

    private void LogRetainedWorker()
    {
        var operation = automationOperation;
        if (operation == null) return;
        var elapsed = (long)operation.Timer.Elapsed.TotalMilliseconds;
        if (elapsed - operation.LastStillRunningMs < 1000) return;
        operation.LastStillRunningMs = elapsed;
        Diagnostics.Log("updater-monitor", $"uia-worker-still-running; {OperationDiagnostic()}; replacement-worker-started=false");
    }

    private string OperationDiagnostic()
    {
        var operation = automationOperation;
        return operation == null ? "operation=none" : $"operation={operation.Id}; pid={operation.ProcessId}; kind={operation.Kind}; duration-ms={(long)operation.Timer.Elapsed.TotalMilliseconds}";
    }

    private static string CancellationSource(Func<string>? origin)
    {
        try
        {
            return origin?.Invoke() switch
            {
                "explicit-user" => "explicit-user",
                "hexor-shutdown" => "hexor-shutdown",
                "application-shutdown" => "application-shutdown",
                "main-window-closed" => "main-window-closed",
                "viewmodel-disposed" => "viewmodel-disposed",
                "unspecified" => "unspecified",
                _ => "external-token"
            };
        }
        catch { return "external-token"; }
    }

    private sealed class AutomationOperation(long id, int processId, string kind)
    {
        public long Id { get; } = id;
        public int ProcessId { get; } = processId;
        public string Kind { get; } = kind;
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
        public long LastStillRunningMs;
    }

    private Task RetryAsync(int failures, CancellationToken token)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(1000, retryInterval.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 4))));
        Diagnostics.Log("updater-monitor", $"retry-scheduled; failures={failures}; delay-ms={(long)delay.TotalMilliseconds}");
        return Task.Delay(delay, token);
    }
    private static TimeSpan BoundDelay(TimeSpan value) => TimeSpan.FromMilliseconds(Math.Clamp(value.TotalMilliseconds, 1, 1000));
    private static bool Recoverable(Exception e) => e is COMException or ElementNotAvailableException;
    private static void LogAutomationException(Exception e, int failures) => Diagnostics.Log("updater-monitor",
        $"uia-exception; type={e.GetType().Name}; hresult=0x{e.HResult:X8}; failures={failures}");
    private static void ObserveFault(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static bool PathsEqual(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static VendorUpdaterResult Permission() => new(VendorUpdaterState.PermissionMismatch,
        "Hexor cannot access the elevated updater window. Run both applications at the same Windows permission level, or enter the key in the original updater.");
}

public sealed class WindowsVendorUpdaterAutomationAdapter : IVendorUpdaterAutomationAdapter
{
    private const string WindowId = "MainWindowInstance";
    private const string WindowName = "AION.ONL UPDATER";
    public const string VerifiedCompletionMessage = "组件已成功安装，请直接启动游戏";

    public static bool IsCompletionReady(string? message, bool completionControlsVisible, bool updateInProgress, bool activationPending, bool installationFailed)
    {
        // Completion is a reviewed structural UI state. Text may be localized, but a nonempty
        // message plus all exact completion controls is required; time/download-idle is never used.
        var text = message?.Trim() ?? "";
        return text.Length > 0 && completionControlsVisible && !updateInProgress && !activationPending && !installationFailed;
    }

    public VendorUpdaterUi Probe(VendorUpdaterProcess process)
    {
        if (process.MainWindowHandle == IntPtr.Zero) return new(false, false);
        // Never cache UIA references: each attempt resolves the current HWND and every child again.
        AutomationElement? window;
        try { window = AutomationElement.FromHandle(process.MainWindowHandle); }
        // The HWND can disappear after the process snapshot. UIA can report an invalid argument
        // or return no element for that handle; both require a fresh window snapshot on the next poll.
        catch (ArgumentException) { return new(false, false); }
        if (window == null || window.Current.ProcessId != process.Id || (IntPtr)window.Current.NativeWindowHandle != process.MainWindowHandle) return new(false, false);
        AutomationElement? Find(string id) => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        var completeMessage = Find("CompleteMessageText");
        var completeStart = Find("CompleteStartGameButton");
        var messageVisible = Visible(completeMessage);
        var completionControlsVisible = messageVisible && completeMessage!.Current.ControlType == ControlType.Text
            && VisibleText(Find("F10HelpText")) && VisibleText(Find("UpdaterReminderText"))
            && completeStart != null && Visible(completeStart)
            && completeStart.Current.ControlType == ControlType.Button && completeStart.Current.IsEnabled
            && completeStart.TryGetCurrentPattern(InvokePattern.Pattern, out _);
        // WPF panels may not be UIA-exposed. Their named children independently block premature close.
        var updateInProgress = new[] { "UpdateView", "UpdatingText", "UpdateProgressBar", "DownloadStatusText", "ProgressText" }.Any(id => Visible(Find(id)));
        var activationPending = new[] { "LoginView", "CardInput", "LoginButton", "StatusText" }.Any(id => Visible(Find(id)));
        var installationFailed = Visible(Find("RetryUpdateButton"));
        var ready = IsCompletionReady(messageVisible ? completeMessage!.Current.Name : null, completionControlsVisible,
            updateInProgress, activationPending, installationFailed);
        return new(true, false, ready, window, TargetProcessId: process.Id, TargetWindowHandle: process.MainWindowHandle,
            TargetWindowVerified: true, CompletionControlsVisible: completionControlsVisible,
            TargetProcessStartTimeUtc: process.StartTimeUtc, TargetExecutablePath: process.ExecutablePath,
            UpdateInProgress: updateInProgress, RequiresAttention: activationPending || installationFailed,
            Diagnostic: $"completion-controls={completionControlsVisible}; update-active={updateInProgress}; activation-pending={activationPending}; failure={installationFailed}");
    }

    public void CloseWindow(VendorUpdaterUi ui) => CloseWindow(ui, () => true);

    public void CloseWindow(VendorUpdaterUi ui, Func<bool> canClose)
    {
        if (!canClose() || ui.TargetProcessId == Environment.ProcessId || !ui.TargetWindowVerified || ui.TargetWindowHandle == IntPtr.Zero
            || !OwnedWindowStillCurrent(ui)) throw new ElementNotAvailableException();
        // A snapshot can go stale between detection and close. Rebuild and recheck the complete state,
        // then use only WindowPattern.Close; the launch-console/game button is never invoked.
        var current = Probe(new(ui.TargetProcessId, ui.TargetWindowHandle, ui.TargetExecutablePath, ui.TargetProcessStartTimeUtc));
        if (!current.CompletionReady || current.Window is not AutomationElement window
            || !window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern)
            || !canClose() || !OwnedWindowStillCurrent(ui)) throw new ElementNotAvailableException();
        ((WindowPattern)pattern).Close();
    }

    private static bool OwnedWindowStillCurrent(VendorUpdaterUi ui)
    {
        if (ui.TargetProcessStartTimeUtc == null || string.IsNullOrWhiteSpace(ui.TargetExecutablePath)) return false;
        var identity = new VendorUpdaterProcess(ui.TargetProcessId, ui.TargetWindowHandle, ui.TargetExecutablePath, ui.TargetProcessStartTimeUtc);
        using var processes = new WindowsVendorUpdaterProcessAdapter();
        var current = processes.FindOwned(ui.TargetExecutablePath, identity);
        return current != null && current.MainWindowHandle == ui.TargetWindowHandle
            && string.Equals(current.ExecutablePath, ui.TargetExecutablePath, StringComparison.OrdinalIgnoreCase);
    }
    private static bool Visible(AutomationElement? element) => element != null && !element.Current.IsOffscreen;
    private static bool VisibleText(AutomationElement? element) => Visible(element) && element!.Current.ControlType == ControlType.Text;
}
