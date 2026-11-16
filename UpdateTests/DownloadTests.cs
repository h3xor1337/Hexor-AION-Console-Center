using System.Net;
using System.Net.Http;
using System.Text;
using Hexor.Updates;

// All transport responses are generated in memory. These tests never contact GitHub.
internal static class DownloadTests
{
    private const string Manifest = "https://raw.githubusercontent.com/hexor-tests/hexor-updates/main/latest.json";
    private const string Package = "https://github.com/hexor-tests/hexor-updates/releases/download/v1.0.1/Hexor-1.0.1-win-x64.zip";
    private const string Asset = "https://release-assets.githubusercontent.com/github-production-release-asset/test.zip?token=temporary";

    internal static async Task Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "download-tests");
        Directory.CreateDirectory(directory);
        int sequence = 0;
        string Target() => Path.Combine(directory, (++sequence).ToString() + ".download");

        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            check(rejected, label);
        }

        async Task RejectDownload(string url, RecordingHandler handler, string label, long limit = 100,
            CancellationToken cancellation = default)
        {
            var target = Target();
            bool rejected = false;
            try { await UpdateCore.DownloadWithHandler(url, target, limit, handler, token: cancellation); }
            catch { rejected = true; }
            check(rejected && !File.Exists(target), label);
        }

        var raw = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "signed manifest bytes"));
        var path = Target();
        await UpdateCore.DownloadWithHandler(Manifest, path, 128, raw);
        check(File.ReadAllText(path) == "signed manifest bytes" && raw.Requests.Count == 1,
            "raw GitHub manifest retrieval preserves exact bytes");

        var redirect = new RecordingHandler((_, index) => index == 0
            ? Redirect(HttpStatusCode.Found, Asset) : Response(HttpStatusCode.OK, "ZIP bytes"));
        path = Target();
        await UpdateCore.DownloadWithHandler(Package, path, 100, redirect);
        check(File.ReadAllText(path) == "ZIP bytes" && redirect.Requests.Select(r => r.Url).SequenceEqual(new[] { Package, Asset }),
            "GitHub release 302 follows approved signed asset URL");
        check(redirect.Requests.All(r => r.Method == HttpMethod.Get && r.Version == HttpVersion.Version11 &&
                r.VersionPolicy == HttpVersionPolicy.RequestVersionExact && !r.HasCredentials && !r.HasBody &&
                !r.HasRange && r.UserAgent == "Hexor-Updater/" + UpdateCore.UpdaterVersion),
            "redirect requests use fresh credential-free HTTP GET messages");

        var hops = new RecordingHandler((_, index) => index switch
        {
            0 => Redirect(HttpStatusCode.MovedPermanently, "/hexor-tests/hexor-updates/releases/download/v1.0.1/renamed.zip"),
            1 => Redirect(HttpStatusCode.SeeOther, "https://objects.githubusercontent.com/release/1"),
            2 => Redirect(HttpStatusCode.TemporaryRedirect, "https://github-releases.githubusercontent.com/release/2"),
            3 => Redirect(HttpStatusCode.PermanentRedirect, Asset),
            _ => Response(HttpStatusCode.OK, "approved hops")
        });
        path = Target();
        await UpdateCore.DownloadWithHandler(Package, path, 100, hops);
        check(File.ReadAllText(path) == "approved hops" && hops.Requests.Count == 5 &&
                hops.Requests[1].Url == "https://github.com/hexor-tests/hexor-updates/releases/download/v1.0.1/renamed.zip",
            "relative redirects and all approved GitHub delivery domains work");

        var maximum = new RecordingHandler((_, index) => index < UpdateCore.MaximumRedirects
            ? Redirect(HttpStatusCode.Found, Asset + "&hop=" + index) : Response(HttpStatusCode.OK, "five hops"));
        path = Target();
        await UpdateCore.DownloadWithHandler(Package, path, 100, maximum);
        check(File.ReadAllText(path) == "five hops" && maximum.Requests.Count == UpdateCore.MaximumRedirects + 1,
            "configured maximum number of redirects is accepted");

        var loop = new RecordingHandler((_, _) => Redirect(HttpStatusCode.Found, Asset));
        await RejectDownload(Package, loop, "redirect loop is bounded");
        check(loop.Requests.Count == UpdateCore.MaximumRedirects + 1, "redirect limit stops before another network request");

        var unsafeUrls = new (string Url, string Name)[]
        {
            ("http://github.com/hexor-tests/hexor-updates/releases/download/v1.0.1/update.zip", "HTTP downgrade"),
            ("https://github.com:444/update.zip", "non-HTTPS service port"),
            ("https://name:secret@github.com/update.zip", "embedded authentication credentials"),
            ("https://github.com/update.zip#fragment", "URL fragment"),
            ("https://github.com.attacker.invalid/update.zip", "GitHub hostname suffix lookalike"),
            ("https://release-assets.githubusercontent.com.attacker.invalid/update.zip", "asset hostname suffix lookalike"),
            ("https://evilgithub.com/update.zip", "GitHub hostname prefix lookalike"),
            ("https://raw.githubusercontent.com.attacker.invalid/latest.json", "raw hostname lookalike"),
            ("https://unapproved.githubusercontent.com/update.zip", "unapproved GitHub subdomain"),
            ("https://github.com./update.zip", "trailing-dot hostname alias"),
            ("https://localhost/update.zip", "localhost hostname"),
            ("https://private.internal/update.zip", "private network hostname"),
            ("https://127.0.0.1/update.zip", "loopback IPv4 literal"),
            ("https://10.1.2.3/update.zip", "private IPv4 literal"),
            ("https://169.254.169.254/update.zip", "link-local metadata endpoint"),
            ("https://140.82.112.3/update.zip", "public IP literal bypass"),
            ("https://[::1]/update.zip", "loopback IPv6 literal"),
            ("https://[::ffff:127.0.0.1]/update.zip", "mapped loopback IPv6 literal"),
            ("https://[fc00::1]/update.zip", "private IPv6 literal"),
            ("file:///C:/Windows/notepad.exe", "local file scheme"),
            ("ftp://github.com/update.zip", "non-HTTPS scheme")
        };
        foreach (var item in unsafeUrls)
        {
            var initial = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "must not download"));
            await RejectDownload(item.Url, initial, "initial URL rejects " + item.Name);
            check(initial.Requests.Count == 0, "unsafe initial " + item.Name + " rejected before transport");
            var unsafeRedirect = new RecordingHandler((_, _) => Redirect(HttpStatusCode.Found, item.Url));
            await RejectDownload(Package, unsafeRedirect, "redirect rejects " + item.Name);
            check(unsafeRedirect.Requests.Count == 1, "unsafe redirect " + item.Name + " is never requested");
        }

        foreach (var host in new[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" })
        {
            var initial = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "must not download"));
            await RejectDownload("https://" + host + "/update.zip", initial, "temporary CDN URL cannot be the initial package URL: " + host);
            check(initial.Requests.Count == 0, "CDN initial URL rejected before transport: " + host);
        }
        await RejectDownload(Package, new RecordingHandler((_, _) => Redirect(HttpStatusCode.Found, Manifest)),
            "release redirect cannot switch to raw content hosting");
        await RejectDownload(Package, new RecordingHandler((_, _) => Redirect(HttpStatusCode.Found, "//attacker.invalid/update.zip")),
            "scheme-relative unapproved redirect is rejected");
        await RejectDownload(Package, new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Found)),
            "redirect without Location is rejected");

        foreach (var status in new[] { 201, 202, 204, 206, 300, 304, 305, 400, 401, 403, 404, 429, 500, 503 })
            await RejectDownload(Package, new RecordingHandler((_, _) => Response((HttpStatusCode)status, "unusable response")),
                "download requires final HTTP 200, rejects " + status);

        await RejectDownload(Package, new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "payload", 101)),
            "declared oversized download rejected before creating destination", 100);
        await RejectDownload(Package, new RecordingHandler((_, _) => StreamResponse(new ChunkStream(Encoding.UTF8.GetBytes("oversized payload")))),
            "unknown-length streamed download enforces size limit and removes partial file", 5);
        await RejectDownload(Package, new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "actual body", 1)),
            "understated Content-Length is rejected and partial file removed");
        await RejectDownload(Package, new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "short body", 50)),
            "truncated Content-Length is rejected and partial file removed");
        await RejectDownload(Package, new RecordingHandler((_, _) => StreamResponse(new ChunkStream(Encoding.UTF8.GetBytes("partial bytes"), failAfterFirstRead: true))),
            "connection interruption after bytes written removes partial download");
        await RejectDownload(Package, new RecordingHandler((_, _) => throw new HttpRequestException("simulated offline transport")),
            "offline transport failure leaves no destination");

        var unknownLength = new RecordingHandler((_, _) => StreamResponse(new ChunkStream(Encoding.UTF8.GetBytes("unknown length"))));
        var reports = new ImmediateProgress();
        path = Target();
        await UpdateCore.DownloadWithHandler(Package, path, 100, unknownLength, reports);
        check(File.ReadAllText(path) == "unknown length" && reports.Values.Count > 0 && reports.Values.All(v => v.Percentage == null && v.TotalBytes == null),
            "valid unknown-length body downloads without invented percentage");
        var knownReports = new ImmediateProgress();
        path = Target();
        await UpdateCore.DownloadWithHandler(Package, path, 100,
            new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "known length")), knownReports);
        check(knownReports.Values.Last().Percentage == 100 && knownReports.Values.Last().BytesReceived == knownReports.Values.Last().TotalBytes, "known-length progress reaches 100 percent with actual byte totals");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "must not download"));
            await RejectDownload(Package, cancelled, "pre-cancelled download creates no file", cancellation: cancellation.Token);
            check(cancelled.Requests.Count == 0, "pre-cancelled download never invokes transport");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            await RejectDownload(Package,
                new RecordingHandler((_, _) => StreamResponse(new ChunkStream(Encoding.UTF8.GetBytes("cancel during body"), cancelAfterFirstRead: cancellation))),
                "cancellation after bytes written removes partial download", cancellation: cancellation.Token);
        }
        foreach (long limit in new long[] { 0, -1 })
        {
            var invalidLimit = new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "must not download"));
            await RejectDownload(Package, invalidLimit, "nonpositive download size limit rejected: " + limit, limit);
            check(invalidLimit.Requests.Count == 0, "nonpositive limit rejected before transport: " + limit);
        }

        path = Target();
        File.WriteAllText(path, "pre-existing customer file");
        bool existingRejected = false;
        try { await UpdateCore.DownloadWithHandler(Package, path, 100, new RecordingHandler((_, _) => Response(HttpStatusCode.OK, "replacement"))); }
        catch (IOException) { existingRejected = true; }
        check(existingRejected && File.ReadAllText(path) == "pre-existing customer file", "existing destination is neither overwritten nor deleted");

        var deniedAddresses = new[]
        {
            "0.0.0.0", "0.1.2.3", "10.0.0.1", "10.255.255.255", "100.64.0.0", "100.127.255.255",
            "127.0.0.1", "127.255.255.255", "169.254.0.1", "169.254.169.254", "172.16.0.1", "172.31.255.255",
            "192.0.0.1", "192.0.2.1", "192.88.99.1", "192.168.0.1", "198.18.0.1", "198.19.255.255",
            "198.51.100.1", "203.0.113.1", "224.0.0.1", "239.255.255.255", "240.0.0.1", "255.255.255.255",
            "::", "::1", "::10.0.0.1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "::ffff:169.254.169.254",
            "::ffff:192.168.1.1", "64:ff9b::a00:1", "64:ff9b:1::1", "100::1", "fc00::1", "fd12::1",
            "fe80::1", "fe80::1%1", "fec0::1", "ff02::1", "2001::1", "2001:0:4136:e378:8000:63bf:3fff:fdd2",
            "2001:100::1", "2001:db8::1", "2002:a00:1::1", "3fff::1", "3fff:fff::1"
        };
        foreach (var address in deniedAddresses)
            check(!UpdateCore.IsPublicAddress(IPAddress.Parse(address)), "network policy rejects special/private destination " + address);
        var allowedAddresses = new[]
        {
            "1.1.1.1", "8.8.8.8", "100.63.255.255", "100.128.0.1", "140.82.112.3", "172.15.255.255", "172.32.0.1",
            "192.0.1.1", "198.17.255.255", "198.20.0.1", "::ffff:140.82.112.3", "2001:4860:4860::8888", "2606:4700::1111"
        };
        foreach (var address in allowedAddresses)
            check(UpdateCore.IsPublicAddress(IPAddress.Parse(address)), "network policy accepts public destination " + address);
        Reject(() => UpdateCore.ValidateAddresses(Array.Empty<IPAddress>()), "empty DNS answer is rejected");
        Reject(() => UpdateCore.ValidateAddresses(new[] { IPAddress.Parse("140.82.112.3"), IPAddress.Parse("127.0.0.1") }),
            "mixed public/private IPv4 DNS answer is rejected");
        Reject(() => UpdateCore.ValidateAddresses(new[] { IPAddress.Parse("2606:4700::1111"), IPAddress.Parse("fd00::1") }),
            "mixed public/private IPv6 DNS answer is rejected");
        UpdateCore.ValidateAddresses(new[] { IPAddress.Parse("140.82.112.3"), IPAddress.Parse("2606:4700::1111") });
        check(true, "all-public dual-stack DNS answer accepted");

        check(UpdateCore.ManifestUrl("hexor-tests/hexor-updates") == Manifest && UpdateCore.ValidateManifestUrl(Manifest).AbsoluteUri == Manifest,
            "stable manifest URL is derived from configured repository");
        check(UpdateCore.ValidateReleaseUrl(Package).AbsoluteUri == Package, "versioned GitHub release URL accepted");
        foreach (var repository in new[] { "OWNER/hexor-updates", "YOUR-OWNER/hexor-updates", "your-org/hexor-updates", "hexor-tests/REPO", "hexor-tests/PLACEHOLDER", "hexor-tests", "hexor-tests/repo/extra", "hexor-tests/repo?token=secret", "-owner/repo" })
            Reject(() => UpdateCore.ManifestUrl(repository), "invalid or placeholder repository rejected: " + repository);
        foreach (var url in new[] { Manifest.Replace("/main/", "/develop/"), Manifest + "?token=secret", Manifest.Replace("latest.json", "LATEST.JSON"), Manifest.Replace("hexor-tests", "OWNER"), "https://github.com/hexor-tests/hexor-updates/blob/main/latest.json", "https://updates.hexor.wtf/latest.json" })
            Reject(() => UpdateCore.ValidateManifestUrl(url), "manifest configuration rejects noncanonical endpoint: " + url);
        foreach (var url in new[] { Package + "?token=secret", Package.Replace("/download/v1.0.1/", "/latest/download/"), Asset, Manifest, "https://updates.hexor.wtf/update.zip" })
            Reject(() => UpdateCore.ValidateReleaseUrl(url), "signed package URL requires stable versioned GitHub release endpoint: " + url);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        if (declaredLength.HasValue) response.Content.Headers.ContentLength = declaredLength.Value;
        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage StreamResponse(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    private sealed record RequestSnapshot(string Url, HttpMethod Method, Version Version, HttpVersionPolicy VersionPolicy,
        bool HasCredentials, bool HasBody, bool HasRange, string UserAgent);

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Requests.Count;
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method, request.Version, request.VersionPolicy,
                request.Headers.Authorization != null || request.Headers.Contains("Proxy-Authorization") || request.Headers.Contains("Cookie"),
                request.Content != null, request.Headers.Range != null, request.Headers.UserAgent.ToString()));
            return Task.FromResult(respond(request, index));
        }
    }

    private sealed class ImmediateProgress : IProgress<DownloadProgress>
    {
        internal List<DownloadProgress> Values { get; } = new();
        public void Report(DownloadProgress value) => Values.Add(value);
    }

    private sealed class ChunkStream(byte[] bytes, bool failAfterFirstRead = false,
        CancellationTokenSource? cancelAfterFirstRead = null) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (position > 0 && failAfterFirstRead) throw new IOException("simulated connection interruption");
            if (position > 0 && cancelAfterFirstRead != null)
            {
                cancelAfterFirstRead.Cancel();
                cancelAfterFirstRead.Token.ThrowIfCancellationRequested();
            }
            int count = Math.Min(Math.Min(3, buffer.Length), bytes.Length - position);
            bytes.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
    }
}
