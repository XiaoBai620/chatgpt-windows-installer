using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml;

namespace PackagePicker;

internal static class Acquisition
{
    private static readonly Uri Source = new("https://persistent.oaistatic.com/codex-app-prod/");
    private const long PackageLimit = 4L * 1024 * 1024 * 1024;
    private const long LicenseLimit = 4 * 1024 * 1024;
    private const int MaxRedirects = 3;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<string> DownloadAsync(string cache, IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("官方下载仅支持 x64 或 arm64 系统。")
        };
        var root = Directory.CreateDirectory(Path.GetFullPath(cache)).FullName;
        // Keep an actual .msix suffix for WinVerifyTrust, but hide incomplete files from the catalog's top-level scan.
        var stage = Directory.CreateDirectory(Path.Combine(root, ".acquire-" + Guid.NewGuid().ToString("N"))).FullName;
        var package = Path.Combine(stage, "ChatGPT.msix");
        var license = Path.ChangeExtension(package, ".xml");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        var ct = deadline.Token;
        try
        {
            await FetchAsync($"ChatGPT-{arch}.msix", package, PackageLimit, "安装包", progress, ct).ConfigureAwait(false);
            progress.Report("正在验证安装包身份、兼容性与 Windows 签名…");
            var row = Catalog.Inspect(package, arch, Environment.OSVersion.Version, Trust.Verify);
            ct.ThrowIfCancellationRequested();
            if (!row.Eligible || row.Version is null) throw new InvalidDataException("下载的安装包不可用：" + row.State);
            await FetchAsync("ChatGPT-License.xml", license, LicenseLimit, "离线许可证", progress, ct).ConfigureAwait(false);
            ValidateLicense(license);
            var target = Path.Combine(root, $"ChatGPT-{arch}-{row.Version}.msix");
            var targetLicense = Path.ChangeExtension(target, ".xml");
            ct.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                // Only reuse the exact pair just obtained and checked. A version number alone is not an integrity check.
                if (!await SameAsync(license, targetLicense, ct).ConfigureAwait(false) ||
                    !await SameAsync(package, target, ct).ConfigureAwait(false))
                    throw new IOException("缓存里已有不同内容的同版本文件，已保留原文件。请另选缓存目录。");
                ct.ThrowIfCancellationRequested();
                return target;
            }
            Publish(package, license, target, ct);
            return target;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("官方下载超时，请稍后重试。", ex);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private static void Publish(string package, string license, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sidecar = Path.ChangeExtension(target, ".xml");
        File.Move(license, sidecar, overwrite: false);
        try
        {
            token.ThrowIfCancellationRequested();
            File.Move(package, target, overwrite: false); // Same-volume rename is the commit point.
        }
        catch { File.Delete(sidecar); throw; }
    }

    private static async Task<bool> SameAsync(string source, string existing, CancellationToken ct)
    {
        if (!File.Exists(existing)) return false;
        using var a = File.OpenRead(source);
        using var b = File.OpenRead(existing);
        if (a.Length != b.Length) return false;
        var ha = await SHA256.HashDataAsync(a, ct).ConfigureAwait(false);
        var hb = await SHA256.HashDataAsync(b, ct).ConfigureAwait(false);
        return ha.AsSpan().SequenceEqual(hb);
    }

