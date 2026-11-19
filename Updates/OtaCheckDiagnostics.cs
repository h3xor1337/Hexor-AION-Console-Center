using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hexor.Updates;

internal enum OtaCheckStage { TrustConfiguration, ManifestFetch, ManifestRead, ManifestValidation, ManifestCleanup, Presentation, CompanionSync }

internal static class OtaCheckDiagnostics
{
    // Only controlled labels and numeric status are emitted: never exception messages,
    // request URLs, headers, file paths, inner exceptions, or trust material.
    internal static string Format(OtaCheckStage stage, Exception error, bool installCallbackInvoked = false)
    {
        var status = (error as HttpRequestException)?.StatusCode;
        var label = stage switch
        {
            OtaCheckStage.TrustConfiguration => "trust-configuration",
            OtaCheckStage.ManifestFetch when status.HasValue => "response-validation",
            OtaCheckStage.ManifestFetch => "manifest-fetch",
            OtaCheckStage.ManifestRead => "manifest-read",
            OtaCheckStage.ManifestValidation => "manifest-validation",
            OtaCheckStage.ManifestCleanup => "manifest-cleanup",
            OtaCheckStage.Presentation => "presentation",
            OtaCheckStage.CompanionSync => "companion-sync",
            _ => "unknown"
        };
        var (type, category) = error switch
        {
            HttpRequestException => ("HttpRequestException", status.HasValue ? "http-status" : "http-request"),
            OperationCanceledException => ("OperationCanceledException", "cancelled-or-timeout"),
            CryptographicException => ("CryptographicException", "signature-validation"),
            JsonException => ("JsonException", "manifest-format"),
            InvalidDataException => ("InvalidDataException", "invalid-data"),
            UnauthorizedAccessException => ("UnauthorizedAccessException", "access-denied"),
            IOException => ("IOException", "io"),
            InvalidOperationException => ("InvalidOperationException", "invalid-operation"),
            ArgumentException => ("ArgumentException", "invalid-argument"),
            _ => ("Exception", "other")
        };
        return $"check-failed; type={type}; stage={label}; category={category}; http-status={(status.HasValue ? ((int)status.Value).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable")}; install-callback-invoked={(installCallbackInvoked ? "true" : "false")}";
    }
}
