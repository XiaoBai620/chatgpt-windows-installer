using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
namespace PackagePicker;
internal class Picker:Window {
    readonly HashSet<string> dirs=new(StringComparer.OrdinalIgnoreCase);
    readonly DataGrid grid=new(){IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserSortColumns=false,SelectionMode=DataGridSelectionMode.Single};
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap}, selected=new(){TextWrapping=TextWrapping.Wrap};
    readonly Button updates=new(){Content="助手更新…",Padding=new Thickness(10,4,10,4),VerticalAlignment=VerticalAlignment.Center};
    readonly Button add=new(){Content="添加包目录…"}, refresh=new(){Content="重新扫描"};
    readonly Button fetch=new(){Content="下载官方当前离线包"}, install=new(){Content="部署所选包…",IsEnabled=false}, cancel=new(){Content="取消下载",Visibility=Visibility.Collapsed};
    readonly string cache;
    bool busy;
    CancellationTokenSource? download;
    internal bool ScanSucceeded {get;private set;}
    internal bool VerificationPassed {get;private set;}
    public Picker(bool smoke=false,string? verifyFile=null){
        Title="ChatGPT 安装助手 · 离线包选择"; Width=800; Height=490; MinWidth=540;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel{Margin=new Thickness(22)}; Content=root;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var heading=new DockPanel();top.Children.Add(heading);
        DockPanel.SetDock(updates,Dock.Right);heading.Children.Add(updates);
        updates.Click+=(_,_)=>{if(!busy)new AssistantUpdateWindow{Owner=this}.ShowDialog();};
        heading.Children.Add(new TextBlock{Text="选择离线安装包",FontSize=23,FontWeight=FontWeights.SemiBold});
        var guide=new Button{Content="首选：从 Microsoft Store 获取 ChatGPT ↗",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,10,0,6),Padding=new Thickness(8,4,8,4)};top.Children.Add(guide);
        guide.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9plm9xgg6vks"){UseShellExecute=true});}catch{status.Text="未能打开浏览器，请访问 learn.chatgpt.com 的 Windows 部署页。";}};
        top.Children.Add(new TextBlock{Text="官方入口需要联网。本地列表按包内版本排序；推荐仅指已扫描文件，不代表全网最新。",TextWrapping=TextWrapping.Wrap});
        var tools=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,4)};top.Children.Add(tools);foreach(var b in new[]{add,refresh,fetch,cancel,install}){b.Padding=new Thickness(8,4,8,4);b.Margin=new Thickness(0,0,8,0);tools.Children.Add(b);}
        top.Children.Add(status);selected.Margin=new Thickness(0,8,0,0);DockPanel.SetDock(selected,Dock.Bottom);root.Children.Add(selected);root.Children.Add(grid);
        foreach(var (title,field) in new[]{("版本","Version"),("架构","Arch"),("状态","State"),("文件","FileName")})grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(field)});
        grid.SelectionChanged+=(_,_)=>{install.IsEnabled=!busy && grid.SelectedItem is Package {Eligible:true};selected.Text=grid.SelectedItem is Package p?$"{(p.Eligible?"选择":"仅供查看")}：{p.Path}\n{p.State}。部署前还会检查许可证并请求确认。":"未选择本地包。";};
        string dl=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders","{374DE290-123F-4565-9164-39C4925E467B}",null) as string??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
        cache=Path.Combine(Environment.ExpandEnvironmentVariables(dl),"ChatGPT-offline");dirs.Add(cache);
        fetch.Click+=async(_,_)=>await DownloadPackage();cancel.Click+=(_,_)=>download?.Cancel();install.Click+=async(_,_)=>await InstallSelected();
        Closing+=(_,e)=>{if(busy){e.Cancel=true;status.Text=download!=null?"请先取消下载，清理完成后即可关闭。":"正在等待部署结果，请勿关闭。";}};
        add.Click+=async(_,_)=>{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true){dirs.Add(picker.FolderName);await Scan();}};refresh.Click+=async(_,_)=>await Scan();
        if(verifyFile!=null){dirs.Clear();dirs.Add(Path.GetDirectoryName(Path.GetFullPath(verifyFile))!);}
        Loaded+=async(_,_)=>{await Scan(); if(smoke||verifyFile!=null){VerificationPassed=ScanSucceeded && (verifyFile==null || grid.SelectedItem is Package p && p.Eligible && string.Equals(p.Path,Path.GetFullPath(verifyFile),StringComparison.OrdinalIgnoreCase));if(smoke)VerificationPassed &= await AssistantUpdateTests.SmokeWindow(this);Close();}};
    }
    void LockActions(bool value){busy=value;updates.IsEnabled=grid.IsEnabled=add.IsEnabled=refresh.IsEnabled=fetch.IsEnabled=!value;install.IsEnabled=!value && grid.SelectedItem is Package {Eligible:true};}
    async Task DownloadPackage(){
        if(busy || MessageBox.Show(this,$"将从 OpenAI 下载约 1 GB 的 MSIX 及许可证到：\n{cache}\n下载不会自动安装。继续？","准备离线包",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        LockActions(true);using var cts=new CancellationTokenSource();download=cts;cancel.Visibility=Visibility.Visible;
        try{var file=await Acquisition.DownloadAsync(cache,new Progress<string>(s=>status.Text=s),cts.Token);await Scan();selected.Text="已准备："+file+"。包与同名 XML 可一起复制到目标电脑。";}catch(OperationCanceledException){status.Text="已取消下载，未部署。";}catch(Exception e){status.Text="下载未完成："+e.Message;}finally{download=null;cancel.Visibility=Visibility.Collapsed;LockActions(false);}}
    async Task InstallSelected(){
        if(busy||grid.SelectedItem is not Package {Eligible:true} p)return;
        var license=Path.ChangeExtension(p.Path,".xml");if(!File.Exists(license)){var pick=new OpenFileDialog{Title="为所选安装包选择离线许可证",Filter="离线许可证 (*.xml)|*.xml",InitialDirectory=Path.GetDirectoryName(p.Path)};if(pick.ShowDialog(this)!=true)return;license=pick.FileName;}
        try{Acquisition.ValidateLicense(license);}catch(Exception ex){status.Text="许可证不可用："+ex.Message;return;}
        if(MessageBox.Show(this,$"为这台电脑预配 ChatGPT {p.Version} ({p.Arch})？\n安装包：{p.Path}\n许可证：{license}\n此操作需要管理员权限，会修改系统，可能需要重新登录。不会自动重启。","确认部署",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        LockActions(true);status.Text="等待授权或系统部署与回查…";try{status.Text=await Deployment.InstallAsync(p.Path,license);}finally{LockActions(false);}}
    async Task Scan(){ScanSucceeded=false;fetch.IsEnabled=add.IsEnabled=refresh.IsEnabled=false;grid.ItemsSource=null;selected.Text="";status.Text="正在读取包清单并检查本地签名…";var roots=dirs.ToArray();
        try{var result=await Task.Run(()=>{var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var errors=new List<string>();
            foreach(var root in roots){try{foreach(var file in Directory.EnumerateFiles(root,"*.msix",SearchOption.TopDirectoryOnly))files.Add(file);}catch(DirectoryNotFoundException){}catch(Exception ex)when(ex is UnauthorizedAccessException or IOException){errors.Add(root);}}
            return (Rows:Catalog.Rank(files.Select(p=>Catalog.Inspect(p,RuntimeInformation.OSArchitecture.ToString(),Environment.OSVersion.Version,Trust.Verify))),Errors:errors);});
            grid.ItemsSource=result.Rows;var best=result.Rows.FirstOrDefault(x=>x.Eligible);if(best!=null)grid.SelectedItem=best;
            status.Text=$"目录 {roots.Length} 个 · 文件 {result.Rows.Count} 个 · "+(best==null?"无可推荐本地包":$"首选：{best.Version}（本地最高可选版本）")+(result.Errors.Count>0?$" · 未能读取：{string.Join("；",result.Errors)}":"");status.ToolTip=string.Join("\n",roots);ScanSucceeded=result.Errors.Count==0;
        }catch(Exception ex){status.Text="扫描未完成："+ex.Message;}finally{fetch.IsEnabled=add.IsEnabled=refresh.IsEnabled=!busy;}}
}
internal static class Program {
    [STAThread] static int Main(string[] args){if(args.Length==3 && args[0]=="--deploy")return Deployment.Helper(args[1],args[2]);if(args.SequenceEqual(new[]{"--test"})){foreach(var test in new Func<int>[]{CatalogTests.Run,Acquisition.SelfTest,Deployment.SelfTest,AssistantUpdateTests.Run}){var rc=test();if(rc!=0)return rc;}return 0;}if(args.Length==2 && args[0]=="--verify-download"){try{var f=Acquisition.DownloadAsync(args[1],new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult();Acquisition.ValidateLicense(Path.ChangeExtension(f,".xml"));return 0;}catch(Exception e){Console.Error.WriteLine(e);return 6;}}var verify=args.Length==2&&args[0]=="--verify-package"?args[1]:null;bool smoke=args.Contains("--smoke");if(args.Length==0 || args.SequenceEqual(new[]{"--home-smoke"})){bool check=args.Length!=0;var home=new Home(check);var result=new Application().Run(home);return check&&!home.Passed?7:result;}var picker=new Picker(smoke,verify);var code=new Application().Run(picker);return (smoke||verify!=null)&&!picker.VerificationPassed?5:code;}
}
