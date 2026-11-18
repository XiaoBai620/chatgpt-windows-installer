using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackagePicker;

// A desktop release is an independent namespace. Never interpret GitHub's generic 'latest'
// release, an MSIX version, or a release-provided URL as a desktop update instruction.
internal sealed class AssistantUpdates : IDisposable
{
    internal const string Repo = "XiaoBai620/chatgpt-windows-installer";
    const long MaxExe = 256L * 1024 * 1024;
    internal record Release(Version Version, string Rid, bool Complete)
    {
        internal string Tag => "desktop-v" + Version;
        internal string Name => $"ChatGPT-PackagePicker-{Rid}.exe";
        internal Uri Url(string suffix) => new($"https://github.com/{Repo}/releases/download/{Tag}/{Name}{suffix}");
    }
    readonly HttpClient http;
    readonly string? publicKey;
    internal static Version Current { get { var v = typeof(AssistantUpdates).Assembly.GetName().Version!; return new Version(v.Major, v.Minor, v.Build); } }
    internal static string Rid => RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => throw new PlatformNotSupportedException("不支持此助手架构。") };
    internal AssistantUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
        typeof(AssistantUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DesktopUpdatePublicKey").Value) { }
    internal AssistantUpdates(HttpMessageHandler handler, string? key)
    {
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-PackagePicker/" + Current);
        publicKey = key;
    }
    ECDsa OpenKey()
    {
        var key = ECDsa.Create();
        try {
            var bytes = Convert.FromBase64String(publicKey ?? "");
            key.ImportSubjectPublicKeyInfo(bytes, out int used);
            if (used != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException();
            return key;
        } catch { key.Dispose(); throw new InvalidDataException("此构建未配置有效的助手更新公钥，仅允许检查版本。"); }
    }
    internal bool CanDownload { get { try { using var key = OpenKey(); return true; } catch (InvalidDataException) { return false; } } }
    internal async Task<Release?> CheckAsync(CancellationToken ct)
    {
        Release? best = null;
        for (int page = 1; page <= 10; page++) {
            using var res = await GetAsync(new Uri($"https://api.github.com/repos/{Repo}/releases?per_page=30&page={page}"), false, ct);
            using var buf = new MemoryStream();
            await CopyAsync(res, buf, 4 * 1024 * 1024, ct);
            using var json = JsonDocument.Parse(buf.ToArray());
            var rows = json.RootElement;
            foreach (var r in rows.EnumerateArray()) {
                var tag = r.GetProperty("tag_name").GetString();
                if (r.GetProperty("draft").GetBoolean() || r.GetProperty("prerelease").GetBoolean() || tag?.StartsWith("desktop-v", StringComparison.Ordinal) != true ||
                    !Version.TryParse(tag[9..], out var v) || v.Build < 0 || v.Revision >= 0 || tag != "desktop-v" + v || v <= Current || (best != null && v <= best.Version)) continue;
                var item = new Release(v, Rid, false);
                var assets = r.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();
                best = item with { Complete = assets.Count(n => n == item.Name) == 1 && assets.Count(n => n == item.Name + ".sig") == 1 };
            }
            if (rows.GetArrayLength() < 30) return best;
        }
        throw new InvalidDataException("发布列表超过检查范围，无法确认最新版本。");
    }
    internal static byte[] Payload(Release r, byte[] digest) => Encoding.UTF8.GetBytes($"{Repo}\n{r.Tag}\n{r.Name}\n{Convert.ToHexString(digest)}\n");
    internal async Task<string> DownloadAsync(Release r, string folder, CancellationToken ct)
    {
        using var key = OpenKey(); // Must precede *any* asset request or filesystem change.
        if (!r.Complete || r.Version <= Current || r.Version.Build < 0 || r.Version.Revision >= 0 || r.Rid != Rid) throw new InvalidDataException("不适用的助手更新。");
        var final = Path.Combine(folder, $"ChatGPT-PackagePicker-{r.Version}-{Rid}.exe");
        if (File.Exists(final)) throw new IOException("目标文件已存在，请选择其他目录；不会覆盖已有文件。");
        using var sig = await GetAsync(r.Url(".sig"), true, ct); using var buffer = new MemoryStream();
        await CopyAsync(sig, buffer, 64, ct);
        if (buffer.Length != 64) throw new InvalidDataException("发布签名格式无效。");
        using var exe = await GetAsync(r.Url(""), true, ct);
        var temp = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
        var owned = false;
        try {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true)) {
                owned = true;
                await CopyAsync(exe, stream, MaxExe, ct);
                stream.Position = 0;
                var digest = await SHA256.HashDataAsync(stream, ct);
                if (!key.VerifyData(Payload(r, digest), buffer.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new InvalidDataException("助手发布签名不匹配，已拒绝保存 EXE。");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, final); owned = false;
            return final;
        } finally { if (owned) File.Delete(temp); }
    }
    async Task<HttpResponseMessage> GetAsync(Uri url, bool asset, CancellationToken ct)
    {
        for (int hops = 0; hops <= 3; hops++) {
            var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode == HttpStatusCode.OK) return res;
            using (res) {
                if (asset && (int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { IsAbsoluteUri: true } next &&
                    next.Scheme == "https" && next.IsDefaultPort && next.UserInfo == "" && next.IdnHost == "release-assets.githubusercontent.com") { url = next; continue; }
                throw new HttpRequestException($"GitHub 请求失败或发生非预期重定向（HTTP {(int)res.StatusCode}）。");
            }
        }
        throw new HttpRequestException("GitHub 重定向次数超限。");
    }
    static async Task CopyAsync(HttpResponseMessage msg, Stream output, long limit, CancellationToken ct)
    {
        var expected = msg.Content.Headers.ContentLength;
        if (expected > limit) throw new InvalidDataException("响应超过大小限制。");
        using var stream = await msg.Content.ReadAsStreamAsync(ct);
        var buf = new byte[81920]; long total = 0; int n;
        while ((n = await stream.ReadAsync(buf, ct)) > 0) {
            if ((total += n) > limit) throw new InvalidDataException("响应超过大小限制。");
            await output.WriteAsync(buf.AsMemory(0, n), ct);
        }
        if (total == 0 || expected.HasValue && expected != total) throw new InvalidDataException("响应为空或未完整接收。");
    }
    public void Dispose() => http.Dispose();
}
