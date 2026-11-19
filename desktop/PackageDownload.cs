using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace PackagePicker;

internal enum DownloadPhase { Transferring, Verifying, Saving }

internal sealed record DownloadProgress(string Status, long Bytes = 0, long? TotalBytes = null,
    double BytesPerSecond = 0, TimeSpan Elapsed = default, TimeSpan Idle = default,
    int Connections = 1, int Attempt = 1, DownloadPhase Phase = DownloadPhase.Transferring)
{
    internal double? Percent => Phase == DownloadPhase.Transferring && TotalBytes is > 0 ? Math.Clamp(Bytes * 100d / TotalBytes.Value, 0, 100) : null;
    internal TimeSpan? Remaining => Phase == DownloadPhase.Transferring && TotalBytes is long total && BytesPerSecond > 0
        ? TimeSpan.FromSeconds(Math.Clamp((total - Bytes) / BytesPerSecond, 0, 8640000)) : null;
    internal static string Size(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:F1} MiB" : $"{bytes / 1024:F0} KiB";
    internal string Statistics => Phase != DownloadPhase.Transferring ? "" : Size(Bytes) + (TotalBytes is long total ? $" / {Size(total)} · {Percent:F1}%" : "")
        + $" · {Size(BytesPerSecond)}/s" + (Remaining is TimeSpan eta ? $" · 预计剩余 {(eta.TotalHours >= 1 ? $"{eta.TotalHours:F1} 小时" : eta.TotalMinutes >= 1 ? $"{Math.Ceiling(eta.TotalMinutes)} 分钟" : $"{Math.Ceiling(eta.TotalSeconds)} 秒")}" : " · 剩余时间待估算");
    internal string NetworkHint => Phase != DownloadPhase.Transferring ? "" : Idle.TotalSeconds >= 3
        ? $"连续 {Idle.TotalSeconds:F0} 秒未收到网络数据；30 秒无数据将自动重连。"
        : Bytes > 0 && BytesPerSecond is > 0 and < 131072 && Elapsed.TotalSeconds >= 8
            ? "仍在收到数据，当前速度较慢。多连接仍受本机带宽和官方线路影响。" : "";
    internal string Text => Phase != DownloadPhase.Transferring ? Status
        : $"{Status}\n{Statistics}" + (NetworkHint.Length > 0 ? "\n" + NetworkHint : "");
}

