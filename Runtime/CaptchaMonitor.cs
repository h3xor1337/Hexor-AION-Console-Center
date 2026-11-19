using System.IO;
using System.Text;
namespace Hexor.Runtime;

// CAPTCHA awareness for Hexor: non-invasive detection of provider CAPTCHA activity from read-only signals,
// user notification, and optional pausing of Hexor's own provider-setting apply actions.
// Hexor never reads challenge images or answers, never touches the provider's private IPC/injection
// channels, and never solves, submits, or bypasses a CAPTCHA.
public enum CaptchaSignalKind { ChallengeRequested, AnswerDispatched, ResolvedConfirmed, ResolveFailed, RecognitionTimeout, RecognitionError }
public enum CaptchaPhase { Idle, ChallengeDetected, AnswerDispatched, Resolved, Failed, OutcomeUnknown }

public sealed record CaptchaSignal(CaptchaSignalKind Kind, DateTimeOffset ObservedAt, string Source);
public sealed record CaptchaRecord(CaptchaPhase Phase, DateTimeOffset ObservedAt);

public interface ICaptchaEventSource
{
    string Description { get; }
    IReadOnlyList<CaptchaSignal> Read(DateTimeOffset now);
}

public sealed record CaptchaSnapshot(CaptchaPhase Phase, DateTimeOffset? DetectedAt, DateTimeOffset? LastSignalAt, string Detail, string SourcesDescription)
{
    public bool ChallengeActive => Phase is CaptchaPhase.ChallengeDetected or CaptchaPhase.AnswerDispatched;
    public IReadOnlyList<CaptchaRecord> RecentRecords { get; init; } = [];
}

// Verified against the decompiled provider (LegionCommander_Reference/Analysis/AccountController.resolved.cs:2948–2958):
// when the runtime sends a CAPTCHA request, the provider persists the challenge image to
// %TEMP%\Aion2Captcha\captcha_ipc_<id>.png|jpg|bmp for its legacy automation flow. Hexor observes only
// file metadata (name and write time); image contents are never opened.
public sealed class CaptchaTempFileSource : ICaptchaEventSource
{
    public static readonly string DefaultDirectory = Path.Combine(Path.GetTempPath(), "Aion2Captcha");
    private static readonly string[] Extensions = [".png", ".jpg", ".bmp"];
    private readonly string directory;
    private readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Freshness { get; init; } = TimeSpan.FromSeconds(90);
    public CaptchaTempFileSource(string? directory = null) => this.directory = directory ?? DefaultDirectory;
    public string Description => $"Provider CAPTCHA artifact folder ({directory}) — " +
        (Directory.Exists(directory) ? "folder present" : "folder not created yet");
    public IReadOnlyList<CaptchaSignal> Read(DateTimeOffset now)
    {
        var signals = new List<CaptchaSignal>();
        string[] files;
        try { files = Directory.Exists(directory) ? Directory.GetFiles(directory, "captcha_ipc_*") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return signals; }
        foreach (var file in files)
        {
            if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            DateTimeOffset written;
            try { written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if ((now - written).Duration() > Freshness) continue; // stale artifact from an earlier session
            if (seen.TryGetValue(file, out var previous) && previous == written) continue;
            seen[file] = written;
            signals.Add(new(CaptchaSignalKind.ChallengeRequested, written, Description));
        }
        if (seen.Count > 256)
            foreach (var key in seen.Where(e => (now - e.Value).Duration() > Freshness).Select(e => e.Key).ToArray())
                seen.Remove(key);
        return signals;
    }
}

// Tail of the provider's own debug log (%APPDATA%\LegionCommander\Logs\debug_*.log). Lines are classified
// against log-message literals verified in the decompiled provider; raw log text is never displayed or
// stored by Hexor. The newest log is baselined on first sight so historical content produces no signals.
public sealed class CaptchaLogTailSource : ICaptchaEventSource
{
    private readonly string directory;
    private string? trackedFile;
    private long offset;
    private bool baseline;
    public CaptchaLogTailSource(string? directory = null) => this.directory = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LegionCommander", "Logs");
    public string Description => $"Provider debug log tail ({directory}) — " +
        (Directory.Exists(directory) ? "folder present" : "no provider Logs folder");
    public IReadOnlyList<CaptchaSignal> Read(DateTimeOffset now)
    {
        var signals = new List<CaptchaSignal>();
        string? latest;
        try
        {
            if (!Directory.Exists(directory)) { Reset(null); return signals; }
            latest = Directory.EnumerateFiles(directory, "debug_*.log").Order(StringComparer.OrdinalIgnoreCase).LastOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return signals; }
        if (latest == null) { Reset(null); return signals; }
        if (!string.Equals(latest, trackedFile, StringComparison.OrdinalIgnoreCase)) Reset(latest);
        try
        {
            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < offset) Reset(latest); // truncated or rewritten in place
            if (baseline) { offset = stream.Length; baseline = false; return signals; }
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[65536];
            byte[] pending = [];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                pending = Concat(pending, buffer.AsSpan(0, read));
                var start = 0;
                for (var index = 0; index < pending.Length; index++)
                    if (pending[index] == (byte)'\n')
                    {
                        Emit(pending, start, index - start, signals, now);
                        start = index + 1;
                    }
                if (start > 0) pending = pending[start..];
            }
            offset = stream.Position - pending.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* retried on the next poll */ }
        return signals;
    }
    private void Reset(string? file) { trackedFile = file; offset = 0; baseline = true; }
    private void Emit(byte[] buffer, int start, int length, List<CaptchaSignal> signals, DateTimeOffset now)
    {
        if (length == 0) return;
        var line = Encoding.UTF8.GetString(buffer, start, length).TrimEnd('\r');
        if (CaptchaLogClassifier.Classify(line) is { } kind) signals.Add(new(kind, now, Description));
    }
    private static byte[] Concat(byte[] left, ReadOnlySpan<byte> right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0); right.CopyTo(result.AsSpan(left.Length)); return result;
    }
}

