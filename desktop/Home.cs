using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PackagePicker;

internal sealed class Home : Window
{
    const string StorePage = "https://apps.microsoft.com/detail/9plm9xgg6vks";
    readonly TextBlock title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18) };
    readonly TextBlock version = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly Button primary = new(), offline = new() { Content = "商店不可用？使用离线包…" };
    readonly CancellationTokenSource lifetime = new();
    (bool? Registered, string? AppId) app;
    internal bool Passed { get; private set; }

    public Home(bool smoke = false)
    {
        Title = "ChatGPT 安装助手";
        Width = 560; Height = 350; MinWidth = 320; MinHeight = 235;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Theme.Apply(this);
        var muted = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(86, 94, 89));
        info.Foreground = version.Foreground = muted;
        var layout = new Grid { Margin = new Thickness(24, 22, 24, 16) };
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = new ScrollViewer { Content = layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        var main = new StackPanel();
        layout.Children.Add(main);
        main.Children.Add(new TextBlock { Text = "当前 Windows 账户", Foreground = muted, FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        main.Children.Add(title);
        main.Children.Add(info);
        var actions = new WrapPanel();
        main.Children.Add(actions);
        Theme.Primary(primary);
        primary.MinHeight = 36;
        primary.Margin = new Thickness(0, 0, 6, 4);
        actions.Children.Add(primary);
        primary.Click += (_, _) => PrimaryAction();
        var refresh = new Button { Content = "重新检查" };
        Theme.Quiet(refresh);
        refresh.MinHeight = 36;
        actions.Children.Add(refresh);
        refresh.Click += (_, _) => Refresh();
        Theme.Quiet(offline);
        offline.HorizontalAlignment = HorizontalAlignment.Left;
        offline.Margin = new Thickness(0, 6, 0, 18);
        main.Children.Add(offline);
        offline.Click += (_, _) => { new Picker { Owner = this }.ShowDialog(); Refresh(); };

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var details = new Button { Content = "更新详情…", Margin = new Thickness(8, 0, 0, 0) };
        Theme.Quiet(details);
        DockPanel.SetDock(details, Dock.Right);
        footer.Children.Add(details);
        footer.Children.Add(version);
        var separator = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(215, 218, 213)) };
        Grid.SetRow(separator, 1);
        layout.Children.Add(separator);
        details.Click += (_, _) => new AssistantUpdateWindow { Owner = this }.ShowDialog();
        AutomationProperties.SetName(refresh, "重新检查当前账户的安装状态");
        title.Text = "正在检查安装状态…";
        SetUpdateStatus("正在检查更新…");
        Closed += (_, _) => lifetime.Cancel();
        Loaded += async (_, _) => {
            Refresh();
            if (smoke) {
                Passed = title.Text.Length > 0 && primary.IsVisible && offline.IsVisible && OwnedWindows.Count == 0;
                Close();
                return;
            }
            primary.Focus();
            await CheckUpdatesAsync();
        };
    }

    void SetUpdateStatus(string status) => version.Text = $"助手 {AssistantUpdates.Current} · {status}";

    async Task CheckUpdatesAsync()
    {
        try {
            using var client = new AssistantUpdates();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var release = await client.CheckAsync(timeout.Token);
            if (!lifetime.IsCancellationRequested)
                SetUpdateStatus(release == null ? "暂无新版" : !release.Complete ? $"{release.Version} 发布文件尚不完整" : $"发现新版 {release.Version}");
        } catch { if (!lifetime.IsCancellationRequested) SetUpdateStatus("暂时无法检查更新"); }
    }

    void Refresh()
    {
        app = LocalApp.Inspect();
        (title.Text, info.Text, primary.Content) = app switch {
            (true, not null) => ("ChatGPT 已注册", "可直接打开当前账户中的应用，无需重新安装。", "打开 ChatGPT"),
            (true, null) => ("ChatGPT 已注册", "已找到应用包，但无法确认唯一的启动项。请从开始菜单打开。", "查看开始菜单指引"),
            (false, _) => ("尚未安装 ChatGPT", "建议从 Microsoft Store 安装。安装完成后，可在这里重新检查。", "从 Microsoft Store 安装"),
            _ => ("无法确认安装状态", "请先在开始菜单搜索 ChatGPT。若未找到，可前往 Microsoft Store 安装。", "查看 Microsoft Store 商品页")
        };
    }

    void PrimaryAction()
    {
        if (app.Registered == true) {
            if (app.AppId is { } id) {
                try { LocalApp.Activate(id); info.Text = "已向 Windows 发送 ChatGPT 启动请求。"; return; }
                catch { app = (true, null); primary.Content = "查看开始菜单指引"; }
            }
            info.Text = "按 Windows 键，输入 ChatGPT，再选择应用并按 Enter 打开。若列表为空，请重新检查安装状态。";
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(StorePage) { UseShellExecute = true });
            info.Text = "已请求打开官方商品页。安装完成后，请选择“重新检查”。";
        } catch { info.Text = "无法打开商品页。请在 Microsoft Store 搜索 ChatGPT，或使用下方的离线包入口。"; }
    }

    // Ask Windows only about this family for the current user. Use the registered
    // application IDs supplied by the package API, never an inferred manifest ID.
    static class LocalApp
    {
        const string Family = "OpenAI.Codex_2p2nqsd0c76g0";
        const int MoreSpace = 122;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int GetPackagesByPackageFamily(string family, ref uint count, [Out] IntPtr[]? names, ref uint chars, IntPtr buffer);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int OpenPackageInfoByFullName(string name, uint reserved, out IntPtr reference);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern int GetPackageApplicationIds(IntPtr reference, ref uint bytes, IntPtr buffer, out uint count);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        static extern int ClosePackageInfo(IntPtr reference);

        internal static (bool?, string?) Inspect()
        {
            bool? registered = null;
            try {
                uint count = 0, chars = 0;
                int code = GetPackagesByPackageFamily(Family, ref count, null, ref chars, IntPtr.Zero);
                if (code == 0 && count == 0) return (false, null);
                if (code != MoreSpace || count == 0) return (null, null);
                registered = true;
                if (count > 64 || chars is 0 or > 65536) return (true, null);
                var names = new IntPtr[count];
                var buffer = Marshal.AllocHGlobal(checked((int)chars * sizeof(char)));
                try {
                    if (GetPackagesByPackageFamily(Family, ref count, names, ref chars, buffer) != 0) return (true, null);
                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < count; i++) {
                        var name = Marshal.PtrToStringUni(names[i]);
                        if (name == null || !ReadIds(name, ids) || ids.Count > 1) return (true, null);
                    }
                    return (true, ids.SingleOrDefault());
                } finally { Marshal.FreeHGlobal(buffer); }
            } catch { return (registered, null); }
        }

        static bool ReadIds(string name, HashSet<string> ids)
        {
            if (OpenPackageInfoByFullName(name, 0, out var reference) != 0) return false;
            try {
                uint bytes = 0;
                int code = GetPackageApplicationIds(reference, ref bytes, IntPtr.Zero, out var count);
                if (code == 0 && count == 0) return true;
                if (code != MoreSpace || bytes is 0 or > 65536) return false;
                var buffer = Marshal.AllocHGlobal((int)bytes);
                try {
                    if (GetPackageApplicationIds(reference, ref bytes, buffer, out count) != 0 || count > bytes / IntPtr.Size) return false;
                    for (int i = 0; i < count; i++) {
                        var id = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                        if (id == null || !id.StartsWith(Family + "!", StringComparison.OrdinalIgnoreCase) || id.Length <= Family.Length + 1) return false;
                        ids.Add(id);
                    }
                    return true;
                } finally { Marshal.FreeHGlobal(buffer); }
            } finally { ClosePackageInfo(reference); }
        }

        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationActivationManager
        {
            void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        }

        internal static void Activate(string id)
        {
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true)!;
            var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
            try { manager.ActivateApplication(id, null, 0, out _); }
            finally { Marshal.ReleaseComObject(manager); }
        }
    }
}
