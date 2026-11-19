using System.Text.Json;
using Hexor.Runtime;
using System.IO;

internal static class CaptchaAutoSolverTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        Payloads(check);
        SolverFlow(check).GetAwaiter().GetResult();
        DelayAndSamples(check).GetAwaiter().GetResult();
        Disabled(check);
    }

    private static void Payloads(Action<bool, string> check)
    {
        var request = JsonSerializer.Deserialize<CaptchaGameRequest>(
            "{\"requestId\":\"req-7\",\"imageBase64\":\"aGk=\",\"mimeType\":\"image/png\"}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(request != null && request!.RequestId == "req-7" && request.ImageBase64 == "aGk=" &&
            request.MimeType == "image/png",
            "Captcha request payloads deserialize the challenge image reference");

        var outcome = JsonSerializer.Deserialize<CaptchaGameOutcome>(
            "{\"requestId\":\"req-7\",\"success\":true,\"message\":\"SUCCESS\"}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(outcome != null && outcome!.RequestId == "req-7" && outcome.Success == true,
            "Captcha outcome payloads deserialize the game's verdict");

        check(new CaptchaGameOutcome("req-9", null, null) is { } partial && partial.Success == null,
            "Outcome payloads without a verdict are preserved as unknown");
    }

    private static async Task SolverFlow(Action<bool, string> check)
    {
        using var manager = new GameIpcManager();
        (int pid, string command, object? args)? submitted = null;
        var solveCalls = 0;
        using var solver = new CaptchaAutoSolver(manager, (image, _) =>
        {
            solveCalls++;
            return Task.FromResult("4242");
        }, keyOverride: "test-key", 0, 0)
        {
            SubmitOverride = (pid, command, args) =>
            {
                submitted = (pid, command, args);
                return Task.CompletedTask;
            },
        };
        check(solver.Enabled && solver.Describe().Contains("Auto-resolve is active", StringComparison.Ordinal),
            "The auto-resolve reports itself active with a configured key");

        solver.OnChallenge(4242, "req-1", "aGVsbG8=", "image/png");
        // The solve and submit path is async void; give it a moment.
        for (var i = 0; i < 50 && submitted == null; i++) await Task.Delay(100);
        var json = submitted == null ? "" : JsonSerializer.Serialize(submitted.Value.args,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(submitted != null && submitted.Value.pid == 4242 &&
            submitted.Value.command == "SubmitCaptchaAnswer" &&
            json.Contains("\"requestId\":\"req-1\"", StringComparison.Ordinal) &&
            json.Contains("\"answer\":\"4242\"", StringComparison.Ordinal) &&
            json.Contains("\"forceLogout\":false", StringComparison.Ordinal),
            "Solved answers are submitted with the documented command shape");

        check(solver.Describe().Contains("waiting for the game's result", StringComparison.Ordinal),
            "The solver status reports waiting for the game's result after submitting");

        solver.OnChallenge(4242, "req-1", "aGVsbG8=", "image/png");
        for (var i = 0; i < 10; i++) await Task.Delay(100);
        check(solveCalls == 1, "The same challenge request is never solved twice");

        solver.OnOutcome(4242, "req-1", success: true, "SUCCESS confirmed by game");
        check(solver.Solved == 1 && solver.Waiting == 0 &&
            solver.Describe().Contains("accepted by the game", StringComparison.Ordinal),
            "The game's accepted verdict is counted and reported");

        solver.OnOutcome(4242, "req-2", success: false, "wrong answer");
        check(solver.Rejected == 1 && solver.Describe().Contains("rejected by the game: wrong answer", StringComparison.Ordinal),
            "Rejected verdicts surface the game's message");
        check(solver.History.Any(h => h.Text.Contains("accepted by the game", StringComparison.Ordinal)),
            "The solver keeps an activity history for the challenge log");

        // Solver failure still delivers a response so the game is not left waiting forever.
        using var failing = new CaptchaAutoSolver(manager, (_, _) => throw new InvalidOperationException("boom"), keyOverride: "k", 0, 0)
        {
            SubmitOverride = (_, _, _) => Task.CompletedTask,
        };
        failing.OnChallenge(1, "req-3", "aGVsbG8=", null);
        for (var i = 0; i < 50; i++) await Task.Delay(100);
        check(failing.Failed == 1 && failing.Describe().Contains("could not be solved: boom", StringComparison.Ordinal),
            "Solver failures are counted and described to the operator");
    }

    private static async Task DelayAndSamples(Action<bool, string> check)
    {
        using var manager = new GameIpcManager();
        var sampleDirectory = Path.Combine(Path.GetTempPath(), "CaptchaSamples-" + Guid.NewGuid().ToString("N"));
        CaptchaAutoSolver.SampleDirectoryResolver = () => sampleDirectory;
        try
        {
            DateTimeOffset? solvedAt = null, submittedAt = null;
            (int pid, string command, object? args)? submitted = null;
            using var solver = new CaptchaAutoSolver(manager, (_, _) =>
            {
                solvedAt = DateTimeOffset.UtcNow;
                return Task.FromResult("77");
            }, keyOverride: "k", 250, 450)
            {
                SubmitOverride = (pid, command, args) => { submittedAt = DateTimeOffset.UtcNow; submitted = (pid, command, args); return Task.CompletedTask; },
            };
            solver.OnChallenge(777, "req-d-1", Convert.ToBase64String([1, 2, 3, 4]), "image/png");
            for (var i = 0; i < 100 && submitted == null; i++) await Task.Delay(50);
            var elapsed = (submittedAt ?? solvedAt ?? DateTimeOffset.UtcNow) - (solvedAt ?? DateTimeOffset.UtcNow);
            check(submitted != null && elapsed.TotalMilliseconds >= 200,
                $"Answers wait a randomized delay before submission (measured {elapsed.TotalMilliseconds:0}ms)");
            var files = Directory.GetFiles(sampleDirectory);
            check(files.Any(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
                "Challenge images are archived as PNG samples");
            check(files.Any(f => f.Contains("reqd1", StringComparison.OrdinalIgnoreCase)),
                "Sample file names carry the challenge request id (sanitized)");
            solver.OnOutcome(777, "req-d-1", success: false, "wrong answer");
            for (var i = 0; i < 50; i++) await Task.Delay(50);
            var outcomeFiles = Directory.GetFiles(sampleDirectory).Where(f => f.EndsWith(".outcome.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
            check(outcomeFiles.Length == 1 && File.ReadAllText(outcomeFiles[0]).Contains("outcome=rejected"),
                "Rejected outcomes are recorded next to the sample image");
        }
        finally { CaptchaAutoSolver.SampleDirectoryResolver = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexor", "ControlCenter", "CaptchaSamples"); }
    }

    private static void Disabled(Action<bool, string> check)
    {
        using var manager = new GameIpcManager();
        using var solver = new CaptchaAutoSolver(manager, (_, _) => Task.FromResult("x"), keyOverride: "");
        check(!solver.Enabled && solver.Describe().Contains("not configured", StringComparison.Ordinal),
            "A missing solver key is reported as not configured");
        var submitCalled = false;
        solver.SubmitOverride = (_, _, _) => { submitCalled = true; return Task.CompletedTask; };
        solver.OnChallenge(1, "req-4", "aGVsbG8=", null);
        check(!submitCalled, "Challenges are ignored while the solver is not configured");
    }
}
