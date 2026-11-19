using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Hexor;
using Hexor.Compatibility;
using Hexor.Runtime;

internal static class CaptchaMonitorTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Classifier(check);
        TempFileSource(root, check);
        LogTail(root, check);
        Monitor(check);
        Records(check);
        PreferencesRoundTrip(root, check);
        ViewModelGating(root, check);
    }

    private static void Classifier(Action<bool, string> check)
    {
        check(CaptchaLogClassifier.Classify("[Account1] [12:00:01] 收到守护者验证图片，正在请求 Aion 自有识别服务。") == CaptchaSignalKind.ChallengeRequested,
            "Challenge-received log line is classified as a challenge request");
        check(CaptchaLogClassifier.Classify("[Account1] [12:00:02] 守护者验证图片已为遗留自动流程持久化；日志不记录本地路径。") == CaptchaSignalKind.ChallengeRequested,
            "Challenge-persisted log line is classified as a challenge request");
        check(CaptchaLogClassifier.Classify("[Account1] [12:00:40] Aion 自有识别服务已返回结果并发送至 DLL；日志不记录答案内容。") == CaptchaSignalKind.AnswerDispatched,
            "Answer-dispatched log line is classified as an answer dispatch");
        check(CaptchaLogClassifier.Classify("[Account1] [12:00:41] DLL 已确认验证码窗口关闭，本次接码成功。") == CaptchaSignalKind.ResolvedConfirmed,
            "Provider-confirmed success is classified as resolved");
        check(CaptchaLogClassifier.Classify("[Account1] [12:00:41] copied message: DLL 已确认验证码窗口关闭，本次接码成功。") == null,
            "Quoting the game closure message does not confirm a clearance");
        check(CaptchaLogClassifier.Classify("[Account1] [12:01:00] DLL 已确认本次接码失败: timeout") == CaptchaSignalKind.ResolveFailed,
            "Provider-confirmed failure is classified as failed");
        check(CaptchaLogClassifier.Classify("[Account1] [12:02:00] Aion 自有识别服务在 40 秒内未返回答案；连续错误已达 9 次，超过允许的 8 次，已通知 DLL 执行小退。") == CaptchaSignalKind.ResolveFailed,
            "Escalation log line is classified as failed");
        check(CaptchaLogClassifier.Classify("[Account1] [12:02:01] Aion 自有识别服务在 40 秒内未返回答案；连续错误 3/8，已执行遗留兜底；日志不记录答案内容。") == CaptchaSignalKind.AnswerDispatched,
            "Fallback-answer log line is classified as an answer dispatch");
        check(CaptchaLogClassifier.Classify("[Account1] [12:03:00] Aion 自有识别服务识别失败: unavailable; 连续错误 2/8，已执行遗留兜底；日志不记录答案内容。") == CaptchaSignalKind.AnswerDispatched,
            "Recognition-failure fallback line is classified as an answer dispatch");
        check(CaptchaLogClassifier.Classify("[SteamAccountWindow] [04:03:39] 登录诊断[baa3790a] 阶段 3/7：正在联系登录服务器并验证启动凭据，第 1/1 次尝试。（累计 75.9 秒）") == null,
            "Login-diagnostic verification lines are not classified as CAPTCHA events");
        check(CaptchaLogClassifier.Classify("[Accounts] 使用数据库：D:\\db\\accounts.db，记录数=0") == null,
            "Ordinary provider log lines produce no CAPTCHA signal");
    }

    private static void TempFileSource(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "Aion2Captcha");
        var source = new CaptchaTempFileSource(directory) { Freshness = TimeSpan.FromSeconds(60) };
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        check(source.Read(now).Count == 0, "Missing provider CAPTCHA folder produces no signals");
        Directory.CreateDirectory(directory);
        var fresh = Path.Combine(directory, "captcha_ipc_req-1.png");
        File.WriteAllBytes(fresh, [1, 2, 3]);
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime);
        var signals = source.Read(now);
        check(signals.Count == 1 && signals[0].Kind == CaptchaSignalKind.ChallengeRequested, "Fresh CAPTCHA artifact emits one challenge signal");
        check(source.Read(now).Count == 0, "The same artifact is deduplicated across polls");
        var stale = Path.Combine(directory, "captcha_ipc_req-2.jpg");
        File.WriteAllBytes(stale, [1]);
        File.SetLastWriteTimeUtc(stale, now.UtcDateTime.AddMinutes(-5));
        check(source.Read(now).Count == 0, "Stale artifact outside the freshness window is ignored");
        File.SetLastWriteTimeUtc(fresh, now.UtcDateTime.AddSeconds(-10));
        check(source.Read(now).Count == 1, "Changed artifact timestamp is treated as a new signal");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "unrelated");
        check(source.Read(now).Count == 0, "Unrelated files in the artifact folder are ignored");
        check(!CaptchaTempFileSource.DefaultDirectory.Contains("Aion2Captcha" + Path.DirectorySeparatorChar + "captcha", StringComparison.Ordinal),
            "Default artifact folder resolves to %TEMP%\\Aion2Captcha");
    }

    private static void LogTail(string root, Action<bool, string> check)
    {
        var logDirectory = Path.Combine(root, "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "debug_20260101_000000.log");
        File.WriteAllText(logPath, "[Main] start\n[Account1] [12:00:01] 收到守护者验证图片，正在请求 Aion 自有识别服务。\n");
        var source = new CaptchaLogTailSource(logDirectory);
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 2, TimeSpan.Zero);
        check(source.Read(now).Count == 0, "Initial attach baselines existing log content without emitting signals");
        File.AppendAllText(logPath, "[Account1] [12:00:40] Aion 自有识别服务已返回结果并发送至 DLL；日志不记录答案内容。\n");
        var dispatched = source.Read(now.AddSeconds(1));
        check(dispatched.Count == 1 && dispatched[0].Kind == CaptchaSignalKind.AnswerDispatched, "Appended CAPTCHA lines are classified incrementally");
        File.AppendAllText(logPath, "[Account1] [12:00:41] DLL 已确认验证码窗口关闭，本次接");
        check(source.Read(now.AddSeconds(2)).Count == 0, "Partial trailing line without a newline is not emitted early");
        File.AppendAllText(logPath, "码成功。\n");
        var resolved = source.Read(now.AddSeconds(3));
        check(resolved.Count == 1 && resolved[0].Kind == CaptchaSignalKind.ResolvedConfirmed, "Completed partial line is emitted exactly once");
        File.WriteAllText(Path.Combine(logDirectory, "debug_20260102_000000.log"), "[Main] start\n");
        check(source.Read(now.AddSeconds(4)).Count == 0, "Rotated log file is baselined without replaying old content");
        File.AppendAllText(Path.Combine(logDirectory, "debug_20260102_000000.log"), "[Account1] [12:01:00] DLL 已确认本次接码失败: x\n");
        var failed = source.Read(now.AddSeconds(5));
        check(failed.Count == 1 && failed[0].Kind == CaptchaSignalKind.ResolveFailed, "Rotated log file appends are classified");
        check(source.Read(now.AddSeconds(6)).Count == 0, "Repeated log-tail reads without appends stay silent");
    }

    private sealed class FakeSource : ICaptchaEventSource
    {
        private readonly Queue<CaptchaSignal> queue = new();
        public string Description => "synthetic test source";
        public void Enqueue(CaptchaSignal signal) => queue.Enqueue(signal);
        public IReadOnlyList<CaptchaSignal> Read(DateTimeOffset now) { var list = new List<CaptchaSignal>(); while (queue.Count > 0) list.Add(queue.Dequeue()); return list; }
    }

    private static void Monitor(Action<bool, string> check)
    {
        var source = new FakeSource();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = start;
        var monitor = new CaptchaMonitor([source], () => now, TimeSpan.FromMinutes(15));
        var snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.Idle && !snapshot.ChallengeActive, "CAPTCHA monitor starts idle with no active challenge");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.ChallengeRequested, start, "test"));
        snapshot = monitor.Observe();
        check(snapshot.ChallengeActive && snapshot.Phase == CaptchaPhase.ChallengeDetected && snapshot.DetectedAt == start,
            "Challenge signal activates detection with the observed time");
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.ChallengeDetected, "Repeated observation without new signals keeps the deduplicated phase");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.AnswerDispatched, start.AddSeconds(30), "test"));
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.AnswerDispatched && snapshot.ChallengeActive, "Answer dispatch keeps the challenge active but unconfirmed");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.ResolvedConfirmed, start.AddSeconds(40), "test"));
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.Resolved && !snapshot.ChallengeActive, "Resolved confirmation clears the active challenge");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.ResolveFailed, start.AddMinutes(2), "test"));
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.Failed && !snapshot.ChallengeActive, "Failure signal after resolution is tracked without reactivating the challenge");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.ChallengeRequested, start.AddMinutes(3), "test"));
        snapshot = monitor.Observe();
        check(snapshot.ChallengeActive && snapshot.Phase == CaptchaPhase.ChallengeDetected, "A later challenge signal reactivates detection");
        now = start.AddMinutes(25);
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.OutcomeUnknown && !snapshot.ChallengeActive,
            "An expired challenge becomes unknown without claiming clearance");
        source.Enqueue(new CaptchaSignal(CaptchaSignalKind.RecognitionTimeout, now, "test"));
        snapshot = monitor.Observe();
        check(snapshot.Phase == CaptchaPhase.OutcomeUnknown, "Recognition timeout leaves the challenge outcome unknown");
    }

    private static void Records(Action<bool, string> check)
    {
        var source = new FakeSource();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monitor = new CaptchaMonitor([source], () => now, TimeSpan.FromMinutes(1));
        source.Enqueue(new(CaptchaSignalKind.ChallengeRequested, now, "fixture"));
        source.Enqueue(new(CaptchaSignalKind.AnswerDispatched, now, "fixture"));
        var active = monitor.Observe();
        check(active.RecentRecords.Select(record => record.Phase).SequenceEqual(
            new[] { CaptchaPhase.AnswerDispatched, CaptchaPhase.ChallengeDetected }),
            "Records retain multiple transitions observed in one poll, newest first");
        source.Enqueue(new(CaptchaSignalKind.AnswerDispatched, now, "fixture"));
        check(ReferenceEquals(active.RecentRecords, monitor.Observe().RecentRecords),
            "A repeated phase does not add a duplicate record");
        now = now.AddMinutes(2);
        var expired = monitor.Observe();
        check(expired.RecentRecords[0] == new CaptchaRecord(CaptchaPhase.OutcomeUnknown, now) && active.RecentRecords.Count == 2,
            "Expiration is recorded at observation time and earlier snapshots retain their contents");
        for (var index = 0; index < 30; index++)
        {
            source.Enqueue(new(CaptchaSignalKind.ChallengeRequested, now, "fixture"));
            source.Enqueue(new(CaptchaSignalKind.ResolvedConfirmed, now, "fixture"));
        }
        var closed = monitor.Observe();
        check(closed.RecentRecords.Count == 50 && closed.RecentRecords[0].Phase == CaptchaPhase.Resolved,
            "CAPTCHA records retain the latest 50 status changes");
    }

    internal static async Task RunWpfAsync(MainWindow window, string logPath, Action<bool, string> check)
    {
        window.ShowPage(0);
        var tile = (TextBlock)LogicalTreeHelper.FindLogicalNode(window, "CaptchaStatusTile");
        check(tile.Text == "No challenge yet", "CAPTCHA tile displays the empty state");

        File.AppendAllText(logPath, "[FixtureAccount] [12:00:01] 收到守护者验证图片。\n");
        await window.Model.PollCaptchaAsync();
        check(tile.Text.StartsWith("CAPTCHA detected"),
            "A challenge log fixture updates the rendered status tile");

        File.AppendAllText(logPath, "[FixtureAccount] [12:00:02] Aion 自有识别服务已返回结果并发送至 DLL。\n");
        await window.Model.PollCaptchaAsync();
        check(tile.Text == "CAPTCHA active · awaiting result",
            "A dispatch fixture leaves the rendered status active");

        File.AppendAllText(logPath, "[FixtureAccount] [12:00:03] 40 秒内未返回答案。\n");
        await window.Model.PollCaptchaAsync();
        check(tile.Text == "CAPTCHA outcome unknown",
            "A timeout fixture produces an unknown outcome in the rendered UI");

        File.AppendAllText(logPath, "[FixtureAccount] [12:00:04] DLL 已确认验证码窗口关闭，本次接码成功。\n");
        await window.Model.PollCaptchaAsync();
        check(tile.Text.StartsWith("CAPTCHA clearance reported") && window.Model.CaptchaRecordLines.Count == 4
            && window.Model.CaptchaRecordLines[0].EndsWith("CAPTCHA clearance reported")
            && window.Model.CaptchaRecordLines[1].EndsWith("CAPTCHA outcome unknown")
            && window.Model.CaptchaRecordLines.All(row => !row.Contains("FixtureAccount")),
            "The game-closure fixture updates both bindings without displaying the account label");
    }


    private static void PreferencesRoundTrip(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "captcha-preferences.json");
        var json = new JsonConfigurationService(path, new SettingsBackupService());
        var preferences = json.Load();
        check(preferences.CaptchaNotifications, "CAPTCHA notifications default to enabled");
        preferences.CaptchaNotifications = false;
        json.Save(preferences);
        var reloaded = new JsonConfigurationService(path, new SettingsBackupService()).Load();
        check(!reloaded.CaptchaNotifications,
            "CAPTCHA preference toggles round-trip through Hexor preferences");
    }

    private static void ViewModelGating(string root, Action<bool, string> check)
    {
        var dataRoot = Path.Combine(root, "verified");
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "db"));
        JsonObject Envelope(WorkflowPlan plan) => new() { ["FormatVersion"] = 1, ["Values"] = new JsonObject(), ["Workflow"] = plan.ToJson(), ["WorkflowDefaultVersion"] = 2 };
        File.WriteAllText(Path.Combine(dataRoot, "config", "settings.json"), Envelope(WorkflowPlan.Default()).ToJsonString());
        File.WriteAllText(Path.Combine(dataRoot, "config", "machine.json"), Envelope(new()).ToJsonString());
        File.WriteAllText(Path.Combine(dataRoot, "db", "accounts.db"), "availability-only fixture");
        var json = new JsonConfigurationService(Path.Combine(root, "captcha-gating-preferences.json"), new SettingsBackupService());
        var registryRoot = "Software\\HexorTests\\" + Guid.NewGuid().ToString("N");
        var source = new FakeSource();
        var service = new LegionConfigurationService(dataRoot);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = start;
        try
        {
            using var vm = new MainViewModel(json, new RegistrySettingsService(registryRoot), service,
                captchaMonitor: new CaptchaMonitor([source], () => now, TimeSpan.FromMinutes(15)));
            check(!vm.CaptchaChallengeActive && vm.CaptchaStatusText.Length > 0, "CAPTCHA status text is available before any poll");
            vm.MaxAccounts = "2"; vm.WorkMin = "60"; vm.WorkMax = "120"; vm.RestMin = "5"; vm.RestMax = "10";
            vm.SaveSchedule();
            check(ScheduleSettings.Read(service.Read()).Accounts == 2, "Schedule apply persists while the CAPTCHA monitor is idle (status: " + vm.Status + ")");
            source.Enqueue(new CaptchaSignal(CaptchaSignalKind.ChallengeRequested, start, "test"));
            vm.PollCaptchaAsync().GetAwaiter().GetResult();
            check(vm.CaptchaChallengeActive && vm.CaptchaStatusText.StartsWith("CAPTCHA detected", StringComparison.Ordinal),
                "An active challenge is surfaced through the status tile");
            vm.MaxAccounts = "3";
            vm.SaveSchedule();
            check(ScheduleSettings.Read(service.Read()).Accounts == 3,
                "Applies are never paused by challenge signals (status: " + vm.Status + ")");
            vm.SaveDaily();
            check(!vm.Status.StartsWith("CAPTCHA detected", StringComparison.Ordinal),
                "Workflow apply is not paused while a challenge signal is active");
            now = start.AddMinutes(16);
            vm.PollCaptchaAsync().GetAwaiter().GetResult();
            check(!vm.CaptchaChallengeActive, "Challenge activity expiry clears the active flag");
            vm.CaptchaNotifications = false;
            check(vm.CaptchaStatusText == "CAPTCHA notifications off", "Notification preference controls the status tile without affecting diagnostics");
            check(vm.CaptchaDiagnostics.Contains("never reads challenge images", StringComparison.Ordinal),
                "Diagnostics always document the non-invasive CAPTCHA boundary");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false); }
    }
}