// Substring literals are the provider's own log messages, verified in the decompiled
// AccountController (Analysis/AccountController.resolved.cs lines 2720–2996). Ordering is intentional:
// escalation and fallback variants are more specific than their base timeout/error texts.
public static class CaptchaLogClassifier
{
    private const string ConfirmedClosure = "DLL 已确认验证码窗口关闭，本次接码成功";

    private static readonly (string Fragment, CaptchaSignalKind Kind)[] Rules =
    [
        ("已通知 DLL 执行小退", CaptchaSignalKind.ResolveFailed),
        ("已执行遗留兜底", CaptchaSignalKind.AnswerDispatched),
        ("DLL 已确认本次接码失败", CaptchaSignalKind.ResolveFailed),
        ("已返回结果并发送至 DLL", CaptchaSignalKind.AnswerDispatched),
        ("自有识别服务识别失败", CaptchaSignalKind.RecognitionError),
        ("40 秒内未返回答案", CaptchaSignalKind.RecognitionTimeout),
        ("收到守护者验证图片", CaptchaSignalKind.ChallengeRequested),
        ("守护者验证图片已为遗留自动流程持久化", CaptchaSignalKind.ChallengeRequested),
    ];
    public static CaptchaSignalKind? Classify(string line)
    {
        var message = line.AsSpan().Trim();
        if (message.StartsWith("[", StringComparison.Ordinal) &&
            message.IndexOf("] [", StringComparison.Ordinal) is var accountEnd && accountEnd > 0)
        {
            var timestamp = message[(accountEnd + 3)..];
            if (timestamp.IndexOf(']') is var timeEnd && timeEnd > 0)
                message = timestamp[(timeEnd + 1)..].TrimStart();
        }
        if (message.SequenceEqual(ConfirmedClosure) || message.SequenceEqual(ConfirmedClosure + "。"))
            return CaptchaSignalKind.ResolvedConfirmed;

        foreach (var rule in Rules)
            if (line.Contains(rule.Fragment, StringComparison.Ordinal)) return rule.Kind;
        return null;
    }
}

