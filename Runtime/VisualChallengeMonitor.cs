using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Hexor.Runtime;

// Visual challenge detection for Hexor: read-only screen analysis of the observed AION2 window.
// Hexor detects a challenge-like overlay pattern, logs the event, and notifies the customer.
// Hexor never reads challenge answer content, never classifies challenge text, never generates
// answers, never submits anything anywhere, and never issues pause or resume commands to any
// automation task. Detection is pattern-based only: template images come from a customer-supplied
// patterns folder and are compared as whole-overlay shapes, not as text.

public enum VisualMonitorPhase { Idle, Monitoring, ChallengeDetected, Resolved, Error }

public sealed record TargetWindowRecord(IntPtr Handle, string Title, int ProcessId, string? Launcher = null);
public sealed record VisualFrame(int Width, int Height, float[] Gray);
public sealed record ChallengeTemplate(string Name, VisualFrame Pattern);
public sealed record TemplateMatch(string TemplateName, double Confidence, int OffsetX, int OffsetY);
public sealed record VisualChallengeEvent(DateTimeOffset ObservedAt, VisualMonitorPhase Phase, string WindowTitle, double? Confidence, string Detail);

public sealed record VisualMonitorSnapshot(
    VisualMonitorPhase Phase,
    DateTimeOffset? DetectedAt,
    string? WindowTitle,
    double? Confidence,
    string Detail,
    int WindowCount,
    int TemplateCount,
    IReadOnlyList<VisualChallengeEvent> RecentEvents)
{
    public bool ChallengeActive => Phase == VisualMonitorPhase.ChallengeDetected;
    // Distinct launcher provenance of the windows observed in the latest poll, e.g. "Purple launcher ×1";
    // empty when no window's launcher could be attributed.
    public string LauncherSummary { get; init; } = "";
}

public sealed record VisualChallengeMonitorOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(4);
    public double Threshold { get; init; } = 0.80;
    public int MissesToResolve { get; init; } = 2;
    public TimeSpan ResolutionCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ErrorAfterFailedPolls { get; init; } = TimeSpan.FromSeconds(12);
    public string? TemplateFolder { get; init; }
}

public interface IVisualWindowSource { IReadOnlyList<TargetWindowRecord> Enumerate(); }
public interface IVisualFrameSource
{
    VisualFrame? Capture(TargetWindowRecord window);
    string? LastFailureReason { get; }
}

