using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PackagePicker;

internal static class AssistantUpdateTests
{
    sealed class Handler(Func<Uri,HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct) { Calls++; return Task.FromResult(reply(req.RequestUri!)); }
    }
    static HttpResponseMessage Ok(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    static DesktopVersion V(string text)=>DesktopVersion.Parse(text)!;
    static readonly byte[] Body="non-executable fixture"u8.ToArray();
    static AssistantUpdates.Release Release(string version)=>new(V(version),AssistantUpdates.Rid,"sha256:"+Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant(),Body.Length);
    static object Asset(AssistantUpdates.Release r)=>new {name=r.Name,browser_download_url=r.Url.AbsoluteUri,digest=r.Digest,size=r.Size};
    static object Row(AssistantUpdates.Release r,object[]? assets=null,bool? pre=null,bool draft=false,string? tag=null)=>new {tag_name=tag??r.Tag,draft,prerelease=pre??(r.Version.Rc!=null),assets=assets??[Asset(r)]};
    static byte[] Rows(params object[] rows)=>JsonSerializer.SerializeToUtf8Bytes(rows);
    static void Assert(bool ok){if(!ok)throw new InvalidOperationException("助手更新测试失败。");}
    static async Task Reject<T>(Func<Task> action) where T:Exception {try{await action();}catch(T){return;}throw new InvalidOperationException("未按预期拒绝。");}
    internal static int Run(){try{Test().GetAwaiter().GetResult();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 7;}}
    internal static async Task<bool> SmokeWindow(System.Windows.Window owner)
    {
        var r=Release("99.1.2"); var handler=new Handler(_=>Ok(Rows(Row(r))));var window=new AssistantUpdateWindow(new AssistantUpdates(handler,V("1.0.4"))){Owner=owner,ShowActivated=false};
        try {
            window.Show();window.UpdateLayout();var body=(System.Windows.Controls.StackPanel)window.Content;
            var buttons=body.Children.OfType<System.Windows.Controls.WrapPanel>().Single().Children.OfType<System.Windows.Controls.Button>().ToArray();
            var check=buttons.Single(b=>Equals(b.Content,"联网检查版本"));var install=buttons.Single(b=>Equals(b.Content,"更新并重启…"));var save=buttons.Single(b=>Equals(b.Content,"仅保存新版…"));
            if(handler.Calls!=0 || !check.IsEnabled || save.IsEnabled || install.IsEnabled)return false;
            check.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));using var t=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!check.IsEnabled)await Task.Delay(10,t.Token);
            return handler.Calls==1 && save.IsEnabled && install.IsEnabled==(AssistantUpdateInstall.Unavailable()==null) && body.Children.OfType<System.Windows.Controls.TextBlock>().Any(x=>x.Text.Contains("发现桌面助手 99.1.2"));
        }finally{window.Close();}
    }
    static async Task Test()
    {
        var dir=Directory.CreateTempSubdirectory();var ct=CancellationToken.None;
        try {
            var r=Release("1.0.2-rc1");var stable=Release("1.0.2");byte[] listing=Rows(Row(stable),Row(r)); string redirect="https://release-assets.githubusercontent.com/example";byte[] body=Body.ToArray();
            var handler=new Handler(uri=>{
                if(uri.AbsoluteUri==$"https://api.github.com/repos/{AssistantUpdates.Repo}/releases?per_page=30&page=1")return Ok(listing);
                if(uri==r.Url){var response=new HttpResponseMessage(HttpStatusCode.Found);response.Headers.Location=new Uri(redirect);return response;}
                if(uri.AbsoluteUri=="https://release-assets.githubusercontent.com/example")return Ok(body);
                throw new InvalidOperationException("意外请求："+uri);
            });
            using var client=new AssistantUpdates(handler,V("1.0.1-rc2")); Assert(await client.CheckAsync(ct)==stable);
            listing=Rows(Row(stable,pre:true),Row(r),Row(Release("9.0.0"),draft:true),Row(stable,tag:"v1.1.0"),Row(stable,tag:"desktop-v09.0.0")); Assert(await client.CheckAsync(ct)==r);
            using(var stableClient=new AssistantUpdates(new Handler(_=>Ok(listing)),V("1.0.1")))Assert(await stableClient.CheckAsync(ct)==null);
            using(var promote=new AssistantUpdates(new Handler(_=>Ok(Rows(Row(stable)))),r.Version))Assert(await promote.CheckAsync(ct)==stable);
            Assert(V("1.0.2-rc10").CompareTo(V("1.0.2-rc2"))>0 && V("1.0.3-rc1").CompareTo(stable.Version)>0);
            foreach(var invalid in new[]{"1.0.0-rc0","1.0.0-rc01","1.0.0-rc.1","v1.0.0","1.0","1.0.0.0","1.0.0+build"})Assert(DesktopVersion.Parse(invalid)==null);
            foreach(var assets in new object[][]{[],[Asset(r),Asset(r)],[Asset(r with{Rid="wrong"})],[new {name=r.Name,browser_download_url="https://example.invalid/x",digest=r.Digest,size=r.Size}],[Asset(r with{Digest=null})],[Asset(r with{Size=0})],[Asset(r with{Digest="sha256:"+new string('x',64)})]}) {
                listing=Rows(Row(r,assets));var bad=await client.CheckAsync(ct);Assert(bad is {Complete:false});int count=handler.Calls;await Reject<InvalidDataException>(()=>client.DownloadAsync(bad!,dir.FullName,ct));Assert(handler.Calls==count);
            }
            var file=await client.DownloadAsync(r,dir.FullName,ct);Assert(File.ReadAllBytes(file).SequenceEqual(body));await Reject<IOException>(()=>client.DownloadAsync(r,dir.FullName,ct));File.Delete(file);
            body[0]^=1;await Reject<InvalidDataException>(()=>client.DownloadAsync(r,dir.FullName,ct));
            foreach(var bad in new[]{"https://example.invalid/","http://release-assets.githubusercontent.com/foo","https://release-assets.githubusercontent.com.evil.invalid/foo"}){redirect=bad;await Reject<HttpRequestException>(()=>client.DownloadAsync(r,dir.FullName,ct));}
            Assert(!dir.EnumerateFileSystemInfos().Any());
        }finally{dir.Delete(true);}
    }
#if UPDATE_FIXTURE
    // Compiled only into disposable OLD integration fixtures, never into either distributed RID.
    internal static int LocalFixture()
    {
        var root=Path.GetDirectoryName(Environment.ProcessPath!)!;
        try {
            if(!Path.GetFileName(root).StartsWith("PickerE2E-",StringComparison.Ordinal) || !string.Equals(Path.GetDirectoryName(root),Path.TrimEndingDirectorySeparator(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))return 9;
            var file=Path.Combine(root,"fixture","ChatGPT-PackagePicker.exe");using var stream=File.OpenRead(file);
            var r=new AssistantUpdates.Release(V("1.0.4"),AssistantUpdates.Rid,"sha256:"+Convert.ToHexString(SHA256.HashData(stream)),stream.Length);
            using var client=new AssistantUpdates(new Handler(uri=>uri==r.Url?new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(file))}:Ok(Rows(Row(r)))),AssistantUpdates.Current);
            var release=client.CheckAsync(default).GetAwaiter().GetResult(); Assert(release==r);AssistantUpdateInstall.PrepareAsync(client,r,default).GetAwaiter().GetResult();return 0;
        }catch(Exception e){File.WriteAllText(Path.Combine(root,"fixture-error.txt"),e.ToString());return 9;}
    }
#endif
}