public sealed class CaptchaMonitor(IEnumerable<ICaptchaEventSource> sources, Func<DateTimeOffset>? clock = null, TimeSpan? activityExpiry = null)
{
    private const int RecordLimit = 50;
    private readonly ICaptchaEventSource[] sources = sources.ToArray();
    private readonly Queue<CaptchaRecord> records = new();
    private IReadOnlyList<CaptchaRecord> recentRecords = [];
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan activityExpiry = activityExpiry ?? TimeSpan.FromMinutes(15);
    private CaptchaPhase phase = CaptchaPhase.Idle;
    private DateTimeOffset? detectedAt;
    private DateTimeOffset? lastSignalAt;
    private CaptchaSignalKind? lastSignalKind;
    public CaptchaMonitor() : this(new ICaptchaEventSource[] { new CaptchaTempFileSource(), new CaptchaLogTailSource() }) { }
    public CaptchaSnapshot Observe()
    {
        var now = clock();
        foreach (var source in sources)
        {
            IReadOnlyList<CaptchaSignal> signals;
            try { signals = source.Read(now); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { continue; }
            foreach (var signal in signals) Apply(signal);
        }
        Expire(now);
        return new(phase, detectedAt, lastSignalAt, Detail(), SourcesDescription()) { RecentRecords = recentRecords };
    }
    private void Apply(CaptchaSignal signal)
    {
        if (signal.ObservedAt < lastSignalAt) return;
        var previousPhase = phase;
        lastSignalAt = signal.ObservedAt;
        lastSignalKind = signal.Kind;
        switch (signal.Kind)
        {
            case CaptchaSignalKind.ChallengeRequested:
                if (phase is not (CaptchaPhase.ChallengeDetected or CaptchaPhase.AnswerDispatched))
                { phase = CaptchaPhase.ChallengeDetected; detectedAt = signal.ObservedAt; }
                break;
            case CaptchaSignalKind.AnswerDispatched:
                if (phase is not CaptchaPhase.AnswerDispatched)
                {
                    if (phase != CaptchaPhase.ChallengeDetected) detectedAt = signal.ObservedAt;
                    phase = CaptchaPhase.AnswerDispatched;
                }
                break;
            case CaptchaSignalKind.ResolvedConfirmed:
                phase = CaptchaPhase.Resolved;
                break;
            case CaptchaSignalKind.ResolveFailed:
                phase = CaptchaPhase.Failed;
                break;
            case CaptchaSignalKind.RecognitionTimeout:
            case CaptchaSignalKind.RecognitionError:
                phase = CaptchaPhase.OutcomeUnknown;
                break;
        }
        if (phase != previousPhase) Record(signal.ObservedAt);
    }
    private void Expire(DateTimeOffset now)
    {
        if (phase == CaptchaPhase.Idle) return;
        if (lastSignalAt is not { } last || now - last <= activityExpiry) return;
        if (phase is CaptchaPhase.ChallengeDetected or CaptchaPhase.AnswerDispatched or CaptchaPhase.Failed)
        {
            phase = CaptchaPhase.OutcomeUnknown;
            lastSignalKind = null;
            Record(now);
        }
    }
    private void Record(DateTimeOffset observedAt)
    {
        records.Enqueue(new(phase, observedAt));
        if (records.Count > RecordLimit) records.Dequeue();
        recentRecords = Array.AsReadOnly(records.Reverse().ToArray());
    }
    private string Detail() => phase switch
    {
        CaptchaPhase.ChallengeDetected => $"CAPTCHA detected at {Local(detectedAt)}; complete or supervise the challenge in the game window. Hexor does not solve or submit captchas.",
        CaptchaPhase.AnswerDispatched => $"CAPTCHA detected at {Local(detectedAt)}; provider automation dispatched an answer — the outcome is not confirmed by Hexor.",
        CaptchaPhase.Resolved => $"Provider reported game-confirmed CAPTCHA closure (observed {Local(lastSignalAt)}).",
        CaptchaPhase.Failed => $"Provider reported a failed CAPTCHA attempt (observed {Local(lastSignalAt)}). Check the game for current challenge status.",
        CaptchaPhase.OutcomeUnknown => lastSignalKind switch
        {
            CaptchaSignalKind.RecognitionTimeout => "The provider timed out while waiting for a CAPTCHA result. Check the game for current challenge status.",
            CaptchaSignalKind.RecognitionError => "The provider reported a CAPTCHA processing error. Check the game for current challenge status.",
            _ => $"The CAPTCHA signal expired without a confirmed closure. Last signal observed {Local(lastSignalAt)}.",
        },
        _ => "No CAPTCHA signal observed.",
    };
    private string SourcesDescription() => string.Join("; ", sources.Select(source => source.Description));
    private static string Local(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown";
}
