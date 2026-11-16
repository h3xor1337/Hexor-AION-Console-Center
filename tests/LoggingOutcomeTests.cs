using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Hexor;
using Hexor.Updates;

internal static class LoggingOutcomeTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        JsonElement[] Records() => File.ReadLines(Diagnostics.ActiveLogPath).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
        string Details(JsonElement r) => r.GetProperty("eventDetails").GetString()!;
        bool Severity(JsonElement r, string value) => r.GetProperty("severity").GetString() == value;

        var cleanupCases = new (VendorOverlayCleanupState[] States, string Details, string Severity)[]
        {
            ([VendorOverlayCleanupState.Removed, VendorOverlayCleanupState.Restored, VendorOverlayCleanupState.Skipped], "completed; removed=2; failed=0", "information"),
            ([], "completed; removed=0; failed=0", "information"),
            ([VendorOverlayCleanupState.Removed, VendorOverlayCleanupState.Failed], "completed; removed=1; failed=1", "error"),
            ([VendorOverlayCleanupState.BlockedByRunningProcess], "completed; removed=0; failed=1", "error")
        };
        foreach (var item in cleanupCases)
        {
            // Exercise the production summary logger only; never execute cleanup.
            var results = item.States.Select(state => new CleanupPlanItem(CleanupItemKind.Overlay, "fixture", state, "fixture")).ToArray();
            MainWindow.LogCleanupCompleted(results);
            var record = Records().Last();
            check(Severity(record, item.Severity) && Details(record) == item.Details
                && record.GetProperty("component").GetString() == "cleanup"
                && record.GetProperty("area").GetString() == "cleanup"
                && record.GetProperty("eventName").GetString() == item.Details
                && record.GetProperty("sessionId").GetString() == Diagnostics.SessionId
                && record.GetProperty("processId").GetInt32() == Environment.ProcessId
                && record.GetProperty("timestamp").GetDateTimeOffset() != default,
                $"Cleanup {item.Details} uses {item.Severity} and preserves event content and correlation");
        }

        StorageAudit.Run(StorageTarget.Registry, ["ResourceGrab.AuctionAutoSell"], () => true, value => value);
        var audit = Records().Where(r => r.GetProperty("component").GetString() == "storage-audit").ToArray();
        using var attempt = JsonDocument.Parse(Details(audit[0]));
        using var verified = JsonDocument.Parse(Details(audit[1]));
        check(Severity(audit[0], "information") && attempt.RootElement.GetProperty("Outcome").GetString() == "Attempt"
            && attempt.RootElement.GetProperty("ErrorCategory").ValueKind == JsonValueKind.Null, "Storage Attempt without an error is informational");
        check(Severity(audit[1], "information") && verified.RootElement.GetProperty("Outcome").GetString() == "ReadBackVerified"
            && verified.RootElement.GetProperty("ErrorCategory").ValueKind == JsonValueKind.Null, "ReadBackVerified with ErrorCategory=null is informational");
        check(attempt.RootElement.GetProperty("OperationId").GetString() == verified.RootElement.GetProperty("OperationId").GetString()
            && audit.All(r => r.GetProperty("sessionId").GetString() == Diagnostics.SessionId
                && r.GetProperty("processId").GetInt32() == Environment.ProcessId
                && r.GetProperty("timestamp").GetDateTimeOffset() != default
                && r.GetProperty("area").GetString() == r.GetProperty("component").GetString()
                && r.GetProperty("eventName").GetString() == Details(r)), "Structured fields and operation/session correlation remain compatible");
        foreach (var error in new Exception[] { new IOException("test"), new InvalidDataException("test") })
        {
            var propagated = false;
            try { StorageAudit.Run<bool>(StorageTarget.Registry, [], () => throw error, value => value); }
            catch (Exception caught) { propagated = ReferenceEquals(caught, error); }
            var record = Records().Last();
            using var payload = JsonDocument.Parse(Details(record));
            var expected = error is InvalidDataException ? "Rejected" : "Failed";
            check(propagated && Severity(record, "error") && payload.RootElement.GetProperty("Outcome").GetString() == expected,
                $"Storage {expected} is an error and preserves the original exception");
        }

        var registry = new MemoryRegistry();
        var service = new RegistryInitializationService(registry);
        var result = service.Ensure();
        check(result.Success && result.ErrorsEncountered == 0 && Severity(Records().Last(), "information")
            && Details(Records().Last()).Contains("errors=0"), "Successful synchronization summary is informational");
        registry.Invalid = true;
        var offset = Records().Length;
        result = service.Ensure();
        var findings = Records().Skip(offset).ToArray();
        check(result.Success && result.InvalidValuesDetected == 1 && Severity(findings.Last(), "information")
            && findings.Any(r => Details(r).StartsWith("invalid-value-preserved;") && Severity(r, "warning"))
            && registry.Invalid, "Invalid preserved value is a warning; errors=0 summary stays informational without changing the value");
        registry.Invalid = false; registry.Denied = true;
        offset = Records().Length;
        result = service.Ensure();
        check(!result.Success && result.ErrorsEncountered > 0 && Severity(Records().Last(), "error")
            && Records().Skip(offset).Any(r => Details(r).StartsWith("failure;") && Severity(r, "error")), "Genuine synchronization failures and summary are errors");
        registry.Denied = false; registry.SchemaDenied = true;
        result = service.Ensure();
        check(!result.Success && result.ErrorsEncountered == 1 && Severity(Records().Last(), "error"),
            "Schema metadata failure retains error severity even without a per-setting failure");
        check(registry.WriteCalls == 0, "Diagnostic fixtures never request settings writes or touch the Windows registry");

        const string secret = "https://example.invalid/?token=PRIVATE password=PRIVATE C:\\Customer\\PRIVATE";
        var cases = new (OtaCheckStage Stage, Exception Error, string Category)[]
        {
            (OtaCheckStage.ManifestFetch, new HttpRequestException(secret), "http-request"),
            (OtaCheckStage.ManifestFetch, new HttpRequestException(secret, new Exception(secret), HttpStatusCode.Forbidden), "http-status"),
            (OtaCheckStage.ManifestValidation, new CryptographicException(secret), "signature-validation"),
            (OtaCheckStage.ManifestValidation, new JsonException(secret), "manifest-format"),
            (OtaCheckStage.ManifestFetch, new TaskCanceledException(secret), "cancelled-or-timeout"),
            (OtaCheckStage.ManifestRead, new IOException(secret), "io"),
            (OtaCheckStage.TrustConfiguration, new InvalidOperationException(secret), "invalid-operation"),
            (OtaCheckStage.ManifestCleanup, new UnauthorizedAccessException(secret), "access-denied"),
            (OtaCheckStage.Presentation, new Exception(secret), "other")
        };
        foreach (var item in cases)
        {
            var details = OtaCheckDiagnostics.Format(item.Stage, item.Error);
            Diagnostics.Log("ota", details, DiagnosticSeverity.Error);
            check(details.Contains("category=" + item.Category + ";") && details.Contains("install-callback-invoked=false")
                && !details.Contains("PRIVATE") && !details.Contains("https:") && !details.Contains("Customer")
                && Severity(Records().Last(), "error"), $"OTA {item.Category} diagnostics are sanitized and retain error severity");
        }
        check(OtaCheckDiagnostics.Format(OtaCheckStage.ManifestFetch, cases[1].Error).Contains("stage=response-validation; category=http-status; http-status=403;"),
            "HTTP status failures identify response validation and retain numeric status");
        check(OtaCheckDiagnostics.Format(OtaCheckStage.ManifestFetch, cases[0].Error).Contains("stage=manifest-fetch; category=http-request; http-status=unavailable;"),
            "Network failures do not invent HTTP status or a deeper failure cause");
        check(OtaCheckDiagnostics.Format(OtaCheckStage.Presentation, new Exception(secret), true).Contains("install-callback-invoked=true"),
            "Later presentation failures can report that the install callback was invoked");
        using var handler = new StatusHandler();
        try
        {
            UpdateCore.DownloadWithHandler("https://raw.githubusercontent.com/owner/repo/main/latest.json", Path.Combine(root, "manifest.json"), 131072, handler).GetAwaiter().GetResult();
            check(false, "Non-OK response rejected");
        }
        catch (HttpRequestException error)
        {
            check(error.StatusCode == HttpStatusCode.ServiceUnavailable && handler.Requests == 1
                && !File.Exists(Path.Combine(root, "manifest.json")), "Transport retains HTTP status while rejecting the response without retry or file creation");
        }
    }

    private sealed class MemoryRegistry : IRegistrySettingsService
    {
        public bool Invalid, Denied, SchemaDenied;
        public int WriteCalls;
        public int? Read(string section, string name) => Inspect(section, name).Value;
        public RegistryEntry Inspect(string section, string name)
        {
            if (Denied) throw new UnauthorizedAccessException();
            var setting = RegistryDefaults.Template.Single(t => t.Section == section && t.Name == name);
            return new(Invalid && section == "ResourceGrab" && name == "AbyssLayer" ? 999 : setting.InitializationDefault, true, true);
        }
        public void WriteVerified(string section, string name, int value) { WriteCalls++; throw new Exception("Unexpected settings write"); }
        public int ReadAppliedSchemaVersion() => LegionCommanderRegistrySchema.Version;
        public void WriteAppliedSchemaVersion(int version) { if (SchemaDenied) throw new UnauthorizedAccessException(); }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }
}
