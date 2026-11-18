using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PackagePicker;

internal sealed class AssistantUpdateWindow : Window
{
    readonly AssistantUpdates updates;
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
    readonly Button check = new() { Content = "联网检查版本" }, save = new() { Content = "下载并验证 EXE…", IsEnabled = false }, cancel = new() { Content = "取消请求", Visibility = Visibility.Collapsed };
    AssistantUpdates.Release? candidate;
    CancellationTokenSource? request;

    internal AssistantUpdateWindow() : this(new AssistantUpdates()) { }
    internal AssistantUpdateWindow(AssistantUpdates service)
    {
        updates = service;
        Title = "安装助手更新"; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(20) }; Content = body;
        body.Children.Add(new TextBlock { Text = $"桌面助手 {AssistantUpdates.Current} · {AssistantUpdates.Rid}", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = $"来源：GitHub / {AssistantUpdates.Repo}\n仅检查安装助手，不更新 ChatGPT，也不运行历史脚本。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
        if (!updates.CanDownload) body.Children.Add(new TextBlock { Text = "此构建未配置有效的发布公钥：可检查版本，不可下载。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
        body.Children.Add(status); status.Text = "尚未联网。";
        var actions = new WrapPanel { Margin = new Thickness(0,14,0,0) }; body.Children.Add(actions);
        foreach (var button in new[]{check,save,cancel}) { button.Padding = new Thickness(8,4,8,4); button.Margin = new Thickness(0,0,8,0); actions.Children.Add(button); }
        check.Click += async (_,_) => await RunAsync(async token => {
            candidate = null; status.Text = "正在读取 GitHub 公开发行记录…";
            candidate = await updates.CheckAsync(token);
            status.Text = candidate == null ? "未发现比当前助手更新的稳定桌面版。" : !candidate.Complete ? $"发现 {candidate.Version}，但缺少此架构的 EXE 或签名，不可下载。" : $"发现桌面助手 {candidate.Version}（{candidate.Rid}）。尚未下载或验证。";
        });
        save.Click += async (_,_) => {
            if (candidate is not { Complete: true } release || !updates.CanDownload) return;
            var folder = new OpenFolderDialog { Title = "选择保存已验证助手 EXE 的目录" };
            if (folder.ShowDialog(this) != true || MessageBox.Show(this,$"下载桌面助手 {release.Version}（{release.Rid}）到：\n{folder.FolderName}\n\n仅校验签名并保存，不会启动、覆盖或安装。继续？",Title,MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await RunAsync(async token => {
                status.Text = "正在下载并验证助手发布签名…";
                var path = await updates.DownloadAsync(release,folder.FolderName,token);
                status.Text = $"签名已验证，文件已保存：\n{path}\n尚未替换。请关闭旧助手，再手动替换。";
            });
        };
        cancel.Click += (_,_) => request?.Cancel();
        Closing += (_,e) => { if (request != null) { e.Cancel = true; request.Cancel(); } };
        Closed += (_,_) => updates.Dispose();
    }
    async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if (request != null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8)); request = cts;
        check.IsEnabled = save.IsEnabled = false; cancel.Visibility = Visibility.Visible;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { status.Text = "请求已取消或超时。未替换助手。"; }
        catch (Exception ex) { status.Text = $"操作未完成：{ex.Message}"; }
        finally { request = null; cancel.Visibility = Visibility.Collapsed; check.IsEnabled = true; save.IsEnabled = updates.CanDownload && candidate?.Complete == true; }
    }
}
