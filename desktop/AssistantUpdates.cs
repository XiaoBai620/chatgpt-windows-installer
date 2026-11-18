using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PackagePicker;

internal sealed record DesktopVersion(Version Core, int? Rc) : IComparable<DesktopVersion>
{
    public override string ToString() => Core + (Rc is int rc ? "-rc" + rc.ToString(CultureInfo.InvariantCulture) : "");
    public int CompareTo(DesktopVersion? other) => other == null ? 1 : Core != other.Core ? Core.CompareTo(other.Core) : Rc == other.Rc ? 0 : Rc == null ? 1 : other.Rc == null ? -1 : Rc.Value.CompareTo(other.Rc.Value);
    internal static DesktopVersion? Parse(string? value)
    {
        if (value == null) return null;
        var parts = value.Split("-rc", StringSplitOptions.None);
        if (parts.Length > 2 || !Version.TryParse(parts[0], out var core) || core.Build < 0 || core.Revision >= 0) return null;
        int? rc = null;
        if (parts.Length == 2) { if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) return null; rc = n; }
        var version = new DesktopVersion(core,rc);
        return version.ToString() == value ? version : null;
    }
}

internal sealed class AssistantUpdates : IDisposable
{
    internal const string Repo = "XiaoBai620/chatgpt-windows-installer";
    internal const long MaxExe = 256L * 1024 * 1024;
    internal record Release(DesktopVersion Version, string Rid, string? Digest, long Size)
    {
        internal string Tag => "desktop-v" + Version;
        internal string Name => $"ChatGPT-PackagePicker-{Rid}.exe";
        internal Uri Url => new($"https://github.com/{Repo}/releases/download/{Tag}/{Name}");
        internal bool Complete => ValidDigest(Digest) && Size is > 0 and <= MaxExe;
    }
    internal static bool ValidDigest(string? s) => s is { Length: 71 } && s.StartsWith("sha256:",StringComparison.Ordinal) && s.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    internal static DesktopVersion Current { get; } = DesktopVersion.Parse(typeof(AssistantUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]) ?? throw new InvalidDataException("助手版本格式无效。");
    internal static string Rid => RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64", _ => throw new PlatformNotSupportedException("不支持此助手架构。") };
    readonly HttpClient http;
    readonly DesktopVersion current;
    internal AssistantUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },Current) { }
    internal AssistantUpdates(HttpMessageHandler handler, DesktopVersion current)
    {
        this.current=current;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-PackagePicker/" + current);
    }
    bool Eligible(DesktopVersion v) => v.CompareTo(current)>0 && (current.Rc != null || v.Rc == null);
    internal async Task<Release?> CheckAsync(CancellationToken ct)
    {
        Release? best = null;
        for (int page=1;page<=10;page++) {
            using var res=await GetAsync(new Uri($"https://api.github.com/repos/{Repo}/releases?per_page=30&page={page}"),false,ct);
            using var buf=new MemoryStream(); await CopyAsync(res,buf,4*1024*1024,ct);
            using var json=JsonDocument.Parse(buf.ToArray()); var rows=json.RootElement;
            foreach (var row in rows.EnumerateArray()) {
                var tag=row.GetProperty("tag_name").GetString();
                if (row.GetProperty("draft").GetBoolean() || tag?.StartsWith("desktop-v",StringComparison.Ordinal)!=true || DesktopVersion.Parse(tag[9..]) is not {} v || !Eligible(v) || (v.Rc!=null)!=row.GetProperty("prerelease").GetBoolean() || best!=null && v.CompareTo(best.Version)<=0) continue;
                var item=new Release(v,Rid,null,0);
                var matches=row.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==item.Name).ToArray();
                if (matches is [var a] && a.TryGetProperty("browser_download_url",out var url) && url.GetString()==item.Url.AbsoluteUri && a.TryGetProperty("digest",out var d) && d.ValueKind==JsonValueKind.String && a.TryGetProperty("size",out var s) && s.TryGetInt64(out var size)) item=item with {Digest=d.GetString(),Size=size};
                best=item;
            }
            if (rows.GetArrayLength()<30) return best;
        }
        throw new InvalidDataException("发布列表超过检查范围，无法确认最新版本。");
    }
    internal async Task<string> DownloadAsync(Release r,string folder,CancellationToken ct)
    {
        if (!r.Complete || r.Rid!=Rid || !Eligible(r.Version)) throw new InvalidDataException("缺少可信摘要或版本不适用。");
        var final=Path.Combine(folder,r.Name); if(File.Exists(final)) throw new IOException("文件已存在，请选择空目录。不会覆盖已有文件。");
        using var exe=await GetAsync(r.Url,true,ct); var temp=Path.Combine(folder,$".{Guid.NewGuid():N}.partial"); var owned=false;
        try {
            await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,81920,true)) {
                owned=true; await CopyAsync(exe,stream,r.Size,ct); stream.Position=0;
                if(stream.Length!=r.Size || !Matches(await SHA256.HashDataAsync(stream,ct),r.Digest!)) throw new InvalidDataException("附件长度或 SHA-256 与 GitHub 发布记录不符，已拒绝。");
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); File.Move(temp,final); owned=false; return final;
        } finally { if(owned) File.Delete(temp); }
    }
    internal static bool Matches(byte[] bytes,string digest) => ValidDigest(digest) && Convert.ToHexString(bytes).Equals(digest[7..],StringComparison.OrdinalIgnoreCase);
    async Task<HttpResponseMessage> GetAsync(Uri url,bool asset,CancellationToken ct)
    {
        for(int hops=0;hops<=3;hops++) {
            var res=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,ct);
            if(res.StatusCode==HttpStatusCode.OK) return res;
            using(res) {
                if(asset && (int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is {IsAbsoluteUri:true} next && next.Scheme=="https" && next.IsDefaultPort && next.UserInfo=="" && next.IdnHost=="release-assets.githubusercontent.com") { url=next; continue; }
                throw new HttpRequestException($"GitHub 请求失败或重定向不可信（HTTP {(int)res.StatusCode}）。");
            }
        }
        throw new HttpRequestException("GitHub 重定向次数超限。");
    }
    static async Task CopyAsync(HttpResponseMessage msg,Stream output,long limit,CancellationToken ct)
    {
        var expected=msg.Content.Headers.ContentLength; if(expected>limit) throw new InvalidDataException("响应超过大小限制。");
        using var stream=await msg.Content.ReadAsStreamAsync(ct); var buf=new byte[81920]; long total=0; int n;
        while((n=await stream.ReadAsync(buf,ct))>0) { if((total+=n)>limit) throw new InvalidDataException("响应超过大小限制。"); await output.WriteAsync(buf.AsMemory(0,n),ct); }
        if(total==0 || expected.HasValue && expected!=total) throw new InvalidDataException("响应为空或未完整接收。");
    }
    public void Dispose()=>http.Dispose();
}
