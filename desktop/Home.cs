using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PackagePicker;
internal sealed class Home:Window {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] static extern int GetPackagesByPackageFamily(string family,ref uint count,IntPtr names,ref uint length,IntPtr buffer);
    static bool? Registered(){uint count=0,length=0;int code=GetPackagesByPackageFamily("OpenAI.Codex_2p2nqsd0c76g0",ref count,IntPtr.Zero,ref length,IntPtr.Zero);return code switch{122 when count>0=>true,0 when count==0=>false,_=>null};}
    readonly TextBlock title=new(){FontSize=26,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap},info=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,18)},version=new(){TextWrapping=TextWrapping.Wrap};
    readonly Button primary=new(),offline=new(){Content="商店不可用？使用离线包…"};
    readonly CancellationTokenSource lifetime=new(); bool? installed;
    internal bool Passed {get;private set;}
    static Button Button(string text)=>new(){Content=text,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,2,8,4)};
    public Home(bool smoke=false){Title="ChatGPT 安装助手";Width=670;Height=430;MinWidth=470;MinHeight=380;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brushes.WhiteSmoke;
        var outer=new StackPanel{Margin=new Thickness(26)};Content=new ScrollViewer{Content=outer,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};outer.Children.Add(new TextBlock{Text="ChatGPT 安装助手   /   Windows",FontSize=17,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
        var card=new StackPanel();outer.Children.Add(new Border{Background=Brushes.White,Padding=new Thickness(22),CornerRadius=new CornerRadius(10),Child=card});card.Children.Add(new TextBlock{Text="当前账户",Foreground=Brushes.DimGray});card.Children.Add(title);card.Children.Add(info);
        var row=new WrapPanel();card.Children.Add(row);primary.Background=new SolidColorBrush(Color.FromRgb(39,60,48));primary.Foreground=Brushes.White;foreach(var b in new[]{primary,offline}){b.Padding=new Thickness(12,7,12,7);b.Margin=new Thickness(0,2,8,4);row.Children.Add(b);}primary.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9plm9xgg6vks"){UseShellExecute=true});info.Text="已请求打开 Microsoft Store 商品页。请在商店安装或打开应用，再回来重新检查。若下载失败，可直接使用离线包。";}catch{info.Text="无法打开商店。可以使用下方的离线包入口。";}};offline.Click+=(_,_)=>new Picker(){Owner=this}.ShowDialog();
        var actions=new WrapPanel{Margin=new Thickness(0,14,0,4)};outer.Children.Add(actions);var refresh=Button("重新检查账户" );actions.Children.Add(refresh);refresh.Click+=(_,_)=>Refresh();var updates=Button("助手更新详情…");actions.Children.Add(updates);updates.Click+=(_,_)=>new AssistantUpdateWindow{Owner=this}.ShowDialog();outer.Children.Add(version);
        title.Text="正在检查 ChatGPT 安装状态…";version.Text="安装助手 "+AssistantUpdates.Current+" · 正在检查助手更新…";Closed+=(_,_)=>lifetime.Cancel();
        Loaded+=async(_,_)=>{Refresh();if(smoke){Passed=title.Text.Length>0&&primary.IsVisible&&offline.IsVisible&&OwnedWindows.Count==0;Close();return;}using var client=new AssistantUpdates();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(8));try{var release=await client.CheckAsync(timeout.Token);version.Text=release==null?$"安装助手 {AssistantUpdates.Current} · 暂无更新的稳定桌面版":$"安装助手有新版 {release.Version}，请查看更新详情。";}catch{if(!lifetime.IsCancellationRequested)version.Text="暂时无法检查助手更新，不影响安装或使用本地包。";}};
    }
    void Refresh(){try{installed=Registered();}catch{installed=null;}title.Text=installed switch{true=>"当前账户已注册 ChatGPT",false=>"当前账户未检测到新版 ChatGPT",_=>"暂时无法确认安装状态"};info.Text=installed switch{true=>"可以从开始菜单打开 ChatGPT，无需重复安装。此次检查未启动应用。",false=>"首次安装建议使用 Microsoft Store。点击下方按钮进入官方商品页，安装后回来重新检查。",_=>"这不代表没有安装。先在开始菜单查找 ChatGPT；需要安装时优先使用商店。"};primary.Content=installed==true?"查看 Microsoft Store 商品页":"从 Microsoft Store 安装";}
}
