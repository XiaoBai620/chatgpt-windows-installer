using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;

namespace PackagePicker;

// No target path, command, URL or user-specified executable is accepted by either entry point.
// The old process sends its verified release to a byte-identical local helper over inherited pipes.
internal static class AssistantUpdateInstall
{
    const string Prefix=".picker-update-", HelperName="picker-update-helper.exe", Backup="previous.exe";
    static string Self => Environment.ProcessPath ?? throw new IOException("无法确定助手路径。");
    static string AssetName => $"ChatGPT-PackagePicker-{AssistantUpdates.Rid}.exe";
    static string Stage(string target,string id)=>Path.Combine(Path.GetDirectoryName(target)!,Prefix+id);
    static bool Same(string? a,string? b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    static string EventName(string id)=>@"Local\PackagePicker-started-"+id;
    static void Require(bool ok,string reason) { if(!ok) throw new InvalidDataException(reason); }
    static FileStream LockFile(string path,bool movable=false)=>new(path,FileMode.Open,FileAccess.Read,FileShare.Read|(movable?FileShare.Delete:0));
    static void CheckLocal(string path)
    {
        var root=Path.GetPathRoot(path);
        Require(root is {Length:3} && new DriveInfo(root).DriveType==DriveType.Fixed,"自动更新仅支持本地固定磁盘。");
        for(string? p=path;p!=null;p=Path.GetDirectoryName(p)) Require((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"不自动更新链接或重解析目录。");
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessBasic { public IntPtr Reserved1,Peb,Reserved2,Reserved3,Pid,ParentPid; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process,int info,out ProcessBasic basic,int length,out int returned);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr token,int info,out int value,int length,out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    static Process Parent()
    {
        Require(NtQueryInformationProcess(Process.GetCurrentProcess().Handle,0,out var b,Marshal.SizeOf<ProcessBasic>(),out _)==0,"无法识别启动进程。");
        var parent=Process.GetProcessById(b.ParentPid.ToInt32());
        if(parent.StartTime.ToUniversalTime()>Process.GetCurrentProcess().StartTime.ToUniversalTime()) {parent.Dispose();throw new IOException("启动进程已退出。");}
        return parent;
    }
    static void OrdinaryUser()
    {
        using var identity=WindowsIdentity.GetCurrent();
        bool known=GetTokenInformation(identity.Token,20,out var elevated,sizeof(int),out _);
        bool admin=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Require(known && elevated==0 && !admin,"管理员上下文不自动替换。请以普通权限重新打开，或手动保存新版。");
    }
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile","IL3000",Justification="Empty Assembly.Location intentionally limits replacement to bundled executables.")]
    static void Target(string target)
    {
        OrdinaryUser(); CheckLocal(target);
        Require(Same(Path.GetFileName(target),"ChatGPT-PackagePicker.exe") || Same(Path.GetFileName(target),AssetName),"当前文件名不是本架构的标准助手文件名，请手动更新。");
        Require(string.IsNullOrEmpty(typeof(AssistantUpdates).Assembly.Location),"仅支持自包含单文件发布件自动更新。");
    }
    internal static string? Unavailable() {try { Target(Self); return null; } catch(Exception e) {return e.Message;} }
    static void DeleteIfPresent(string path) {try { File.Delete(path); }catch(IOException){}catch(UnauthorizedAccessException){} }
    static void RemoveEmpty(string folder) {try {Directory.Delete(folder,false);}catch(IOException){}catch(UnauthorizedAccessException){} }
    static ProcessStartInfo StartInfo(string path,params string[] args)
    {
        var info=new ProcessStartInfo(path){ UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)! }; foreach(var arg in args) info.ArgumentList.Add(arg); return info;
    }
    internal static async Task PrepareAsync(AssistantUpdates client,AssistantUpdates.Release release,CancellationToken ct)
    {
        var target=Self; Target(target);
        var id=Guid.NewGuid().ToString("N"); var stage=Stage(target,id); Require(!Directory.Exists(stage),"临时更新目录已存在。"); Directory.CreateDirectory(stage);
        var helper=Path.Combine(stage,HelperName); bool handedOff=false;
        try {
            CheckLocal(stage); await client.DownloadAsync(release,stage,ct);
            File.Copy(target,helper,false);
            using var original=LockFile(target); using var copy=LockFile(helper);
            var a=await SHA256.HashDataAsync(original,ct); var b=await SHA256.HashDataAsync(copy,ct);
            Require(a.SequenceEqual(b),"更新辅助程序副本不匹配。");
            var info=StartInfo(helper,"--assistant-helper"); info.RedirectStandardInput=info.RedirectStandardOutput=true; info.CreateNoWindow=true;
            using var process=Process.Start(info) ?? throw new IOException("无法启动更新辅助程序。");
            await process.StandardInput.WriteLineAsync(release.Version.ToString()); await process.StandardInput.WriteLineAsync(release.Digest); process.StandardInput.Close();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { var response=await process.StandardOutput.ReadLineAsync(timeout.Token); Require(response=="READY",response ?? "辅助程序未就绪。"); handedOff=true; }
            finally {if(!handedOff && !process.HasExited) {process.Kill();process.WaitForExit(5000);} }
        } finally { if(!handedOff) {DeleteIfPresent(Path.Combine(stage,AssetName));DeleteIfPresent(helper);RemoveEmpty(stage);} }
    }
    static void CheckImage(string path,DesktopVersion version)
    {
        var info=FileVersionInfo.GetVersionInfo(path);
        Require(info.OriginalFilename=="ChatGPT-PackagePicker.dll" && DesktopVersion.Parse(info.ProductVersion?.Split('+')[0])==version,"助手产品或内置版本与发布记录不符。");
        using var f=LockFile(path,true); using var reader=new System.Reflection.PortableExecutable.PEReader(f);
        Require(reader.PEHeaders.CoffHeader.Machine==(AssistantUpdates.Rid=="win-x64"?System.Reflection.PortableExecutable.Machine.Amd64:System.Reflection.PortableExecutable.Machine.Arm64),"附件的 PE 架构不匹配。");
    }
    internal static int Helper()
    {
        bool ready=false; string? stage=null;
        try {
            OrdinaryUser(); Require(Same(Path.GetFileName(Self),HelperName),"辅助程序入口仅供更新事务使用。"); CheckLocal(Self);
            using var parent=Parent();
            Require(GetNamedPipeServerProcessId(GetStdHandle(-11),out uint peer) && peer==parent.Id,"握手管道不属于启动助手。");
            string target=parent.MainModule!.FileName; Target(target);
            stage=Path.GetDirectoryName(Self)!; var name=Path.GetFileName(stage);
            Require(name.StartsWith(Prefix,StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..],"N",out _) && Same(stage,Stage(target,name[Prefix.Length..])),"辅助程序位置不属于启动助手。");
            string id=name[Prefix.Length..], next=Path.Combine(stage,AssetName), backup=Path.Combine(stage,Backup);
            using var mutex=new Mutex(false,@"Local\PackagePicker-update-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target.ToUpperInvariant()))));
            try { Require(mutex.WaitOne(0),"该助手已有更新正在进行。"); } catch(AbandonedMutexException) { }
            // An open process handle refers to the original parent even if Windows reuses its PID.
            _=parent.Handle;
            var version=DesktopVersion.Parse(Console.ReadLine()); var digest=Console.ReadLine();
            Require(version!=null && version.CompareTo(AssistantUpdates.Current)>0 && (AssistantUpdates.Current.Rc!=null || version.Rc==null) && AssistantUpdates.ValidDigest(digest),"无效的更新事务。");
            CheckLocal(next); Require(!File.Exists(backup) && !Directory.Exists(backup),"备份位置已占用。");
            using var helper=LockFile(Self); var oldHash=SHA256.HashData(helper);
            using var candidate=LockFile(next,true);
            Require(AssistantUpdates.Matches(SHA256.HashData(candidate),digest!),"候选文件摘要不匹配。"); CheckImage(next,version!);
            using(var old=LockFile(target)) Require(SHA256.HashData(old).SequenceEqual(oldHash),"启动助手与辅助程序不一致。");
            Console.WriteLine("READY");Console.Out.Flush();ready=true;
            Require(parent.WaitForExit(60000),"原助手未退出，未替换。");
            CheckLocal(target); CheckLocal(next);
            using(var old=LockFile(target)) Require(SHA256.HashData(old).SequenceEqual(oldHash),"原文件已变化，未替换。");
            // ReplaceFileW opens the replacement with write access to merge attributes. Holding
            // our own read-only handle across this call denies that open. Validate both names again
            // after the atomic replace and hold the installed image read-locked through startup.
            candidate.Dispose();
            try { File.Replace(next,target,backup); }
            catch {
                if(!File.Exists(target) && File.Exists(backup)) { CheckLocal(backup); using(var b=LockFile(backup)) Require(SHA256.HashData(b).SequenceEqual(oldHash),"备份不匹配。"); File.Move(backup,target); }
                using var old=LockFile(target); if(SHA256.HashData(old).SequenceEqual(oldHash)) Process.Start(StartInfo(target))?.Dispose();
                throw;
            }
            try {
                CheckLocal(target); CheckLocal(backup); using var prior=LockFile(backup); Require(SHA256.HashData(prior).SequenceEqual(oldHash),"备份不匹配。");
                using var installed=LockFile(target); Require(AssistantUpdates.Matches(SHA256.HashData(installed),digest!),"替换后的文件与发布记录不一致。");
                using var signal=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(id),out bool created); Require(created,"重启确认事件已占用。");
                using var child=Process.Start(StartInfo(target,"--assistant-restarted",id)) ?? throw new IOException("无法重启新版助手。");
                var clock=Stopwatch.StartNew(); while(!signal.WaitOne(200)) {
                    if(child.HasExited) throw new IOException("新版助手在显示首页前退出。");
                    if(clock.Elapsed>TimeSpan.FromSeconds(60)) { child.Kill(); child.WaitForExit(5000); throw new IOException("新版未确认启动。"); }
                }
            } catch(Exception startError) {
                CheckLocal(backup); using(var saved=LockFile(backup)) Require(SHA256.HashData(saved).SequenceEqual(oldHash),"备份校验失败，未启动。");
                File.Replace(backup,target,null);
                using var restored=LockFile(target); Require(SHA256.HashData(restored).SequenceEqual(oldHash),"恢复文件校验失败。"); Process.Start(StartInfo(target))?.Dispose(); throw new IOException($"{startError.Message} 已恢复并重新打开原版本。",startError);
            }
            DeleteIfPresent(backup); return 0;
        } catch(Exception e) {
            if(!ready) {Console.WriteLine(e.Message);Console.Out.Flush();}
#if UPDATE_FIXTURE
            else File.WriteAllText(Path.Combine(stage!,"fixture-result.txt"),e.ToString());
#else
            else MessageBox.Show($"更新未完成：{e.Message}\n请检查原助手；如有 previous.exe 备份可手动恢复。\n{stage}","助手更新",MessageBoxButton.OK,MessageBoxImage.Warning);
#endif
            return 8;
        }
    }
    internal static int Restarted(string id)
    {
#if UPDATE_BROKEN_FIXTURE
        // Integration-test build only: emulate a new app exiting before it renders the homepage.
        return 77;
#else
        try {
            Require(Guid.TryParseExact(id,"N",out _),"无效事务。"); Target(Self); var stage=Stage(Self,id); CheckLocal(stage);
            using var helper=Parent(); var path=Path.Combine(stage,HelperName); Require(Same(helper.MainModule!.FileName,path),"非更新重启。");
            using var signal=EventWaitHandle.OpenExisting(EventName(id)); var home=new Home();
            _=helper.Handle;
            home.ContentRendered+=async (_,_)=> {
                signal.Set(); await helper.WaitForExitAsync(); helper.Dispose();
                // The image section may remain mapped briefly while Windows tears down the helper.
                for(int attempt=0;attempt<20 && File.Exists(path);attempt++) {await Task.Delay(200);DeleteIfPresent(path);}
                RemoveEmpty(stage);
            };
            return new Application().Run(home);
        }catch {return 8;}
#endif
    }
}