internal static class PackageDownload
{
    internal sealed record Settings(TimeSpan IdleTimeout, TimeSpan Heartbeat, TimeSpan RetryDelay, long ParallelThreshold)
    {
        internal static Settings Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 8L * 1024 * 1024);
    }
    sealed class RangeUnavailableException : Exception { }
    sealed class TransferState(string label, int connections, long? length)
    {
        readonly object gate = new();
        readonly Stopwatch clock = Stopwatch.StartNew();
        long bytes, sampleBytes, lastData;
        TimeSpan sampleAt;
        int attempt = 1;
        string? note;
        internal void SetLength(long? value) { lock (gate) length = value; }
        internal void Added(int count) { lock (gate) { bytes += count; lastData = clock.ElapsedTicks; } }
        internal void Restart() { lock (gate) { bytes = 0; sampleBytes = 0; sampleAt = clock.Elapsed; lastData = clock.ElapsedTicks; } }
        internal void Retrying(int number, bool resume) { lock (gate) { attempt = Math.Max(attempt, number); note = resume ? "网络中断，保留进度并重连" : "网络中断，重新连接官方下载"; } }
        internal void Receiving() { lock (gate) note = null; }
        internal DownloadProgress Snapshot(bool complete = false)
        {
            lock (gate)
            {
                var elapsed = clock.Elapsed;
                var seconds = (elapsed - sampleAt).TotalSeconds;
                var speed = seconds > 0 ? Math.Max(0, bytes - sampleBytes) / seconds : 0;
                sampleBytes = bytes; sampleAt = elapsed;
                var idle = TimeSpan.FromSeconds((clock.ElapsedTicks - lastData) / (double)Stopwatch.Frequency);
                return new DownloadProgress(complete ? $"{label}下载完成" : note ?? $"正在下载{label} · {(connections > 1 ? $"{connections} 路官方连接加速" : "单连接")}" + (attempt > 1 ? $" · 第 {attempt}/3 次尝试" : ""), bytes, length, speed, elapsed, idle, connections, attempt);
            }
        }
    }
    static bool Allowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.IdnHost.Equals("persistent.oaistatic.com", StringComparison.OrdinalIgnoreCase);
    static string? StrongTag(HttpResponseMessage response) => response.Headers.ETag is { IsWeak: false, Tag.Length: > 2 } tag ? tag.ToString() : null;
    static void Raw(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("下载响应经过内容编码，已拒绝。");
    }
    static bool Transient(Exception ex) => ex is not InvalidDataException && (ex is TimeoutException or IOException || ex is HttpRequestException http &&
        (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500));
    static void Length(long? bytes, long limit)
    {
        if (bytes is <= 0 || bytes > limit) throw new InvalidDataException("下载文件大小超出允许范围。");
    }
    static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri source, long? first, long? last, string? etag, Settings settings, CancellationToken ct)
    {
        var uri = source;
        for (int hop = 0; hop <= 3; hop++)
        {
            if (!Allowed(uri)) throw new InvalidDataException("下载地址不是允许的官方 HTTPS 来源。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (first.HasValue) request.Headers.Range = new RangeHeaderValue(first.Value, last);
            if (etag != null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.IdleTimeout);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new TimeoutException("连接官方服务器超时。", ex); }
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (hop == 3 || response.Headers.Location is not Uri location || !Uri.TryCreate(uri, location, out var next)) throw new InvalidDataException("下载重定向次数过多或缺少有效目标。");
                uri = next;
            }
        }
        throw new InvalidDataException("下载重定向次数超限。");
    }
    static void RequireStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new InvalidDataException("官方下载内容已更新，请重新下载，避免混用不同版本。");
        if (response.StatusCode != expected) throw new HttpRequestException($"官方下载失败：HTTP {(int)response.StatusCode}。", null, response.StatusCode);
    }
    static void ValidateRange(HttpResponseMessage response, long first, long last, long total, string etag)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            if (StrongTag(response) != etag) throw new InvalidDataException("官方文件标识发生变化，已停止下载。");
            throw new RangeUnavailableException();
        }
        RequireStatus(response, HttpStatusCode.PartialContent); Raw(response);
        var range = response.Content.Headers.ContentRange;
        if (range?.Unit != "bytes" || range.From != first || range.To != last || range.Length != total ||
            response.Content.Headers.ContentLength != last - first + 1 || StrongTag(response) != etag)
            throw new InvalidDataException("分段响应的范围、长度或文件标识不一致，已停止下载。");
    }
    static async Task ReadAsync(HttpResponseMessage response, SafeFileHandle file, long offset, long? expected, long limit, TransferState state, Settings settings, CancellationToken ct)
    {
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[65536]; long received = 0;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(settings.IdleTimeout);
            int count;
            try { count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new TimeoutException("连续 30 秒未收到网络数据。", ex); }
            if (count == 0) break;
            if (received + count > (expected ?? limit)) throw new InvalidDataException("下载内容超过长度限制。");
            await RandomAccess.WriteAsync(file, buffer.AsMemory(0, count), offset + received, ct).ConfigureAwait(false);
            received += count; state.Added(count); state.Receiving();
        }
        if (received == 0 || expected.HasValue && received != expected.Value) throw new IOException("下载连接提前结束，文件尚不完整。");
    }
    internal static async Task FetchAsync(HttpClient client, Uri uri, string destination, long limit, string label,
        IProgress<DownloadProgress> progress, CancellationToken ct, bool accelerate = true, Settings? configuration = null)
    {
        var settings = configuration ?? Settings.Default;
        ct.ThrowIfCancellationRequested();
        // CreateNew protects existing files. Caller owns the new staging directory and removes it on all exits.
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
        progress.Report(new DownloadProgress("正在连接官方源并检查断点续传能力…"));
        long? total = null; string? tag = null; bool ranges = false;
        using var connectionHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connectionClock = Stopwatch.StartNew();
        string connectionStatus = "正在连接官方源并检查断点续传能力…";
        async Task ConnectionMonitor()
        {
            while (true)
            {
                progress.Report(new DownloadProgress(connectionStatus, Elapsed: connectionClock.Elapsed, Idle: connectionClock.Elapsed));
                await Task.Delay(settings.Heartbeat, connectionHeartbeat.Token).ConfigureAwait(false);
            }
        }
        var connectionMonitor = ConnectionMonitor();
        try
        {
          for (int attempt = 1; ; attempt++)
          {
            try
            {
                using var probe = await SendAsync(client, uri, 0, 0, null, settings, ct).ConfigureAwait(false);
                Raw(probe);
                if (probe.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = probe.Content.Headers.ContentRange;
                    if (range?.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is not > 0 || probe.Content.Headers.ContentLength != 1)
                        throw new InvalidDataException("官方服务器返回了无效的分段信息。");
                    total = range.Length; tag = StrongTag(probe); ranges = tag != null;
                }
                else
                {
                    RequireStatus(probe, HttpStatusCode.OK);
                    if (probe.Content.Headers.ContentRange != null) throw new InvalidDataException("下载响应不是完整的原始文件。");
                    total = probe.Content.Headers.ContentLength;
                }
                Length(total, limit); break;
            }
            catch (Exception ex) when (attempt < 3 && Transient(ex))
            {
                connectionStatus = $"连接失败，正在重试官方源（{attempt + 1}/3）…";
                progress.Report(new DownloadProgress(connectionStatus));
                await Task.Delay(settings.RetryDelay, ct).ConfigureAwait(false);
            }
          }
        }
        finally
        {
            connectionHeartbeat.Cancel();
            try { await connectionMonitor.ConfigureAwait(false); } catch (OperationCanceledException) when (connectionHeartbeat.IsCancellationRequested) { }
        }
        var connections = accelerate && ranges && total >= settings.ParallelThreshold ? 4 : 1;
        var state = new TransferState(label, connections, total);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Monitor()
        {
            while (true) { progress.Report(state.Snapshot()); await Task.Delay(settings.Heartbeat, heartbeat.Token).ConfigureAwait(false); }
        }
        var monitor = Monitor();
        try
        {
            if (ranges && total is long size)
            {
                using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tasks = Enumerable.Range(0, connections).Select(async index =>
                {
                    var begin = size * index / connections; var end = size * (index + 1) / connections - 1; long completed = 0;
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            // Write offset advances only after a successful write, so every retry requests exactly the missing suffix.
                            using var response = await SendAsync(client, uri, begin + completed, end, tag, settings, workers.Token).ConfigureAwait(false);
                            ValidateRange(response, begin + completed, end, size, tag!);
                            await ReadRangeAsync(response, output.SafeFileHandle, begin + completed, end - begin - completed + 1,
                                state, settings, n => completed += n, workers.Token).ConfigureAwait(false);
                            return;
                        }
                        catch (Exception ex) when (attempt < 3 && Transient(ex))
                        {
                            state.Retrying(attempt + 1, true); await Task.Delay(settings.RetryDelay, workers.Token).ConfigureAwait(false);
                        }
                        catch { workers.Cancel(); throw; }
                    }
                }).ToArray();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch
                {
                    // A server can advertise ranges and still ignore them later. Only this case gets a safe full-file restart.
                    var errors = tasks.Where(t => t.Exception != null).SelectMany(t => t.Exception!.Flatten().InnerExceptions).Where(e => e is not OperationCanceledException).ToArray();
                    if (errors.Any(e => e is InvalidDataException)) throw new InvalidDataException("并行连接的文件标识或范围验证失败，已停止下载。");
                    if (errors.Length == 0 || errors.Any(e => e is not RangeUnavailableException)) throw;
                    ct.ThrowIfCancellationRequested(); state.Restart(); output.SetLength(0); heartbeat.Cancel();
                    try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
                    await SingleAsync(client, uri, output, total, limit, label, progress, settings, ct).ConfigureAwait(false);
                    return;
                }
                if (RandomAccess.GetLength(output.SafeFileHandle) != size) throw new InvalidDataException("合并后的下载长度不一致。");
            }
            else
            {
                heartbeat.Cancel();
                try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
                await SingleAsync(client, uri, output, total, limit, label, progress, settings, ct).ConfigureAwait(false);
                return;
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            progress.Report(state.Snapshot(true));
        }
        finally
        {
            heartbeat.Cancel();
            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
        }
    }
    static async Task ReadRangeAsync(HttpResponseMessage response, SafeFileHandle file, long offset, long count,
        TransferState state, Settings settings, Action<int> committed, CancellationToken ct)
    {
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[65536]; long read = 0;
        while (read < count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(settings.IdleTimeout);
            int n;
            try { n = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new TimeoutException("连续 30 秒未收到网络数据。", ex); }
            if (n == 0) break;
            if (read + n > count) throw new InvalidDataException("分段内容超过声明范围。");
            await RandomAccess.WriteAsync(file, buffer.AsMemory(0, n), offset + read, ct).ConfigureAwait(false);
            read += n; committed(n); state.Added(n); state.Receiving();
        }
        if (read != count) throw new IOException("分段连接提前结束。");
    }
    static async Task SingleAsync(HttpClient client, Uri uri, FileStream output, long? expected, long limit, string label,
        IProgress<DownloadProgress> progress, Settings settings, CancellationToken ct)
    {
        var state = new TransferState(label + "（服务器未提供可验证分段，使用完整下载）", 1, expected);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Monitor() { while (true) { progress.Report(state.Snapshot()); await Task.Delay(settings.Heartbeat, heartbeat.Token).ConfigureAwait(false); } }
        var monitor = Monitor();
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested(); output.SetLength(0); state.Restart();
                try
                {
                    using var response = await SendAsync(client, uri, null, null, null, settings, ct).ConfigureAwait(false);
                    RequireStatus(response, HttpStatusCode.OK); Raw(response);
                    if (response.Content.Headers.ContentRange != null) throw new InvalidDataException("下载响应不是完整的原始文件。");
                    var length = response.Content.Headers.ContentLength; Length(length, limit);
                    if (expected.HasValue && length != expected) throw new InvalidDataException("官方文件长度在连接间发生变化，请重新下载。");
                    state.SetLength(length);
                    await ReadAsync(response, output.SafeFileHandle, 0, length, limit, state, settings, ct).ConfigureAwait(false);
                    await output.FlushAsync(ct).ConfigureAwait(false); progress.Report(state.Snapshot(true)); return;
                }
                catch (Exception ex) when (attempt < 3 && Transient(ex))
                {
                    state.Retrying(attempt + 1, false); await Task.Delay(settings.RetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
        finally { heartbeat.Cancel(); try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { } }
    }
}