    private static bool Allowed(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == 443 && uri.UserInfo.Length == 0 && uri.IdnHost.Equals(Source.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static async Task FetchAsync(string name, string destination, long limit, string label, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("正在下载" + label + "…");
        var uri = new Uri(Source, name);
        for (int redirects = 0; ; redirects++)
        {
            if (!Allowed(uri)) throw new InvalidDataException("下载地址不是允许的官方 HTTPS 来源。");
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= MaxRedirects || response.Headers.Location is not Uri location || !Uri.TryCreate(uri, location, out var next))
                    throw new InvalidDataException("下载重定向次数过多或缺少有效目标。");
                uri = next;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"{label}下载失败：HTTP {(int)response.StatusCode}。");
            long? length = response.Content.Headers.ContentLength;
            if (response.Content.Headers.ContentRange != null || response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("下载响应不是完整的原始文件。");
            using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            var timer = Stopwatch.StartNew();
            await CopyBoundedAsync(input, output, length, limit, n =>
            {
                if (timer.ElapsedMilliseconds < 750) return;
                progress.Report($"正在下载{label}：{n / 1048576:N0} MiB" + (length.HasValue ? $" / {length.Value / 1048576:N0} MiB" : ""));
                timer.Restart();
            }, ct).ConfigureAwait(false);
            return;
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long? expected, long limit, Action<long> report, CancellationToken ct)
    {
        if (expected is <= 0 || expected > limit) throw new InvalidDataException("下载文件大小超出允许范围。");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long total = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > (expected ?? limit)) throw new InvalidDataException("下载内容超过长度限制。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                report(total);
            }
            if (total == 0 || (expected.HasValue && total != expected.Value)) throw new InvalidDataException("下载文件为空或不完整。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static void ValidateLicense(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is 0 or > LicenseLimit) throw new InvalidDataException("许可证为空或过大。");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = LicenseLimit };
        using var reader = XmlReader.Create(stream, settings);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "License")
            throw new InvalidDataException("离线许可证的 XML 根元素不是 License。");
        while (reader.Read()) { } // Parse to EOF: reject malformed descendants and extra roots.
    }

    // Returns zero on success; no network, trust-store or deployment dependencies.
    internal static int SelfTest()
    {
        static bool Rejects<T>(Action action) where T : Exception
        {
            try { action(); return false; } catch (T) { return true; }
        }
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var xml = Path.Combine(dir.FullName, "input.xml");
            File.WriteAllText(xml, "<x:License xmlns:x='urn:test'/>");
            ValidateLicense(xml);
            File.WriteAllText(xml, "<NotLicense/>");
            if (!Rejects<InvalidDataException>(() => ValidateLicense(xml))) return 11;
            foreach (var bad in new[] { "<!DOCTYPE License><License/>", "<License/><extra/>" })
            {
                File.WriteAllText(xml, bad);
                if (!Rejects<XmlException>(() => ValidateLicense(xml))) return 12;
            }
            if (!Allowed(new Uri(Source, "ChatGPT-x64.msix")) || new[] { "http://persistent.oaistatic.com/a", "https://persistent.oaistatic.com.example/a", "https://persistent.oaistatic.com:8443/a", "https://user@persistent.oaistatic.com/a" }.Any(s => Allowed(new Uri(s)))) return 13;
            static void Copy(int bytes, long? length, CancellationToken ct = default)
            {
                using var input = new MemoryStream(new byte[bytes]);
                CopyBoundedAsync(input, Stream.Null, length, 4, _ => { }, ct).GetAwaiter().GetResult();
            }
            Copy(4, 4);
            Copy(2, null);
            if (!Rejects<InvalidDataException>(() => Copy(3, 4)) || !Rejects<InvalidDataException>(() => Copy(5, null)) || !Rejects<InvalidDataException>(() => Copy(0, null))) return 14;
            if (!Rejects<OperationCanceledException>(() => Copy(4, 4, new CancellationToken(true)))) return 15;
            var package = Path.Combine(dir.FullName, "input.msix");
            var target = Path.Combine(dir.FullName, "cached.msix");
            File.WriteAllText(package, "new");
            File.WriteAllText(target, "existing");
            if (!Rejects<IOException>(() => Publish(package, xml, target, default)) || File.Exists(Path.ChangeExtension(target, ".xml")) || File.ReadAllText(target) != "existing") return 16;
            File.Delete(target);
            File.WriteAllText(xml, "<License/>");
            Publish(package, xml, target, default);
            ValidateLicense(Path.ChangeExtension(target, ".xml"));
            return File.ReadAllText(target) == "new" ? 0 : 17;
        }
        finally { dir.Delete(true); }
    }
}
