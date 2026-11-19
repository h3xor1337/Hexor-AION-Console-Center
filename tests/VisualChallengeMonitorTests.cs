using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Hexor;
using Hexor.Compatibility;
using Hexor.Runtime;

internal static class VisualChallengeMonitorTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Matcher(check);
        Downsample(check);
        TemplateStore(root, check);
        Monitor(check);
        PreferencesRoundTrip(root, check);
        ViewModelWiring(root, check);
    }

    private static VisualFrame Frame(int width, int height, Func<int, int, float> pixel) =>
        new(width, height, Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => pixel(x, y))).ToArray());

    private static readonly Func<int, int, float> TexturedBackground = (x, y) => 0.3f + (x % 7) * 0.02f + (y % 5) * 0.01f;
    private static readonly ChallengeTemplate Overlay = new("synthetic-overlay.png", Frame(32, 12, (x, y) => y == 0 || y == 11 || x == 0 || x == 31 ? 0.2f : 0.85f));
    private static readonly VisualFrame OverlayFrame = Frame(96, 64, (x, y) => x is >= 40 and < 72 && y is >= 20 and < 32
        ? (y == 20 || y == 31 || x == 40 || x == 71 ? 0.2f : 0.85f)
        : TexturedBackground(x, y));

    private static void Matcher(Action<bool, string> check)
    {
        var background = Frame(96, 64, TexturedBackground);
        check(TemplateMatcher.Scales(Overlay.Pattern, background).Any(scaled => scaled.Width == 32 && scaled.Height == 12),
            "Scale ladder includes the authored pattern size");
        var best = TemplateMatcher.Best(OverlayFrame, [Overlay], 0.80);
        check(best != null && best!.Confidence > 0.9, $"Embedded overlay pattern is matched with high confidence (got {best?.Confidence:F2})");
        check(best!.OffsetX is >= 38 and <= 44 && best.OffsetY is >= 16 and <= 22, $"Match reports the embedded overlay position (got {best.OffsetX},{best.OffsetY})");
        var none = TemplateMatcher.Best(background, [Overlay], 0.80);
        check(none == null, $"Background-only frame stays below the threshold (got {none?.Confidence:F2})");
        var flat = TemplateMatcher.Best(Frame(96, 64, (_, _) => 0.5f), [Overlay], 0.10);
        check(flat == null, "Uniform frames produce no match instead of a bogus high score");
        var doubleSize = new ChallengeTemplate("double.png", TemplateMatcher.Resample(Overlay.Pattern, 64, 24));
        check(TemplateMatcher.Best(OverlayFrame, [doubleSize], 0.80) != null,
            "A pattern cropped at a larger scale still matches the same overlay");
    }

    private static void Downsample(Action<bool, string> check)
    {
        var pixels = new int[64 * 32];
        for (var index = 0; index < pixels.Length; index++) pixels[index] = unchecked((int)0xFFFFFFFF); // white BGRA
        var frame = PrintWindowFrameSource.Downsample(pixels, 64, 32);
        check(frame.Width == 64 && frame.Height == 32, "Downsampling keeps a small frame at its working scale");
        check(frame.Gray.All(value => value > 0.99f), "White pixels convert to full grayscale");
        var wide = PrintWindowFrameSource.Downsample(Enumerable.Range(0, 400 * 300).Select(_ => 0).ToArray(), 400, 300);
        check(wide.Width <= 192 && wide.Height <= 240, "Large frames downsample to the working scale");
    }

    private static void TemplateStore(string root, Action<bool, string> check)
    {
        check(ChallengeTemplateStore.Load(null).Count == 0, "Missing template folder yields no templates without errors");
        var folder = Path.Combine(root, "ChallengeTemplates");
        Directory.CreateDirectory(folder);
        check(ChallengeTemplateStore.Load(folder).Count == 0, "Empty template folder yields no templates");
        var png = Path.Combine(folder, "synthetic-overlay.png");
        using (var stream = File.Create(png))
        {
            var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(32, 12, 96, 96, System.Windows.Media.PixelFormats.Gray8, null);
            var row = new byte[32];
            for (var y = 0; y < 12; y++)
            {
                for (var x = 0; x < 32; x++) row[x] = (byte)((y == 0 || y == 11 ? 0.2f : 0.85f) * 255);
                bitmap.WritePixels(new System.Windows.Int32Rect(0, y, 32, 1), row, 32, 0);
            }
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
        var loaded = ChallengeTemplateStore.Load(folder);
        check(loaded.Count == 1 && loaded[0].Name == "synthetic-overlay.png", "PNG template files are loaded as grayscale patterns");
        var matcher = TemplateMatcher.Best(OverlayFrame, loaded, 0.80);
        check(matcher != null && matcher!.Confidence > 0.8, $"A template loaded from disk matches the synthetic overlay (got {matcher?.Confidence:F2})");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "unrelated");
        check(ChallengeTemplateStore.Load(folder).Count == 1, "Non-image files are ignored");
    }

    private sealed class FakeWindowSource(params TargetWindowRecord[] windows) : IVisualWindowSource
    {
        public List<TargetWindowRecord> Current = [.. windows];
        public IReadOnlyList<TargetWindowRecord> Enumerate() => Current;
    }

    private sealed class FakeFrameSource(Func<TargetWindowRecord, VisualFrame?> capture) : IVisualFrameSource
    {
        public string? LastFailureReason { get; private set; }
        public Func<TargetWindowRecord, VisualFrame?> CaptureFunc { get; set; } = capture;
        public VisualFrame? Capture(TargetWindowRecord window)
        {
            var frame = CaptureFunc(window);
            if (frame == null) LastFailureReason = "synthetic-capture-unavailable";
            return frame;
        }
    }

    private static void Monitor(Action<bool, string> check)
    {
        var window = new TargetWindowRecord(new IntPtr(1234), "AION2", 4321);
        var windows = new FakeWindowSource(window);
        var frames = new FakeFrameSource(_ => OverlayFrame);
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = start;
        var monitor = new VisualChallengeMonitor(windows, frames, () => now, new VisualChallengeMonitorOptions { Interval = TimeSpan.Zero, TemplateFolder = null });
        monitor.UseTemplatesForTesting(Overlay);
        var snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.ChallengeDetected && snapshot.ChallengeActive, "A frame matching the overlay pattern activates detection");
        check(snapshot.WindowTitle == "AION2" && snapshot.Confidence > 0.8, $"Detection reports the observed window and confidence (got {snapshot.Confidence:F2})");
        var events = snapshot.RecentEvents.Count;
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.ChallengeDetected && snapshot.RecentEvents.Count == events, "Repeated matches while active are deduplicated without new events");
        frames.CaptureFunc = _ => Frame(96, 64, TexturedBackground);
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.ChallengeDetected, "One missing frame keeps the detection active pending confirmation");
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.Monitoring && snapshot.RecentEvents[^1].Phase == VisualMonitorPhase.Resolved,
            "Consecutive missing frames resolve the detection and log the resolution");
        windows.Current = [];
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.Monitoring && snapshot.WindowCount == 0 && snapshot.Detail.Contains("No AION2 window", StringComparison.Ordinal),
            "Window disappearance reports a monitored state with zero windows");
        windows.Current = [window];
        frames.CaptureFunc = _ => OverlayFrame;
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.Monitoring && snapshot.RecentEvents[^1].Phase == VisualMonitorPhase.Resolved,
            "A reappearing pattern inside the resolution cooldown is suppressed to prevent duplicate detections");
        now = start.AddSeconds(11);
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.ChallengeDetected, "After the cooldown the pattern is detected again");
        frames.CaptureFunc = _ => null;
        now = start.AddSeconds(40);
        snapshot = monitor.Poll();
        check(snapshot.Phase is VisualMonitorPhase.ChallengeDetected or VisualMonitorPhase.Monitoring,
            "Capture failure while a challenge is active does not fabricate a resolution");
        now = start.AddSeconds(120);
        snapshot = monitor.Poll();
        check(snapshot.Phase == VisualMonitorPhase.Error && snapshot.Detail.Contains("Capture failed", StringComparison.Ordinal),
            "Sustained capture failure surfaces the error phase with the failure reason");
        windows.Current = [];
        frames.CaptureFunc = _ => OverlayFrame;
        now = start.AddSeconds(200);
        check(monitor.Poll().Phase == VisualMonitorPhase.Monitoring, "Recovery after the error phase resumes monitoring");
        var rate = new VisualChallengeMonitor(windows, frames, () => start, new VisualChallengeMonitorOptions { Interval = TimeSpan.FromSeconds(30), TemplateFolder = null });
        var first = rate.Poll();
        check(first.Phase == VisualMonitorPhase.Monitoring, "First poll initializes the rate gate with a monitored state");
        var second = rate.Poll();
        check(ReferenceEquals(first, second), "Polls inside the configured interval are rate-gated and return the unchanged snapshot");
        PurpleProvenance(check);
        // Performance probe: a full pipeline poll (capture + match over the whole scale ladder) is timed
        // against the matcher budget so regressions surface in the regression gate.
        windows.Current = [window];
        frames.CaptureFunc = _ => OverlayFrame;
        var probe = Stopwatch.StartNew();
        const int iterations = 50;
        for (var iteration = 0; iteration < iterations; iteration++) monitor.Poll();
        probe.Stop();
        var average = probe.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"PASS visual poll average {average:F2} ms over {iterations} iterations");
        check(average < 50, $"A full monitor poll stays inside the 50 ms budget (average {average:F2} ms)");
    }

    private static void PurpleProvenance(Action<bool, string> check)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = start;
        var purple = new TargetWindowRecord(new IntPtr(11), "AION2", 111, Hexor.Runtime.LauncherProvenance.Purple);
        var unidentified = new TargetWindowRecord(new IntPtr(22), "AION2", 222);
        var windows = new FakeWindowSource(purple, unidentified);
        var frames = new FakeFrameSource(_ => OverlayFrame);
        var monitor = new VisualChallengeMonitor(windows, frames, () => now, new VisualChallengeMonitorOptions { Interval = TimeSpan.Zero, TemplateFolder = null });
        monitor.UseTemplatesForTesting(Overlay);
        var snapshot = monitor.Poll();
        check(snapshot.LauncherSummary.Contains("Purple launcher ×1", StringComparison.Ordinal) && snapshot.LauncherSummary.Contains("launcher unidentified ×1", StringComparison.Ordinal),
            "Window provenance distinguishes Purple-launched clients from unidentified ones: " + snapshot.LauncherSummary);
        check(snapshot.Detail.Contains("launcher: Purple launcher", StringComparison.Ordinal),
            "The detection detail names the Purple launcher when the challenged window was Purple-launched");
        check(snapshot.WindowCount == 2, "Multiple game windows are still enumerated together");
    }

    private static void PreferencesRoundTrip(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "visual-preferences.json");
        var json = new JsonConfigurationService(path, new SettingsBackupService());
        var preferences = json.Load();
        check(!preferences.VisualDetectionEnabled, "Visual detection defaults to disabled");
        check(preferences.VisualDetectionSound && preferences.VisualDetectionSensitivity == 50 && preferences.VisualDetectionIntervalSeconds == 4,
            "Sound, sensitivity, and interval defaults match the documented defaults");
        preferences.VisualDetectionEnabled = true;
        preferences.VisualDetectionSound = false;
        preferences.VisualDetectionSensitivity = 75;
        preferences.VisualDetectionIntervalSeconds = 9;
        preferences.VisualDetectionTemplateFolder = Path.Combine(root, "patterns");
        json.Save(preferences);
        var reloaded = new JsonConfigurationService(path, new SettingsBackupService()).Load();
        check(reloaded.VisualDetectionEnabled && !reloaded.VisualDetectionSound && reloaded.VisualDetectionSensitivity == 75
            && reloaded.VisualDetectionIntervalSeconds == 9 && reloaded.VisualDetectionTemplateFolder == Path.Combine(root, "patterns"),
            "Visual monitor preference toggles round-trip through Hexor preferences");
    }

    private static void ViewModelWiring(string root, Action<bool, string> check)
    {
        var dataRoot = Path.Combine(root, "verified");
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "db"));
        JsonObject Envelope(WorkflowPlan plan) => new() { ["FormatVersion"] = 1, ["Values"] = new JsonObject(), ["Workflow"] = plan.ToJson(), ["WorkflowDefaultVersion"] = 2 };
        File.WriteAllText(Path.Combine(dataRoot, "config", "settings.json"), Envelope(WorkflowPlan.Default()).ToJsonString());
        File.WriteAllText(Path.Combine(dataRoot, "config", "machine.json"), Envelope(new()).ToJsonString());
        File.WriteAllText(Path.Combine(dataRoot, "db", "accounts.db"), "availability-only fixture");
        var json = new JsonConfigurationService(Path.Combine(root, "visual-gating-preferences.json"), new SettingsBackupService());
        var registryRoot = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N");
        var window = new TargetWindowRecord(new IntPtr(4321), "AION2", 1234);
        var windows = new FakeWindowSource(window);
        var frames = new FakeFrameSource(_ => OverlayFrame);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = start;
        var monitor = new VisualChallengeMonitor(windows, frames, () => now, new VisualChallengeMonitorOptions { Interval = TimeSpan.Zero, TemplateFolder = null });
        monitor.UseTemplatesForTesting(Overlay);
        try
        {
            using var vm = new MainViewModel(json, new RegistrySettingsService(registryRoot), new LegionConfigurationService(dataRoot), visualMonitor: monitor);
            check(!vm.VisualDetectionEnabled && vm.VisualMonitorStatusText.Length > 0, "Visual monitor status is available before any poll");
            vm.PollVisualMonitorAsync().GetAwaiter().GetResult();
            check(vm.VisualMonitorStatusText.Length > 0, "Disabled detection leaves the poll as a no-op without pausing anything");
            vm.VisualDetectionEnabled = true;
            now = start.AddSeconds(5);
            vm.PollVisualMonitorAsync().GetAwaiter().GetResult();
            check(vm.VisualMonitorStatusText.StartsWith("Visual challenge detected", StringComparison.Ordinal),
                "An enabled monitor surfaces the detection through the status tile: " + vm.VisualMonitorStatusText);
            check(vm.VisualMonitorDetail.Contains("never generates or submits answers", StringComparison.Ordinal),
                "Visual monitor detail documents the detection-only boundary");
            check(vm.VisualMonitorDetail.Contains("Launcher provenance", StringComparison.Ordinal),
                "Visual monitor detail reports the Purple-aware launcher provenance line");
            var settingsText = File.ReadAllText(Path.Combine(dataRoot, "config", "settings.json"));
            check(settingsText.Contains("Workflow", StringComparison.Ordinal), "Visual polling leaves the customer configuration documents intact");
            vm.VisualDetectionEnabled = false;
            check(!json.Load().VisualDetectionEnabled, "Detection preference persists when toggled off through the view model");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false); }
    }
}
