using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
namespace PackagePicker;
internal class Picker:Window {
    readonly Button back=new(){Content="← 返回首页"}, import=new(){Content="导入历史版本…"};
    readonly TextBlock onlineHint=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,8)};
    readonly CheckBox accelerate=new(){Content="多连接加速（推荐）",IsChecked=true,Margin=new Thickness(0,6,0,0),ToolTip="官方源支持时使用最多 4 个连接；不支持时自动改用单连接。实际速度取决于网络与下载源。"};
    readonly ProgressBar transferBar=new(){Minimum=0,Maximum=100,Height=8,Margin=new Thickness(0,4,0,10)};
    readonly TextBlock network=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
    readonly HashSet<string> dirs=new(StringComparer.OrdinalIgnoreCase);
    readonly DataGrid grid=new(){IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserSortColumns=false,SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal};
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap}, selected=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock emptyTitle=new(){FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap}, emptyHint=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,12)};
    readonly StackPanel empty=new(){VerticalAlignment=VerticalAlignment.Center}, footer=new();
    readonly Button sources=new(){Content="包来源与扫描 ▾",HorizontalAlignment=HorizontalAlignment.Right}, fetch=new(){Content="下载官方离线包",HorizontalAlignment=HorizontalAlignment.Left}, install=new(){Content="部署所选包…",HorizontalAlignment=HorizontalAlignment.Right}, cancel=new(){Content="取消下载",HorizontalAlignment=HorizontalAlignment.Left};
    readonly MenuItem fetchItem=new(){Header="下载官方离线包…"};
    readonly string cache;
    enum Activity { Idle, Scanning, Downloading, Deploying }
    Activity activity=Activity.Scanning;
    CancellationTokenSource? download;
    internal bool ScanSucceeded {get;private set;}
    internal bool VerificationPassed {get;private set;}
    public Picker(bool smoke=false,string? verifyFile=null){
        Title="离线安装包 · ChatGPT 安装助手"; Width=720; Height=580; MinWidth=420; MinHeight=540; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Theme.Apply(this); Theme.Primary(fetch); Theme.Primary(install); Theme.Quiet(sources); Theme.Quiet(cancel);
        var root=new Grid{Margin=new Thickness(20)}; Content=root;
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})root.RowDefinitions.Add(new RowDefinition{Height=height});
        void Place(UIElement element,int row){Grid.SetRow(element,row);root.Children.Add(element);}
        var header=new DockPanel{Margin=new Thickness(0,0,0,12)}; Place(header,0);
        DockPanel.SetDock(sources,Dock.Right); header.Children.Add(sources);
        Theme.Quiet(back);back.HorizontalAlignment=HorizontalAlignment.Left;header.Children.Add(back);back.Click+=(_,_)=>Close();PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape && !menuOpen()){e.Handled=true;Close();}};bool menuOpen()=>sources.ContextMenu?.IsOpen==true;
        var online=new StackPanel{Margin=new Thickness(0,0,0,14)};Place(online,1);online.Children.Add(new TextBlock{Text="官方当前版本",FontSize=20,FontWeight=FontWeights.SemiBold});
        onlineHint.Text=$"{RuntimeInformation.OSArchitecture} · 下载约 1 GB，包含离线许可证。下载完成后由你确认部署。";online.Children.Add(onlineHint);fetch.Content="下载官方当前版本";var downloadActions=new WrapPanel();downloadActions.Children.Add(fetch);downloadActions.Children.Add(cancel);online.Children.Add(downloadActions);online.Children.Add(accelerate);
        var libraryArea=new StackPanel{Margin=new Thickness(0,0,0,8)};Place(libraryArea,2);var library=new DockPanel();libraryArea.Children.Add(library);Theme.Quiet(import);DockPanel.SetDock(import,Dock.Right);library.Children.Add(import);library.Children.Add(new TextBlock{Text="本地与历史版本",FontSize=18,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        libraryArea.Children.Add(new TextBlock{Text="官方在线入口只提供当前版本。已下载的旧包会保留；可导入已有 MSIX 和对应 XML。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,4)});
        var menu=new ContextMenu{PlacementTarget=sources,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
        var add=new MenuItem{Header="添加包目录…"}; var refresh=new MenuItem{Header="重新扫描"};
        menu.Items.Add(add); menu.Items.Add(refresh); menu.Items.Add(fetchItem);
        sources.Click+=(_,_)=>{menu.IsOpen=true;add.Focus();};
        sources.ContextMenu=menu;import.Click+=(_,_)=>add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Place(grid,3); System.Windows.Automation.AutomationProperties.SetName(grid,"本地安装包，使用方向键选择");
        foreach(var (title,field,width) in new[]{("版本","Version",95), ("架构","Arch",55), ("状态","State",130), ("文件","FileName",100)})
            grid.Columns.Add(new DataGridTextColumn{Header=title,Binding=new Binding(field),MinWidth=width,Width=field=="FileName"?new DataGridLength(1,DataGridLengthUnitType.Star):new DataGridLength(width)});
        // The full file path remains available below the list at compact widths.
        grid.SizeChanged+=(_,e)=>grid.Columns[3].Visibility=e.NewSize.Width<460?Visibility.Collapsed:Visibility.Visible;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(grid,System.Windows.Input.KeyboardNavigationMode.Once);
        Place(empty,3);empty.VerticalAlignment=VerticalAlignment.Top;emptyTitle.FontSize=15;empty.Children.Add(emptyTitle); empty.Children.Add(emptyHint);empty.Children.Add(transferBar);empty.Children.Add(network);
        System.Windows.Automation.AutomationProperties.SetName(transferBar,"安装包下载进度");
        System.Windows.Automation.AutomationProperties.SetName(network,"下载速度、剩余时间与网络状态");
        var feedback=new ScrollViewer{Content=status,MaxHeight=68,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,10,0,0)}; Place(feedback,4);
        Place(footer,5); footer.Children.Add(new ScrollViewer{Content=selected,MaxHeight=60,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,6)}); footer.Children.Add(install);
        grid.SelectionChanged+=(_,_)=>{selected.Text=grid.SelectedItem is Package p?$"{(p.Eligible?"已选":"仅供查看")}：{p.Path}\n{p.State}。" : "请选择一个安装包。"; UpdateView();};
        string dl=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders","{374DE290-123F-4565-9164-39C4925E467B}",null) as string??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
        cache=Path.Combine(Environment.ExpandEnvironmentVariables(dl),"ChatGPT-offline");dirs.Add(cache);
        var folderSettings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ChatGPT-InstallAssistant","folders.json");
        if(!smoke && verifyFile==null)try{if(File.Exists(folderSettings)&&new FileInfo(folderSettings).Length<131072)foreach(var d in System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(folderSettings))??[])if(Path.IsPathFullyQualified(d))dirs.Add(d);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException){}
        fetch.Click+=async(_,_)=>await DownloadPackage(); fetchItem.Click+=async(_,_)=>await DownloadPackage(); install.Click+=async(_,_)=>await InstallSelected();
        cancel.Click+=(_,_)=>{download?.Cancel();cancel.IsEnabled=false;status.Text="正在取消下载并清理临时文件…";};
        Closing+=(_,e)=>{if(activity is Activity.Downloading or Activity.Deploying){e.Cancel=true;status.Text=activity==Activity.Downloading?"请先取消下载，清理完成后即可关闭。":"正在等待部署结果，请勿关闭。";}};
        UpdateView();
        add.Click+=async(_,_)=>{var picker=new OpenFolderDialog();if(picker.ShowDialog(this)==true){dirs.Add(picker.FolderName);await Scan();try{Directory.CreateDirectory(Path.GetDirectoryName(folderSettings)!);File.WriteAllText(folderSettings,System.Text.Json.JsonSerializer.Serialize(dirs));}catch(Exception e)when(e is IOException or UnauthorizedAccessException){status.Text+=" · 此目录仅在本次会话可用";}}};refresh.Click+=async(_,_)=>await Scan();
        if(verifyFile!=null){dirs.Clear();dirs.Add(Path.GetDirectoryName(Path.GetFullPath(verifyFile))!);}
        Loaded+=async(_,_)=>{await Scan(); if(smoke||verifyFile!=null){VerificationPassed=ScanSucceeded && (verifyFile==null || grid.SelectedItem is Package p && p.Eligible && string.Equals(p.Path,Path.GetFullPath(verifyFile),StringComparison.OrdinalIgnoreCase));if(smoke)VerificationPassed &= await AssistantUpdateTests.SmokeWindow(this);Close();}};
    }
    void UpdateView(){
        bool idle=activity==Activity.Idle, rows=grid.Items.Count>0, list=rows && (idle || activity==Activity.Deploying);
        sources.Visibility=idle?Visibility.Visible:Visibility.Collapsed; grid.Visibility=footer.Visibility=list?Visibility.Visible:Visibility.Collapsed;
        grid.IsEnabled=idle; install.IsEnabled=idle && grid.SelectedItem is Package {Eligible:true};
        empty.Visibility=list?Visibility.Collapsed:Visibility.Visible; fetchItem.Visibility=Visibility.Collapsed;import.IsEnabled=idle;back.IsEnabled=activity is not (Activity.Downloading or Activity.Deploying);
        fetch.Visibility=activity==Activity.Downloading?Visibility.Collapsed:Visibility.Visible;fetch.IsEnabled=idle; cancel.Visibility=activity==Activity.Downloading?Visibility.Visible:Visibility.Collapsed;
        accelerate.IsEnabled=idle;transferBar.Visibility=network.Visibility=activity==Activity.Downloading?Visibility.Visible:Visibility.Collapsed;
        emptyTitle.Text=activity switch{Activity.Downloading=>"正在下载官方离线包",Activity.Scanning=>"正在检查本地安装包",_=>ScanSucceeded?"未找到本地安装包":"未能完整扫描包目录"};
        emptyHint.Text=activity switch{Activity.Downloading=>"下载源：OpenAI 官方 CDN · 网络中断时会自动重试。",Activity.Scanning=>"正在读取版本信息并验证签名。",_=>"下载过的包会在这里按版本列出。已有旧版 MSIX？点击「导入历史版本」选择所在文件夹。"};
    }
    void FocusPackage(){
        if(!IsActive)return;
        if(grid.SelectedItem is not Package p){fetch.Focus();return;}
        var column=grid.Columns[0];grid.CurrentCell=new DataGridCellInfo(p,column);grid.ScrollIntoView(p,column);grid.UpdateLayout();
        if(column.GetCellContent(p)?.Parent is DataGridCell cell)cell.Focus();else grid.Focus();
    }
    async Task DownloadPackage(){
        if(activity!=Activity.Idle)return; // The explicitly labelled download button is consent to download, never to deploy.
        using var cts=new CancellationTokenSource(); download=cts; activity=Activity.Downloading; cancel.IsEnabled=true; status.Text="";network.Text="正在连接官方源…";transferBar.IsIndeterminate=true;transferBar.Value=0;UpdateView();cancel.Focus();
        try{var file=await Acquisition.DownloadAsync(cache,new Progress<DownloadProgress>(p=>{if(ReferenceEquals(download,cts) && activity==Activity.Downloading && !cts.IsCancellationRequested)ShowDownload(p);}),cts.Token,accelerate.IsChecked==true);await Scan();status.Text=$"已下载 {Path.GetFileName(file)}，可与同名 XML 一起复制。\n"+status.Text;fetch.Content="下载官方当前版本";}
        catch(OperationCanceledException){status.Text="已取消下载，未部署。";}catch(Exception e){status.Text="下载未完成："+e.Message+"\n可点击「重试下载」，或关闭多连接加速后再试。";fetch.Content="重试下载";}finally{download=null;activity=Activity.Idle;UpdateView();FocusPackage();}}
    void ShowDownload(DownloadProgress p){network.Text=p.Text;transferBar.IsIndeterminate=p.TotalBytes is not >0;transferBar.Value=p.Percent??0;}
    internal static int UiSelfTest(){
        Picker? window=null;
        try{
            window=new Picker(smoke:true);window.activity=Activity.Idle;window.UpdateView();
            if(window.accelerate.IsChecked!=true||!window.accelerate.IsEnabled||window.transferBar.Visibility!=Visibility.Collapsed||!Equals(window.import.Content,"导入历史版本…"))return 21;
            window.activity=Activity.Downloading;window.UpdateView();
            var sample=new DownloadProgress("正在下载安装包",45L*1048576,1008L*1048576,512*1024,TimeSpan.FromSeconds(90),TimeSpan.Zero,4,1);
            window.ShowDownload(sample);
            if(window.accelerate.IsEnabled||window.cancel.Visibility!=Visibility.Visible||window.fetch.Visibility!=Visibility.Collapsed||window.transferBar.IsIndeterminate||Math.Abs(window.transferBar.Value-100d*45/1008)>0.01||window.network.Text!=sample.Text)return 22;
            var panel=(FrameworkElement)window.Content;
            foreach(var size in new[]{new Size(680,540),new Size(380,500)}){
                panel.Measure(size);panel.Arrange(new Rect(size));panel.UpdateLayout();
                var position=window.network.TranslatePoint(new Point(),panel);
                if(position.Y+window.network.ActualHeight>size.Height+1||window.network.ActualWidth>size.Width+1)return 23;
                var output=Environment.GetEnvironmentVariable("PICKER_TEST_SCREENSHOTS");
                if(!string.IsNullOrEmpty(output)){
                    Directory.CreateDirectory(output);var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,System.Windows.Media.PixelFormats.Pbgra32);var background=new System.Windows.Media.DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(window.Background,null,new Rect(size));bitmap.Render(background);bitmap.Render(panel);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"download-{size.Width}.png"));encoder.Save(file);
                }
            }
            window.ShowDownload(new DownloadProgress("等待网络",45L*1048576,1008L*1048576,0,TimeSpan.FromSeconds(102),TimeSpan.FromSeconds(12),4,2));
            if(window.network.Text.Length==0||window.transferBar.IsIndeterminate)return 24;
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 25;}finally{window?.Close();}
    }
    async Task InstallSelected(){
        if(activity!=Activity.Idle||grid.SelectedItem is not Package {Eligible:true} p)return;
        var license=Path.ChangeExtension(p.Path,".xml");if(!File.Exists(license)){var pick=new OpenFileDialog{Title="为所选安装包选择离线许可证",Filter="离线许可证 (*.xml)|*.xml",InitialDirectory=Path.GetDirectoryName(p.Path)};if(pick.ShowDialog(this)!=true)return;license=pick.FileName;}
        try{Acquisition.ValidateLicense(license);}catch(Exception ex){status.Text="许可证不可用："+ex.Message;return;}
        if(MessageBox.Show(this,$"为这台电脑预配 ChatGPT {p.Version} ({p.Arch})？\n安装包：{p.Path}\n许可证：{license}\n此操作需要管理员权限，会修改系统，可能需要重新登录。不会自动重启。","确认部署",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        activity=Activity.Deploying;UpdateView();status.Text="等待授权或系统部署与回查…";try{status.Text=await Deployment.InstallAsync(p.Path,license);}catch(Exception ex){status.Text="部署未完成："+ex.Message;}finally{activity=Activity.Idle;UpdateView();install.Focus();}}
    async Task Scan(){ScanSucceeded=false;activity=Activity.Scanning;grid.ItemsSource=null;status.Text="";UpdateView();var roots=dirs.ToArray();
        try{var result=await Task.Run(()=>{var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var errors=new List<string>();
            foreach(var root in roots){try{foreach(var file in Directory.EnumerateFiles(root,"*.msix",SearchOption.TopDirectoryOnly))files.Add(file);}catch(DirectoryNotFoundException){}catch(Exception ex)when(ex is UnauthorizedAccessException or IOException){errors.Add(root);}}
            return (Rows:Catalog.Rank(files.Select(p=>Catalog.Inspect(p,RuntimeInformation.OSArchitecture.ToString(),Environment.OSVersion.Version,Trust.Verify))),Errors:errors);});
            grid.ItemsSource=result.Rows;var best=result.Rows.FirstOrDefault(x=>x.Eligible);grid.SelectedItem=best??result.Rows.FirstOrDefault();
            status.Text=$"已扫描 {roots.Length} 个目录 · {result.Rows.Count} 个文件"+(best!=null?$" · 本地推荐：{best.Version}（最高可选版本）":result.Rows.Count>0?" · 无可部署的包":"")+(result.Errors.Count>0?$" · 未能读取：{string.Join("；",result.Errors)}":"");status.ToolTip=string.Join("\n",roots);ScanSucceeded=result.Errors.Count==0;
        }catch(Exception ex){status.Text="扫描未完成："+ex.Message;}finally{activity=Activity.Idle;UpdateView();FocusPackage();}}
}
internal static class Program {
    [STAThread] static int Main(string[] args){
        if(args.SequenceEqual(new[]{"--test-network"}))return PackageDownloadTests.Run();
        if(args.SequenceEqual(new[]{"--test-ui"}))return Picker.UiSelfTest();
#if UPDATE_FIXTURE
        if(args.SequenceEqual(new[]{"--fixture-update"}))return AssistantUpdateTests.LocalFixture();
#endif
        if(args.SequenceEqual(new[]{"--assistant-helper"}))return AssistantUpdateInstall.Helper();
        if(args.Length==2 && args[0]=="--assistant-restarted")return AssistantUpdateInstall.Restarted(args[1]);
        if(args.Length==3 && args[0]=="--deploy")return Deployment.Helper(args[1],args[2]);if(args.SequenceEqual(new[]{"--test"})){foreach(var test in new Func<int>[]{CatalogTests.Run,Acquisition.SelfTest,Deployment.SelfTest,AssistantUpdateTests.Run,PackageDownloadTests.Run,Picker.UiSelfTest}){var rc=test();if(rc!=0)return rc;}return 0;}if(args.Length==2 && args[0]=="--verify-download"){try{var f=Acquisition.DownloadAsync(args[1],new Progress<string>(),CancellationToken.None).GetAwaiter().GetResult();Acquisition.ValidateLicense(Path.ChangeExtension(f,".xml"));return 0;}catch(Exception e){Console.Error.WriteLine(e);return 6;}}var verify=args.Length==2&&args[0]=="--verify-package"?args[1]:null;bool smoke=args.Contains("--smoke");if(args.Length==0 || args.SequenceEqual(new[]{"--home-smoke"})){bool check=args.Length!=0;var home=new Home(check);var result=new Application().Run(home);return check&&!home.Passed?7:result;}var picker=new Picker(smoke,verify);var code=new Application().Run(picker);return (smoke||verify!=null)&&!picker.VerificationPassed?5:code;}
}
