using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PackagePicker;

internal static class AssistantUpdateTests
{
    sealed class Handler(Func<Uri,HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) { Calls++; return Task.FromResult(reply(req.RequestUri!)); }
    }
    static void Assert(bool ok) { if (!ok) throw new InvalidOperationException("助手更新测试失败。"); }
    static async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("未按预期拒绝。"); }
    internal static int Run() { try { Test().GetAwaiter().GetResult(); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 7; } }
    // Exercise actual WPF controls on the STA dispatcher; no live network or executable fixture.
    internal static async Task<bool> SmokeWindow(System.Windows.Window owner)
    {
        var name = $"ChatGPT-PackagePicker-{AssistantUpdates.Rid}.exe";
        var rows = JsonSerializer.SerializeToUtf8Bytes(new[]{new {tag_name="desktop-v99.1.2",draft=false,prerelease=false,assets=new[]{new{name},new{name=name+".sig"}}}});
        var handler = new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(rows)});
        var window = new AssistantUpdateWindow(new AssistantUpdates(handler,null)){ Owner=owner, ShowActivated=false };
        try {
            window.Show(); window.UpdateLayout();
            var body = (System.Windows.Controls.StackPanel)window.Content;
            var actions = body.Children.OfType<System.Windows.Controls.WrapPanel>().Single();
            var buttons = actions.Children.OfType<System.Windows.Controls.Button>().ToArray();
            var check = buttons.Single(b=>Equals(b.Content,"联网检查版本")); var save = buttons.Single(b=>Equals(b.Content,"下载并验证 EXE…"));
            if (handler.Calls != 0 || !check.IsEnabled || save.IsEnabled || !body.Children.OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text.Contains("不可下载"))) return false;
            check.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!check.IsEnabled) await Task.Delay(10,timeout.Token);
            return handler.Calls==1 && !save.IsEnabled && body.Children.OfType<System.Windows.Controls.TextBlock>().Any(t=>t.Text.Contains("发现桌面助手 99.1.2"));
        } catch { return false; } finally { window.Close(); }
    }
    static async Task Test()
    {
        var dir = Directory.CreateTempSubdirectory(); var ct = CancellationToken.None;
        try {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Ephemeral test fixture; never persisted or used by the UI.
            var pub = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            var expected = new AssistantUpdates.Release(new Version(12,10,0),AssistantUpdates.Rid,true);
            var body = "non-executable test bytes"u8.ToArray();
            var payload = $"XiaoBai620/chatgpt-windows-installer\ndesktop-v12.10.0\n{expected.Name}\n{Convert.ToHexString(SHA256.HashData(body))}\n";
            var signature = signer.SignData(Encoding.UTF8.GetBytes(payload),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            object Row(string tag, bool draft=false, bool pre=false, bool complete=true) => new { tag_name=tag, draft, prerelease=pre, assets=(complete?new[]{expected.Name,expected.Name+".sig"}:new[]{"ChatGPT.msix"}).Select(n=>new {name=n,browser_download_url="https://example.invalid/ignored"}) };
            byte[] listing = JsonSerializer.SerializeToUtf8Bytes(new[]{Row("v1.1.0"),Row("26.1111.2228.0"),Row("desktop-v99.0.0",pre:true),Row("desktop-v88.0.0",draft:true),Row("desktop-v99.0.0-rc1"),Row("desktop-v012.99.0"),Row("desktop-v12.9.0"),Row(expected.Tag)});
            static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
            static HttpResponseMessage Redirect(string uri) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(uri); return r; }
            var redirect = "https://release-assets.githubusercontent.com/example";
            var handler = new Handler(uri => {
                if (uri.AbsoluteUri == $"https://api.github.com/repos/{AssistantUpdates.Repo}/releases?per_page=30&page=1") return Ok(listing);
                if (uri == expected.Url(".sig")) return Ok(signature);
                if (uri == expected.Url("")) return Redirect(redirect);
                if (uri.AbsoluteUri == "https://release-assets.githubusercontent.com/example") return Ok(body);
                throw new InvalidOperationException("意外请求："+uri);
            });
            foreach (var missing in new[]{"","broken",Convert.ToBase64String(new byte[64])}) {
                var never = new Handler(_ => throw new InvalidOperationException("无公钥时不应下载")); using var off = new AssistantUpdates(never,missing);
                Assert(!off.CanDownload); await Reject<InvalidDataException>(()=>off.DownloadAsync(expected,dir.FullName,ct)); Assert(never.Calls == 0);
            }
            using var client = new AssistantUpdates(handler,pub);
            Assert(client.CanDownload && await client.CheckAsync(ct) == expected && handler.Calls == 1);
            listing = JsonSerializer.SerializeToUtf8Bytes(new[]{Row("v1.1.0"),Row("desktop-v"+AssistantUpdates.Current),Row("desktop-v0.1.0")});
            Assert(await client.CheckAsync(ct) == null);
            listing = JsonSerializer.SerializeToUtf8Bytes(new[]{Row(expected.Tag,complete:false)});
            var incomplete = await client.CheckAsync(ct); Assert(incomplete is {Complete:false});
            foreach (var bad in new[]{incomplete!,expected with { Version=AssistantUpdates.Current },expected with { Rid="other" }}) await Reject<InvalidDataException>(()=>client.DownloadAsync(bad,dir.FullName,ct));
            var file = await client.DownloadAsync(expected,dir.FullName,ct); Assert(File.ReadAllBytes(file).SequenceEqual(body));
            await Reject<IOException>(()=>client.DownloadAsync(expected,dir.FullName,ct)); Assert(File.ReadAllBytes(file).SequenceEqual(body)); File.Delete(file);
            body[0] ^= 1; await Reject<InvalidDataException>(()=>client.DownloadAsync(expected,dir.FullName,ct)); body[0] ^= 1;
            signature = signer.SignData(Encoding.UTF8.GetBytes(payload.Replace("12.10.0","12.9.0")),HashAlgorithmName.SHA256); await Reject<InvalidDataException>(()=>client.DownloadAsync(expected,dir.FullName,ct));
            foreach (var invalid in new[]{"http://release-assets.githubusercontent.com/example","https://release-assets.githubusercontent.com.evil.invalid/test","https://evil.invalid/"}) { redirect = invalid; await Reject<HttpRequestException>(()=>client.DownloadAsync(expected,dir.FullName,ct)); }
            signature = new byte[65]; await Reject<InvalidDataException>(()=>client.DownloadAsync(expected,dir.FullName,ct));
            Assert(!dir.EnumerateFileSystemInfos().Any());
        } finally { dir.Delete(true); }
    }
}
