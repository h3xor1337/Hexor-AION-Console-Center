using System.Net.Http;
using System.Text.Json;
using Hexor.Runtime;
using System.IO;

internal static class CaptchaAutoSolverTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var unitSampleDirectory = Path.Combine(root, "captcha-solver-samples");
        CaptchaAutoSolver.SampleDirectoryResolver = () => unitSampleDirectory;
        try
        {
            Payloads(check);
            SolverFlow(check).GetAwaiter().GetResult();
            DelayAndSamples(check).GetAwaiter().GetResult();
            Disabled(check);
            VendorRecognition(check).GetAwaiter().GetResult();
        }
        finally { CaptchaAutoSolver.SampleDirectoryResolver = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hexor", "ControlCenter", "CaptchaSamples"); }
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
        using var solver = new CaptchaAutoSolver(manager, (_, _) => Task.FromResult("x"), keyOverride: "", vendorTokenResolver: () => null);
        check(!solver.Enabled && solver.Describe().Contains("not configured", StringComparison.Ordinal),
            "A missing solver key is reported as not configured");
        var submitCalled = false;
        solver.SubmitOverride = (_, _, _) => { submitCalled = true; return Task.CompletedTask; };
        solver.OnChallenge(1, "req-4", "aGVsbG8=", null);
        check(!submitCalled, "Challenges are ignored while the solver is not configured");
    }

    // The updater's shared recognition service is the primary solver; the embedded key is the
    // failover. These checks pin the wire contract (verified against the updater's own client),
    // the failover chain and the not-signed-in behavior.
    private static async Task VendorRecognition(Action<bool, string> check)
    {
        // Registry token: verified live when the authorization state exists on this machine.
        try
        {
            using var pointer = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Reliability");
            var stateExists = pointer?.GetValue("MachineId") is string;
            var token = AionRecognitionClient.ReadVendorAuthorizationToken();
            if (stateExists) check(token is { Length: > 0 }, "The updater login token is read from its registry location");
            else check(token is null, "Without an updater login the token reader returns nothing");
        }
        catch
        {
            check(AionRecognitionClient.ReadVendorAuthorizationToken() is null, "An unreadable token location yields no token");
        }

        // Fake service: create -> poll(processing) -> poll(ready) -> answer.
        var requests = new List<string>();
        var pollCount = 0;
        var handler = new FakeHandler((method, url, body) =>
        {
            requests.Add(url);
            if (url.Contains("55bff2a9", StringComparison.Ordinal))
                return (200, "{\"errorId\":0,\"success\":true,\"taskId\":\"77\"}");
            pollCount++;
            return pollCount == 1
                ? (200, "{\"errorId\":0,\"success\":true,\"taskId\":\"77\",\"status\":\"processing\"}")
                : (200, "{\"errorId\":0,\"success\":true,\"status\":\"ready\",\"solution\":{\"text\":\"8321\"}}");
        });
        using var vendor = new AionRecognitionClient(() => "jwt-token", ["https://decode.test"], new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) });
        var answer = await vendor.SolveAsync("aGVsbG8=", CancellationToken.None);
        check(answer == "8321", "The shared recognition service answers through the create/poll lifecycle");
        check(requests.Count == 3 && requests.All(u => u.Contains("/api/v8/", StringComparison.Ordinal)),
            "The shared recognition requests hit the pinned API endpoints");

        // Synchronous fast path: create already carries the solution text.
        var fast = new FakeHandler((_, _, _) => (200, "{\"errorId\":0,\"success\":true,\"solution\":{\"text\":\"1111\"}}"));
        using var vendorFast = new AionRecognitionClient(() => "jwt", ["https://decode.test"], new HttpClient(fast));
        check(await vendorFast.SolveAsync("aGVsbG8=", CancellationToken.None) == "1111",
            "A create response that already carries the answer is used immediately");

        // Not signed in: the client refuses without a token.
        using var offline = new AionRecognitionClient(() => null, ["https://decode.test"], new HttpClient(new FakeHandler((_, _, _) => (500, "{}"))));
        try
        {
            await offline.SolveAsync("aGVsbG8=", CancellationToken.None);
            check(false, "A missing updater login is refused");
        }
        catch (InvalidOperationException e)
        {
            check(e.Message.Contains("not signed in", StringComparison.Ordinal), "A missing updater login is refused with a clear message");
        }

        // Retired interface: 404 is permanent, so the chain skips straight to failover.
        var permanentSeen = false;
        var dead = new FakeHandler((_, _, _) => (404, "{}"));
        using var gone = new AionRecognitionClient(() => "jwt", ["https://decode.test"], new HttpClient(dead));
        try
        {
            await gone.SolveAsync("aGVsbG8=", CancellationToken.None);
            check(false, "A retired interface is rejected as permanent");
        }
        catch (InvalidOperationException e)
        {
            permanentSeen = e.Message.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
        }
        check(permanentSeen, "A retired interface (404) reports the updater as too old");

        // Failover chain: primary answers -> reported; primary fails -> backup answer used.
        string? reported = null;
        var chain = CaptchaAutoSolver.ComposeSolvers(
            (image, _) => Task.FromResult("primary-answer"),
            (image, _) => Task.FromResult("backup-answer"),
            p => reported = p);
        check(await chain("img", CancellationToken.None) == "primary-answer" && reported == "AION recognition",
            "A working shared recognition answers first and is reported");
        chain = CaptchaAutoSolver.ComposeSolvers(
            (image, _) => throw new InvalidOperationException("not signed in"),
            (image, _) => Task.FromResult("backup-answer"),
            p => reported = p);
        check(await chain("img", CancellationToken.None) == "backup-answer" && reported == "backup",
            "When the shared recognition is unavailable the backup key answers and is reported");

        // Solver integration: with no embedded key but a working shared service, the challenge is solved.
        using (var manager = new GameIpcManager())
        {
            (int pid, string command, object? args)? submitted = null;
            using var solver = new CaptchaAutoSolver(manager, null, "", 0, 0, vendorSolveOverride: (image, _) => Task.FromResult("7777"))
            {
                SubmitOverride = (pid, command, args) => { submitted = (pid, command, args); return Task.CompletedTask; },
            };
            check(solver.Enabled, "The solver is armed by the shared recognition alone");
            solver.OnChallenge(9, "req-v", "aGVsbG8=", null);
            for (var i = 0; i < 50 && submitted == null; i++) await Task.Delay(50);
            check(submitted != null && System.Text.Json.JsonSerializer.Serialize(submitted.Value.args).Contains("7777"),
                "A challenge is solved through the shared recognition without any embedded key");
        }

        // Disabled again: neither service available.
        using (var manager = new GameIpcManager())
        using (var solver = new CaptchaAutoSolver(manager, keyOverride: "", vendorTokenResolver: () => null))
            check(!solver.Enabled, "Without a login and without a key the solver stays off");
    }

    private sealed class FakeHandler(Func<string, string, string, (int Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() : "";
            var (status, payload) = respond(request.Method.Method, request.RequestUri?.ToString() ?? "", body);
            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}
