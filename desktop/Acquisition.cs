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
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    static Acquisition() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-PackagePicker/1.0");

    sealed class TextProgress(IProgress<string> progress) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => progress.Report(value.Text); }

    internal static Task<string> DownloadAsync(string cache, IProgress<string> progress, CancellationToken token) => DownloadAsync(cache, new TextProgress(progress), token);

    internal static async Task<string> DownloadAsync(string cache, IProgress<DownloadProgress> progress, CancellationToken token, bool accelerate = true)
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
        deadline.CancelAfter(TimeSpan.FromHours(8));
        var ct = deadline.Token;
        try
        {
            await PackageDownload.FetchAsync(Client, new Uri(Source, $"ChatGPT-{arch}.msix"), package, PackageLimit, "安装包", progress, ct, accelerate).ConfigureAwait(false);
            progress.Report(new DownloadProgress("安装包已下载，正在验证身份、兼容性与 Windows 签名…", Phase: DownloadPhase.Verifying));
            var row = Catalog.Inspect(package, arch, Environment.OSVersion.Version, Trust.Verify);
            ct.ThrowIfCancellationRequested();
            if (!row.Eligible || row.Version is null) throw new InvalidDataException("下载的安装包不可用：" + row.State);
            await PackageDownload.FetchAsync(Client, new Uri(Source, "ChatGPT-License.xml"), license, LicenseLimit, "离线许可证", progress, ct, false).ConfigureAwait(false);
            progress.Report(new DownloadProgress("许可证已下载，正在检查 XML 格式…", Phase: DownloadPhase.Verifying));
            ValidateLicense(license);
            var target = Path.Combine(root, $"ChatGPT-{arch}-{row.Version}.msix");
            var targetLicense = Path.ChangeExtension(target, ".xml");
            ct.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                progress.Report(new DownloadProgress("正在比对已有版本，确认安装包与许可证一致…", Phase: DownloadPhase.Saving));
                // Only reuse the exact pair just obtained and checked. A version number alone is not an integrity check.
                if (!await SameAsync(license, targetLicense, ct).ConfigureAwait(false) ||
                    !await SameAsync(package, target, ct).ConfigureAwait(false))
                    throw new IOException("缓存里已有不同内容的同版本文件，已保留原文件。请另选缓存目录。");
                ct.ThrowIfCancellationRequested();
                return target;
            }
            progress.Report(new DownloadProgress("下载与校验已完成，正在保存安装包和许可证…", Phase: DownloadPhase.Saving));
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
