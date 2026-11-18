using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PackagePicker;

internal sealed class AssistantUpdateWindow : Window
{
    readonly AssistantUpdates updates;
    readonly TextBlock status = new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,12,0,0) };
    readonly Button check=new(){Content="联网检查版本"}, install=new(){Content="更新并重启…"}, save=new(){Content="仅保存新版…"}, cancel=new(){Content="取消请求",Visibility=Visibility.Collapsed};
    readonly string? unavailable=AssistantUpdateInstall.Unavailable();
    AssistantUpdates.Release? candidate;
    CancellationTokenSource? request;
    internal AssistantUpdateWindow():this(new AssistantUpdates()) { }
    internal AssistantUpdateWindow(AssistantUpdates service)
    {
        updates=service; Title="安装助手更新"; Width=520; SizeToContent=SizeToContent.Height; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Theme.Apply(this);Theme.Primary(install);Theme.Quiet(cancel);
        var body=new StackPanel {Margin=new Thickness(24)}; Content=body;
        body.Children.Add(new TextBlock {Text=$"桌面助手 {AssistantUpdates.Current} · {AssistantUpdates.Rid}", FontSize=18,FontWeight=FontWeights.SemiBold});
        void Note(string text)=>body.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
        Note($"{(AssistantUpdates.Current.Rc==null?"稳定渠道":"候选渠道（含稳定版）")} · GitHub / {AssistantUpdates.Repo}\n仅更新此安装助手。下载后核对 GitHub 提供的 SHA-256，不使用独立发布者签名。");
        if(unavailable!=null) Note(unavailable);
        body.Children.Add(status); status.Text="尚未联网。";
        var actions=new WrapPanel {Margin=new Thickness(0,14,0,0)};body.Children.Add(actions);
        foreach(var b in new[]{check,install,save,cancel}) {b.Margin=new Thickness(0,0,8,4);b.Padding=new Thickness(8,4,8,4);actions.Children.Add(b);}
        install.IsEnabled=save.IsEnabled=false;
        var page=new Button{Content="查看 GitHub 发布页",HorizontalAlignment=HorizontalAlignment.Left}; Theme.Quiet(page); body.Children.Add(page);
        page.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo($"https://github.com/{AssistantUpdates.Repo}/releases"){UseShellExecute=true});}catch{status.Text="未能打开浏览器。";}};
        check.Click+=async (_,_)=>await RunAsync(async token=>{
            candidate=null;status.Text="正在读取 GitHub 发布记录…";candidate=await updates.CheckAsync(token);
            status.Text=candidate==null?"当前渠道没有更高版本。":!candidate.Complete?$"发现 {candidate.Version}，但本架构附件、来源、大小或摘要不符合要求，已拒绝更新。":$"发现桌面助手 {candidate.Version}（{candidate.Rid}）。尚未下载。";
        });
        install.Click+=async (_,_)=>{
            if(candidate is not {Complete:true} release || unavailable!=null || !Confirm($"从 GitHub 下载 {release.Name}（{release.Version}）并验证 SHA-256？\n\n助手随后将关闭，替换以下文件并重新启动。不会请求管理员权限。\n{Environment.ProcessPath}"))return;
            if(await RunAsync(async token=>{status.Text="正在下载并校验；准备重启…";await AssistantUpdateInstall.PrepareAsync(updates,release,token);})) Application.Current.Shutdown();
        };
        save.Click+=async (_,_)=>{
            if(candidate is not {Complete:true} release)return;
            var folder=new OpenFolderDialog {Title="选择保存新版 EXE 的目录"};
            if(folder.ShowDialog(this)!=true || !Confirm($"下载 {release.Version} 并校验 SHA-256？只保存，不运行或覆盖。\n{System.IO.Path.Combine(folder.FolderName,release.Name)}"))return;
            await RunAsync(async token=>{status.Text="正在下载并验证…";var path=await updates.DownloadAsync(release,folder.FolderName,token);status.Text=$"摘要已验证：\n{path}\n关闭旧助手后可手动替换。";});
        };
        cancel.Click+=(_,_)=>request?.Cancel();Closing+=(_,e)=>{if(request!=null){e.Cancel=true;request.Cancel();}};Closed+=(_,_)=>updates.Dispose();
    }
    bool Confirm(string text)=>MessageBox.Show(this,text,Title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;
    async Task<bool> RunAsync(Func<CancellationToken,Task> action)
    {
        if(request!=null)return false;using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(8));request=cts;check.IsEnabled=install.IsEnabled=save.IsEnabled=false;cancel.Visibility=Visibility.Visible;
        try {await action(cts.Token);return true;}
        catch(OperationCanceledException){status.Text="已取消或超时。未替换助手。";}
        catch(Exception ex){status.Text=$"操作未完成：{ex.Message}\n可选择仅保存新版，或查看发布页。";}
        finally {request=null;cancel.Visibility=Visibility.Collapsed;check.IsEnabled=true;save.IsEnabled=candidate?.Complete==true;install.IsEnabled=save.IsEnabled && unavailable==null;}
        return false;
    }
}
