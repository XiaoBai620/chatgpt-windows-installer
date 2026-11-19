using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PackagePicker;

internal static class PackageDownloadTests
{
    static readonly Uri Source = new("https://persistent.oaistatic.com/codex-app-prod/test.msix");
    static readonly byte[] Body = Enumerable.Range(0, 64).Select(x => (byte)x).ToArray();
    static readonly PackageDownload.Settings Fast = new(TimeSpan.FromMilliseconds(90), TimeSpan.FromMilliseconds(12), TimeSpan.Zero, 1);
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls); return Task.FromResult(respond(request)); }
    }
    sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    sealed class Reports : IProgress<DownloadProgress>
    {
        readonly object gate = new();
        readonly List<DownloadProgress> items = [];
        public void Report(DownloadProgress value) { lock (gate) items.Add(value); }
        internal DownloadProgress[] Values { get { lock (gate) return items.ToArray(); } }
    }
    sealed class BrokenStream(byte[] first, bool stall, Action? waiting = null, Action? canceled = null, Action? disposed = null) : Stream
    {
        bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!sent) { sent = true; if (first.Length > 0) { first.AsMemory().CopyTo(buffer); return first.Length; } }
            if (stall)
            {
                waiting?.Invoke();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { canceled?.Invoke(); throw; }
            }
            throw new IOException("Simulated disconnected network");
        }
        protected override void Dispose(bool disposing) { if (disposing) disposed?.Invoke(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    static HttpResponseMessage Full() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) };
    static HttpResponseMessage Range(HttpRequestMessage request, string tag = "\"stable\"", bool badRange = false, Stream? stream = null)
    {
        var range = request.Headers.Range!.Ranges.Single(); var first = range.From!.Value; var last = range.To ?? Body.Length - 1;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = stream == null ? new ByteArrayContent(Body[(int)first..((int)last + 1)]) : new StreamContent(stream)
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(first + (badRange ? 1 : 0), last, Body.Length);
        response.Content.Headers.ContentLength = last - first + 1;
        return response;
    }
    static bool Probe(HttpRequestMessage request) => request.Headers.Range?.Ranges.Single() is { From: 0, To: 0 };
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static async Task Reject<T>(Func<Task> call) where T : Exception
    { try { await call(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static int Run()
    {
        try { Test().GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("Package download regression: " + ex); return 18; }
    }
    static async Task Test()
    {
        var directory = Directory.CreateTempSubdirectory("PickerDownloadTests-");
        int next = 0;
        string File() => Path.Combine(directory.FullName, (++next) + ".msix");
        try
        {
            // Package validation is local work, so it must not look like a zero-speed network transfer.
            foreach (var phase in new[] { DownloadPhase.Verifying, DownloadPhase.Saving })
            {
                var local = new DownloadProgress("正在处理本地安装包…", 1024, 2048, 1024, Idle: TimeSpan.FromSeconds(5), Phase: phase);
                Assert(local.Text == local.Status && local.Statistics.Length == 0 && local.NetworkHint.Length == 0 && local.Percent == null && local.Remaining == null,
                    "Local validation or saving was presented as a network transfer");
            }
            Assert(new DownloadProgress("正在下载", 1024, 2048, 64, TimeSpan.FromSeconds(10)).NetworkHint.Contains("仍在收到数据"), "Slow transfer feedback is ambiguous");
            Assert(new DownloadProgress("等待网络", 1024, 2048, 0, Idle: TimeSpan.FromSeconds(12)).NetworkHint.Contains("12 秒未收到"), "Stalled transfer feedback is ambiguous");
            // Range workers must assemble distinct offsets and resume only after bytes successfully written.
            int resumed = 0, broken = 0;
            var rangeHandler = new Handler(request =>
            {
                if (Probe(request)) return Range(request);
                Assert(request.Headers.IfMatch.Single().ToString() == "\"stable\"", "Missing strong precondition");
                var first = request.Headers.Range!.Ranges.Single().From;
                if (first == 3) Interlocked.Increment(ref resumed);
                if (first == 0 && Interlocked.Increment(ref broken) == 1) return Range(request, stream: new BrokenStream(Body[..3], false));
                return Range(request);
            });
            var reports = new Reports(); var file = File();
            using (var client = new HttpClient(rangeHandler)) await PackageDownload.FetchAsync(client, Source, file, 1024, "测试", reports, default, true, Fast);
            Assert(System.IO.File.ReadAllBytes(file).SequenceEqual(Body) && resumed == 1, "Resumed assembly differs");
            Assert(reports.Values.Last().Connections == 4 && reports.Values.Last().Bytes == Body.Length, "Incorrect multi-connection totals");

            // Exact reported symptom: bytes arrive, then the socket stops; heartbeats must continue and reconnect from 4.
            int resumedStall = 0;
            var stallHandler = new Handler(request =>
            {
                if (Probe(request)) return Range(request);
                if (request.Headers.Range!.Ranges.Single().From == 0) return Range(request, stream: new BrokenStream(Body[..4], true));
                Interlocked.Increment(ref resumedStall); return Range(request);
            });
            reports = new Reports(); file = File();
            using (var client = new HttpClient(stallHandler)) await PackageDownload.FetchAsync(client, Source, file, 1024, "测试", reports, default, false, Fast);
            Assert(resumedStall == 1 && System.IO.File.ReadAllBytes(file).SequenceEqual(Body), "Stalled stream did not resume");
            Assert(reports.Values.Count(p => p.Bytes == 4) >= 3 && reports.Values.Any(p => p.Idle.TotalMilliseconds >= 30), "No heartbeat while stalled");

            // No range support uses one complete response without splicing unverified partial data.
            var fullHandler = new Handler(_ => Full()); reports = new Reports(); file = File();
            using (var client = new HttpClient(fullHandler)) await PackageDownload.FetchAsync(client, Source, file, 1024, "测试", reports, default, true, Fast);
            Assert(System.IO.File.ReadAllBytes(file).SequenceEqual(Body) && fullHandler.Calls == 2 && reports.Values.Last().Connections == 1, "Single-connection fallback failed");

            // Weak validators cannot bind several HTTP responses into one trusted file.
            var weakHandler = new Handler(request => Probe(request) ? Range(request, "W/\"weak\"") : Full()); reports = new Reports(); file = File();
            using (var client = new HttpClient(weakHandler)) await PackageDownload.FetchAsync(client, Source, file, 1024, "测试", reports, default, true, Fast);
            Assert(weakHandler.Calls == 2 && System.IO.File.ReadAllBytes(file).SequenceEqual(Body) && reports.Values.Last().Connections == 1, "Weak validator enabled ranges");

            var tooLarge = new Handler(request => Range(request));
            using (var client = new HttpClient(tooLarge))
            {
                var target = File();
                await Reject<InvalidDataException>(() => PackageDownload.FetchAsync(client, Source, target, 1, "测试", new Reports(), default, true, Fast));
                Assert(tooLarge.Calls == 1 && new FileInfo(target).Length == 0, "Oversized probe started a body download");
            }

            // A server ignoring Range after the probe triggers a full restart, never concatenation.
            var ignoredHandler = new Handler(request =>
            {
                if (Probe(request)) return Range(request);
                var full = Full(); full.Headers.ETag = EntityTagHeaderValue.Parse("\"stable\""); return full;
            }); reports = new Reports(); file = File();
            using (var client = new HttpClient(ignoredHandler)) await PackageDownload.FetchAsync(client, Source, file, 1024, "测试", reports, default, true, Fast);
            Assert(System.IO.File.ReadAllBytes(file).SequenceEqual(Body) && reports.Values.Last().Connections == 1, "Ignored range was not restarted safely");

            // Content changes, malformed ranges and untrusted redirects must stop rather than silently fall back.
            foreach (var corrupt in new[] { "etag", "range", "redirect" })
            {
                var handler = new Handler(request =>
                {
                    if (corrupt == "redirect") return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.invalid/file") } };
                    return Probe(request) ? Range(request) : Range(request, tag: corrupt == "etag" ? "\"changed\"" : "\"stable\"", badRange: corrupt == "range");
                });
                using var client = new HttpClient(handler); var target = File();
                await Reject<InvalidDataException>(() => PackageDownload.FetchAsync(client, Source, target, 1024, "测试", new Reports(), default, false, Fast));
            }

            // A validation failure in one worker must cancel the three already blocked readers and release every handle.
            var allWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int waiting = 0, canceled = 0, disposed = 0;
            var stopPeers = new AsyncHandler(async (request, token) =>
            {
                if (Probe(request)) return Range(request);
                if (request.Headers.Range!.Ranges.Single().From == 0)
                {
                    await allWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }
                return Range(request, stream: new BrokenStream([], true,
                    () => { if (Interlocked.Increment(ref waiting) == 3) allWaiting.TrySetResult(); },
                    () => Interlocked.Increment(ref canceled), () => Interlocked.Increment(ref disposed)));
            });
            using (var client = new HttpClient(stopPeers))
            {
                var target = File();
                await Reject<InvalidDataException>(() => PackageDownload.FetchAsync(client, Source, target, 1024, "测试", new Reports(), default, true, Fast with { IdleTimeout = TimeSpan.FromSeconds(3) }));
                Assert(waiting == 3 && canceled == 3 && disposed >= 3, "Other range readers were not canceled and disposed");
                using var released = System.IO.File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            // All retries exhausted is bounded; caller cancellation interrupts a waiting body immediately.
            var alwaysStalls = new Handler(request => Probe(request) ? Range(request) : Range(request, stream: new BrokenStream([], true)));
            using (var client = new HttpClient(alwaysStalls))
            {
                var target = File();
                await Reject<TimeoutException>(() => PackageDownload.FetchAsync(client, Source, target, 1024, "测试", new Reports(), default, false, Fast));
                Assert(alwaysStalls.Calls == 4, "Retry budget was not bounded to three attempts");
            }
            var cancelHandler = new Handler(request => Probe(request) ? Range(request) : Range(request, stream: new BrokenStream([], true)));
            using (var client = new HttpClient(cancelHandler))
            using (var stop = new CancellationTokenSource(35))
            {
                var target = File();
                await Reject<OperationCanceledException>(() => PackageDownload.FetchAsync(client, Source, target, 1024, "测试", new Reports(), stop.Token, false, Fast));
            }
            var exists = File(); System.IO.File.WriteAllText(exists, "preserve");
            using (var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected network"))))
                await Reject<IOException>(() => PackageDownload.FetchAsync(client, Source, exists, 1024, "测试", new Reports(), default, true, Fast));
            Assert(System.IO.File.ReadAllText(exists) == "preserve", "Existing file was changed");
        }
        finally { directory.Delete(true); }
    }
}