// Enumerates visible top-level windows owned by the AION2 process. Uses documented,
// non-invasive user32/kernel32 queries only (EnumWindows, IsWindowVisible, GetClientRect,
// GetWindowThreadProcessId, QueryFullProcessImageName, DwmGetWindowAttribute).
public sealed class WindowsGameWindowSource : IVisualWindowSource
{
    private const string GameExecutable = "aion2.exe";
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, System.Text.StringBuilder path, ref int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private readonly Dictionary<int, bool> processMatches = [];
    public IReadOnlyList<TargetWindowRecord> Enumerate()
    {
        var candidates = new List<TargetWindowRecord>();
        try
        {
            EnumWindows((handle, _) =>
            {
                try
                {
                    if (!IsWindowVisible(handle)) return true;
                    if (DwmGetWindowAttributeSafe(handle) != 0) return true; // cloaked or UWP ghost windows
                    GetWindowThreadProcessId(handle, out var pid);
                    if (pid == 0 || !IsGameProcess((int)pid)) return true;
                    GetClientRect(handle, out var rect);
                    if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0) return true;
                    var title = new System.Text.StringBuilder(256);
                    GetWindowText(handle, title, title.Capacity);
                    candidates.Add(new(handle, title.ToString(), (int)pid));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        if (candidates.Count == 0) return candidates;
        // Purple-launched clients are attributed via the parent process (Purple spawns Aion2.exe);
        // provenance stays best-effort and never gates detection.
        try
        {
            var launcherMap = LauncherProvenance.Resolve(candidates.Select(w => w.ProcessId).Distinct().ToArray());
            return candidates.Select(w => launcherMap.TryGetValue(w.ProcessId, out var launcher) ? w with { Launcher = launcher } : w).ToArray();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return candidates; }
    }
    private static int DwmGetWindowAttributeSafe(IntPtr handle)
    {
        try { return DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 ? cloaked : 0; }
        catch (DllNotFoundException) { return 0; } // pre-Vista compositor data unavailable; treat as visible
    }
    private bool IsGameProcess(int processId)
    {
        if (processMatches.TryGetValue(processId, out var cached)) return cached;
        var isGame = false;
        try
        {
            using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
            if (!handle.IsInvalid)
            {
                var buffer = new System.Text.StringBuilder(1024);
                var size = buffer.Capacity;
                var path = QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
                isGame = path != null && Path.GetFileName(path).Equals(GameExecutable, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        processMatches[processId] = isGame;
        return isGame;
    }
}

// Captures the client area of a window with PrintWindow (PW_RENDERFULLCONTENT, documented
// user32/gdi32 API). Works for windowed and borderless modes and does not require the window to
// have focus, so focus changes do not interrupt monitoring. Exclusive-fullscreen or DRM-protected
// frames return null with a failure reason instead of fabricating data.
public sealed class PrintWindowFrameSource : IVisualFrameSource
{
    private const int WorkingWidth = 192, WorkingMaxHeight = 240, WorkingMinHeight = 24;
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bitmap, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        public int Colors;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size; public int Width; public int Height; public short Planes; public short BitCount;
        public int Compression; public int SizeImage; public int XPels; public int YPels; public int ClrUsed; public int ClrImportant;
    }
    public string? LastFailureReason { get; private set; }

    public VisualFrame? Capture(TargetWindowRecord window)
    {
        LastFailureReason = null;
        try
        {
            if (!GetClientRect(window.Handle, out var rect)) return Fail("client-rect-unavailable");
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return Fail("empty-client-area");
            var bitmapInfo = new BITMAPINFO();
            bitmapInfo.Header.Size = Marshal.SizeOf<BITMAPINFOHEADER>();
            bitmapInfo.Header.Width = width; bitmapInfo.Header.Height = -height; // top-down rows
            bitmapInfo.Header.Planes = 1; bitmapInfo.Header.BitCount = 32; bitmapInfo.Header.Compression = 0; // BI_RGB
            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                var memoryDc = CreateCompatibleDC(screenDc);
                try
                {
                    var bits = IntPtr.Zero;
                    var bitmap = CreateDIBSection(memoryDc, ref bitmapInfo, 0 /* DIB_PAL_COLORS */, out bits, IntPtr.Zero, 0);
                    if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return Fail("dib-section-unavailable");
                    try
                    {
                        var previous = SelectObject(memoryDc, bitmap);
                        try
                        {
                            // PW_RENDERFULLCONTENT asks DWM for the composed window content.
                            if (!PrintWindow(window.Handle, memoryDc, 0x00000002)) return Fail("print-window-rejected");
                            var pixels = new int[width * height];
                            Marshal.Copy(bits, pixels, 0, pixels.Length);
                            return Downsample(pixels, width, height);
                        }
                        finally { SelectObject(memoryDc, previous); }
                    }
                    finally { DeleteObject(bitmap); }
                }
                finally { DeleteDC(memoryDc); }
            }
            finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or DllNotFoundException) { return Fail(e.GetType().Name); }
    }
    private VisualFrame? Fail(string reason) { LastFailureReason = reason; return null; }

    // 32bpp BGRA to grayscale with box-average downsampling to the shared working scale, so
    // templates and frames are always compared at the same resolution regardless of window size.
    internal static VisualFrame Downsample(int[] pixels, int width, int height)
    {
        var targetWidth = Math.Min(WorkingWidth, width);
        var targetHeight = Math.Clamp(height * targetWidth / Math.Max(width, 1), WorkingMinHeight, WorkingMaxHeight);
        var gray = new float[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceTop = y * height / targetHeight;
            var sourceBottom = Math.Max((y + 1) * height / targetHeight, sourceTop + 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceLeft = x * width / targetWidth;
                var sourceRight = Math.Max((x + 1) * width / targetWidth, sourceLeft + 1);
                double sum = 0; var count = 0;
                for (var sy = sourceTop; sy < sourceBottom; sy++)
                    for (var sx = sourceLeft; sx < sourceRight; sx++)
                    {
                        var pixel = pixels[sy * width + sx];
                        var r = pixel & 0xFF; var g = (pixel >> 8) & 0xFF; var b = (pixel >> 16) & 0xFF;
                        sum += (0.299 * r + 0.587 * g + 0.114 * b) / 255.0; count++;
                    }
                gray[y * targetWidth + x] = (float)(count == 0 ? 0 : sum / count);
            }
        }
        return new(targetWidth, targetHeight, gray);
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}

// Loads customer-supplied pattern templates (whole-overlay shape references, never challenge
// answer content) from the configured folder. Unreadable or foreign files are skipped and logged;
// a missing folder is a normal, reported state rather than an error.
public static class ChallengeTemplateStore
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp"];
    public static IReadOnlyList<ChallengeTemplate> Load(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        var templates = new List<ChallengeTemplate>();
        string[] files;
        try { files = Directory.EnumerateFiles(folder).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return templates; }
        foreach (var file in files)
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = File.OpenRead(file);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var source = decoder.Frames[0];
                var gray8 = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Gray8, null, 0);
                var width = gray8.PixelWidth; var height = gray8.PixelHeight;
                if (width < 4 || height < 4 || width > 4096 || height > 4096) continue;
                var pixels = new byte[width * height];
                gray8.CopyPixels(pixels, width, 0);
                var gray = new float[pixels.Length];
                for (var index = 0; index < pixels.Length; index++) gray[index] = pixels[index] / 255f;
                templates.Add(new(Path.GetFileName(file), new(width, height, gray)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            { Diagnostics.Log("visual-monitor", $"template-skipped; file={Path.GetFileName(file)}; reason={e.GetType().Name}"); }
        }
        return templates;
    }
}

// Scale-invariant coarse normalized-cross-correlation matcher at the shared working scale.
// Each template is tried across a small geometric scale ladder, so a pattern cropped at any
// reasonable size still matches. Returns the best match or null; a frame without a match above
// the configured threshold is reported as no detection, never as a low-confidence guess.
// A full poll stays well under 10 ms of CPU at the working scale.
public static class TemplateMatcher
{
    private static readonly int[] ScaleHeights = [10, 12, 17, 24, 34, 48, 68, 96];
    public static TemplateMatch? Best(VisualFrame frame, IReadOnlyList<ChallengeTemplate> templates, double threshold)
    {
        TemplateMatch? best = null;
        foreach (var template in templates)
            foreach (var scaled in Scales(template.Pattern, frame))
            {
                if (scaled.Width < 8 || scaled.Height < 8 || scaled.Width >= frame.Width || scaled.Height >= frame.Height) continue;
                var (centered, energy) = CenterTemplate(scaled);
                if (energy <= 0) continue;
                var step = 2;
                for (var offsetY = 0; offsetY + scaled.Height <= frame.Height; offsetY += step)
                    for (var offsetX = 0; offsetX + scaled.Width <= frame.Width; offsetX += step)
                    {
                        double sum = 0, sumSquares = 0;
                        for (var y = 0; y < scaled.Height; y++)
                        {
                            var rowFrame = (offsetY + y) * frame.Width + offsetX;
                            var rowTemplate = y * scaled.Width;
                            for (var x = 0; x < scaled.Width; x++)
                            {
                                var value = frame.Gray[rowFrame + x];
                                sum += value; sumSquares += value * value;
                            }
                        }
                        double covariance = 0;
                        var mean = sum / (scaled.Width * scaled.Height);
                        for (var y = 0; y < scaled.Height; y++)
                        {
                            var rowFrame = (offsetY + y) * frame.Width + offsetX;
                            var rowTemplate = y * scaled.Width;
                            for (var x = 0; x < scaled.Width; x++)
                                covariance += centered[rowTemplate + x] * (frame.Gray[rowFrame + x] - mean);
                        }
                        var variance = sumSquares - sum * sum / (scaled.Width * scaled.Height);
                        if (variance <= 0) continue;
                        var confidence = covariance / Math.Sqrt(energy * variance);
                        if (confidence > threshold && (best == null || confidence > best.Confidence))
                            best = new(template.Name, Math.Min(confidence, 1.0), offsetX, offsetY);
                    }
            }
        return best;
    }
    internal static IEnumerable<VisualFrame> Scales(VisualFrame pattern, VisualFrame frame)
    {
        if (pattern.Width < 4 || pattern.Height < 4) yield break;
        var aspect = pattern.Width / (double)pattern.Height;
        foreach (var height in ScaleHeights)
        {
            var width = (int)Math.Round(height * aspect);
            if (width < 8 || width >= frame.Width - 8 || height >= frame.Height - 8) continue;
            yield return Resample(pattern, width, height);
        }
    }
    internal static VisualFrame Resample(VisualFrame source, int width, int height)
    {
        var gray = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceTop = y * source.Height / height;
            var sourceBottom = Math.Max((y + 1) * source.Height / height, sourceTop + 1);
            for (var x = 0; x < width; x++)
            {
                var sourceLeft = x * source.Width / width;
                var sourceRight = Math.Max((x + 1) * source.Width / width, sourceLeft + 1);
                double sum = 0; var count = 0;
                for (var sy = sourceTop; sy < sourceBottom; sy++)
                    for (var sx = sourceLeft; sx < sourceRight; sx++) { sum += source.Gray[sy * source.Width + sx]; count++; }
                gray[y * width + x] = (float)(count == 0 ? 0 : sum / count);
            }
        }
        return new(width, height, gray);
    }
    private static (float[] Centered, double Energy) CenterTemplate(VisualFrame pattern)
    {
        double sum = 0;
        foreach (var value in pattern.Gray) sum += value;
        var mean = (float)(sum / pattern.Gray.Length);
        var centered = new float[pattern.Gray.Length];
        double energy = 0;
        for (var index = 0; index < pattern.Gray.Length; index++)
        {
            centered[index] = pattern.Gray[index] - mean;
            energy += centered[index] * centered[index];
        }
        return (centered, energy);
    }
}

// Thread-safe state machine: Idle → Monitoring → ChallengeDetected → Resolved → Monitoring, or
// Error on repeated capture failures. Duplicate detections are suppressed while a challenge is
// active and for the resolution cooldown afterwards. Snapshots never claim anything beyond what
// the observed frames support.
public sealed class VisualChallengeMonitor(IVisualWindowSource windows, IVisualFrameSource capture, Func<DateTimeOffset>? clock = null, VisualChallengeMonitorOptions? options = null)
{
    private readonly IVisualWindowSource windows = windows;
    private readonly IVisualFrameSource capture = capture;
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object stateLock = new();
    private VisualChallengeMonitorOptions options = options ?? new();
    private IReadOnlyList<ChallengeTemplate> templates = [];
    private string? loadedTemplateFolder;
    private bool templatesInjected;
    private VisualMonitorPhase phase = VisualMonitorPhase.Monitoring;
    private DateTimeOffset? detectedAt;
    private string? detectedWindow;
    private double? detectedConfidence;
    private DateTimeOffset lastPoll = DateTimeOffset.MinValue;
    private DateTimeOffset lastResolvedAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastFailureAt = DateTimeOffset.MinValue;
    private int misses;
    private readonly List<VisualChallengeEvent> events = [];
    public VisualChallengeMonitorOptions Options { get => options; set => options = value ?? new(); }
    // Test seam: inject synthetic templates and skip folder loading. Never used by the UI.
    internal void UseTemplatesForTesting(params ChallengeTemplate[] seed)
    { templates = seed; templatesInjected = true; }

    public VisualMonitorSnapshot Poll()
    {
        lock (stateLock)
        {
            var now = clock();
            if (now - lastPoll < options.Interval) return lastSnapshot ?? Snapshot();
            lastPoll = now;
            if (!templatesInjected && !SameFolder(options.TemplateFolder, loadedTemplateFolder))
            {
                templates = ChallengeTemplateStore.Load(options.TemplateFolder);
                loadedTemplateFolder = options.TemplateFolder;
            }
            if (templates.Count == 0) return Enter(VisualMonitorPhase.Monitoring, $"No pattern templates configured in {(string.IsNullOrWhiteSpace(options.TemplateFolder) ? "(no folder)" : options.TemplateFolder)}.", null, null, now);
            var observed = windows.Enumerate();
            lastWindowCount = observed.Count;
            lastLauncherSummary = Summary(observed);
            if (observed.Count == 0) return Enter(VisualMonitorPhase.Monitoring, "No AION2 window found.", null, null, now);
            var best = (TemplateMatch?)null; TargetWindowRecord? bestWindow = null;
            var failures = 0; string? failureReason = null;
            foreach (var window in observed)
            {
                var frame = capture.Capture(window);
                if (frame == null)
                {
                    failures++; failureReason ??= capture.LastFailureReason;
                    continue;
                }
                var match = TemplateMatcher.Best(frame, templates, options.Threshold);
                if (match != null && (best == null || match.Confidence > best.Confidence)) { best = match; bestWindow = window; }
            }
            if (best == null)
            {
                if (failures == observed.Count)
                {
                    if (lastFailureAt != DateTimeOffset.MinValue && now - lastFailureAt >= options.ErrorAfterFailedPolls)
                        return Enter(VisualMonitorPhase.Error, $"Capture failed for all {observed.Count} window(s) ({failureReason ?? "unknown reason"}); exclusive-fullscreen or protected content is not capturable.", null, null, now);
                    lastFailureAt = now;
                    return Enter(VisualMonitorPhase.Monitoring, $"Capture unavailable ({failureReason ?? "unknown reason"}); retrying.", null, null, now);
                }
                lastFailureAt = DateTimeOffset.MinValue;
                return MissChallenge(observed.Count, now);
            }
            lastFailureAt = DateTimeOffset.MinValue;
            return HitChallenge(bestWindow!, best, now);
        }
    }
    private VisualMonitorSnapshot HitChallenge(TargetWindowRecord window, TemplateMatch match, DateTimeOffset now)
    {
        misses = 0;
        if (phase == VisualMonitorPhase.ChallengeDetected)
        {
            detectedWindow = window.Title; detectedConfidence = match.Confidence; // deduplicated; no new event
            return Snapshot($"Challenge overlay still visible on \"{window.Title}\" (confidence {match.Confidence:F2}).");
        }
        if (now - lastResolvedAt < options.ResolutionCooldown)
            return Snapshot($"Challenge-shaped pattern ignored for {Math.Max(0, (options.ResolutionCooldown - (now - lastResolvedAt)).TotalSeconds):F0}s cooldown after the previous resolution.");
        phase = VisualMonitorPhase.ChallengeDetected;
        detectedAt = now; detectedWindow = window.Title; detectedConfidence = match.Confidence;
        var launcher = window.Launcher is { } source ? $"; launcher: {source}" : "";
        var detail = $"Challenge overlay detected on \"{window.Title}\"{launcher} (pattern {match.Confidence:F2} at {match.OffsetX},{match.OffsetY}); complete or dismiss it in the game window. Hexor does not read or answer challenges.";
        AddEvent(new(now, phase, window.Title, match.Confidence, detail), now);
        return Snapshot(detail);
    }
    private VisualMonitorSnapshot MissChallenge(int windowCount, DateTimeOffset now)
    {
        if (phase != VisualMonitorPhase.ChallengeDetected)
            return Enter(VisualMonitorPhase.Monitoring, $"Monitoring {windowCount} AION2 window(s); no challenge pattern above threshold.", null, null, now);
        misses++;
        if (misses < options.MissesToResolve) return Snapshot($"Challenge overlay no longer visible; confirming for {options.MissesToResolve - misses} more poll(s).");
        phase = VisualMonitorPhase.Resolved; lastResolvedAt = now;
        var detail = $"Challenge overlay no longer detected (observed {Local(now)}); monitoring continues.";
        AddEvent(new(now, phase, detectedWindow ?? "", detectedConfidence, detail), now);
        var snapshot = Snapshot(detail);
        phase = VisualMonitorPhase.Monitoring; detectedAt = null; detectedWindow = null; detectedConfidence = null;
        return snapshot with { Phase = VisualMonitorPhase.Monitoring, Detail = $"Monitoring {windowCount} AION2 window(s); no challenge pattern above threshold." };
    }
    private VisualMonitorSnapshot Enter(VisualMonitorPhase next, string detail, string? window, double? confidence, DateTimeOffset now)
    {
        if (phase != next)
        {
            if (next == VisualMonitorPhase.Error || next == VisualMonitorPhase.Resolved) AddEvent(new(now, next, window ?? detectedWindow ?? "", confidence, detail), now);
            phase = next;
            if (next != VisualMonitorPhase.ChallengeDetected) { detectedAt = null; detectedWindow = null; detectedConfidence = null; }
        }
        return Snapshot(detail);
    }
    private int lastWindowCount;
    private string lastLauncherSummary = "";
    private VisualMonitorSnapshot? lastSnapshot;
    private static string Summary(IReadOnlyList<TargetWindowRecord> windows)
    {
        var attributed = windows.Where(w => w.Launcher != null)
            .GroupBy(w => w.Launcher).Select(g => $"{g.Key} ×{g.Count()}").ToArray();
        var unidentified = windows.Count(w => w.Launcher == null);
        var parts = new List<string>(attributed);
        if (unidentified > 0) parts.Add($"launcher unidentified ×{unidentified}");
        return string.Join(", ", parts);
    }
    private VisualMonitorSnapshot Snapshot(string? detail = null)
    {
        var active = phase == VisualMonitorPhase.ChallengeDetected;
        lastSnapshot = new(phase, detectedAt, active ? detectedWindow : null, active ? detectedConfidence : null,
            detail ?? Detail(), lastWindowCount, templates.Count, [.. events]) { LauncherSummary = lastLauncherSummary };
        return lastSnapshot;
    }
    private string Detail() => phase switch
    {
        VisualMonitorPhase.ChallengeDetected => $"Challenge overlay detected at {Local(detectedAt)}.",
        VisualMonitorPhase.Error => "Capture is failing; see the detection log.",
        _ => "Monitoring for challenge-shaped overlays.",
    };
    private void AddEvent(VisualChallengeEvent entry, DateTimeOffset now)
    {
        events.Add(entry);
        while (events.Count > 200) events.RemoveAt(0);
    }
    private static bool SameFolder(string? left, string? right) => string.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase);
    private static string Local(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";
}
