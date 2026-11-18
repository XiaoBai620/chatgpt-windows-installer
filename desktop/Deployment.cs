using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PackagePicker;

internal static class Deployment
{
    private const int Complete = 0, Failed = 1, Already = 10, NotAdmin = 11, Invalid = 12,
        InventoryFailed = 13, LowSpace = 14, VerificationFailed = 15, RestartUnverified = 16,
        Cancelled = 1223, Restart = 3010;
    private const string Name = "OpenAI.Codex", PublisherId = "2p2nqsd0c76g0";
    private static string SystemDir => Environment.SystemDirectory;
    private static bool Elevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static Task<string> InstallAsync(string package, string license) => Task.Run(() =>
    {
        try
        {
            if (Elevated()) return Describe(Helper(package, license));
            // Hold the selected inputs and the apphost across the UAC boundary, not just until launch.
            using var locks = new LocalLocks();
            var pkg = locks.File(package); var lic = locks.File(license);
            var exe = locks.File(Environment.ProcessPath ?? throw new IOException());
            if (!Path.GetExtension(exe.Name).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(exe.Name).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return "无法确认独立安装器 EXE，未申请提升权限。";
            var start = new ProcessStartInfo(exe.Name) { UseShellExecute = true, Verb = "runas", WorkingDirectory = SystemDir };
            foreach (var arg in new[] { "--deploy", pkg.Name, lic.Name }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException();
            process.WaitForExit();
            return Describe(process.ExitCode);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled) { return Describe(Cancelled); }
        catch (Exception) { return "启动部署助手或锁定本地路径失败，无法确认部署。"; }
    });

    internal static int Helper(string package, string license)
    {
        try
        {
            if (!Elevated()) return NotAdmin;
            using var locks = new LocalLocks();
            var pkg = locks.File(package); var lic = locks.File(license);
            if (!pkg.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) || pkg.Length == 0 || lic.Length == 0) return Invalid;
            var candidate = Catalog.Inspect(pkg.Name, RuntimeInformation.OSArchitecture.ToString(), Environment.OSVersion.Version, Trust.Verify);
            if (!candidate.Eligible || candidate.Version == null) return Invalid;
            try { Acquisition.ValidateLicense(lic.Name); }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException) { return Invalid; }
            return Provision(candidate.Version, () => ReadInventory(candidate.Arch),
                () => new DriveInfo(Path.GetPathRoot(SystemDir)!).AvailableFreeSpace >= checked(2 * pkg.Length + 256L * 1024 * 1024),
                () => Dism("/Add-ProvisionedAppxPackage", "/PackagePath:" + pkg.Name, "/LicensePath:" + lic.Name, "/Region:all").Code);
        }
        catch (Exception) { return Failed; }
    }

    private static int Provision(Version desired, Func<Version?> inventory, Func<bool> space, Func<int> add)
    {
        Version? existing;
        try { existing = inventory(); } catch (Exception) { return InventoryFailed; }
        if (existing != null && existing >= desired) return Already;
        if (!space()) return LowSpace;
        int code = add();
        if (code != Complete && code != Restart) return Failed;
        try { if (inventory() is Version found && found >= desired) return code; } catch (Exception) { }
        return code == Restart ? RestartUnverified : VerificationFailed;
    }

    private static string Describe(int code) => code switch
    {
        Complete => "预配已完成并核实。请注销并重新登录后检查应用。",
        Already => "已预配相同或更高版本，未重复部署。若当前账户尚未显示应用，请重新登录后检查。",
        Cancelled => "已取消 UAC 授权，未部署。",
        Restart => "预配已核实；DISM 返回 3010，需要手动重启。未自动重启。",
        RestartUnverified => "DISM 返回 3010，但尚未核实目标包；请手动重启后复查。未自动重启。",
        NotAdmin => "部署助手没有管理员权限，未部署。",
        Invalid => "安装包或许可证未通过部署前校验，未部署。",
        LowSpace => "系统卷可用空间不足，未部署。",
        InventoryFailed => "无法可靠读取系统预配清单，未部署。",
        VerificationFailed => "DISM 已返回成功，但回查未确认目标包家族、架构和版本。请检查系统预配状态。",
        _ => "部署失败，未确认完成。请检查输入文件及系统 DISM 日志。"
    };

    private static (int Code, string Output) Dism(params string[] operation)
    {
        // A native apphost is required: do not accidentally invoke redirected SysWOW64 tools.
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo(Path.Combine(SystemDir, "dism.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = SystemDir,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment.Clear();
        start.Environment["SystemRoot"] = start.Environment["windir"] = windows;
        start.Environment["SystemDrive"] = Path.GetPathRoot(windows)![..2];
        start.Environment["PATH"] = SystemDir;
        start.Environment["TEMP"] = start.Environment["TMP"] = Path.Combine(windows, "Temp");
        foreach (var arg in new[] { "/English", "/Online", "/NoRestart" }.Concat(operation)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        return (process.ExitCode, stdout.Result);
    }

    private static Version? ReadInventory(string arch)
    {
        var result = Dism("/Get-ProvisionedAppxPackages");
        if (result.Code != 0) throw new IOException();
        return ParseInventory(result.Output, arch);
    }

    private static Version? ParseInventory(string output, string arch)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Fail closed on missing output, localization, truncated output or partial rows.
        if (!lines.Any(s => s.StartsWith("Image Version:", StringComparison.Ordinal)) || lines.LastOrDefault() != "The operation completed successfully.") throw new InvalidDataException();
        Version? highest = null; string? display = null;
        foreach (var line in lines)
        {
            int colon = line.IndexOf(':'); if (colon < 0) continue;
            var key = line[..colon].Trim(); var value = line[(colon + 1)..].Trim();
            if (key == "DisplayName")
            {
                if (display != null || value.Length == 0) throw new InvalidDataException();
                display = value;
            }
            if (key != "PackageName") continue;
            var fields = value.Split('_');
            if (display == null || fields.Length != 5 || !string.Equals(display, fields[0], StringComparison.OrdinalIgnoreCase)
                || !Version.TryParse(fields[1], out var v) || v.Revision < 0) throw new InvalidDataException();
            display = null;
            if (fields[0].Equals(Name, StringComparison.OrdinalIgnoreCase) && fields[2].Equals(arch, StringComparison.OrdinalIgnoreCase)
                && fields[3].Length == 0 && fields[4].Equals(PublisherId, StringComparison.OrdinalIgnoreCase) && (highest == null || v > highest)) highest = v;
        }
        if (display != null) throw new InvalidDataException();
        return highest;
    }

    // Lock ancestors from the volume root down before resolving each next component.
    // Refuse alternate namespaces, UNC/mapped drives, streams, reparse points and aliases.
    // Directory handles deny rename/delete; file handles additionally deny all writers.
    private sealed class LocalLocks : IDisposable
    {
        private readonly Stack<IDisposable> held = new();
        internal FileStream File(string path)
        {
            if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.AsSpan(2).Contains(':')
                || path.Contains('/') || path.Contains('"') || path.Any(char.IsControl)) throw new IOException();
            var full = Path.GetFullPath(path);
            if (!string.Equals(path, full, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            var root = Path.GetPathRoot(full)!;
            if (new DriveInfo(root).DriveType != DriveType.Fixed) throw new IOException();
            var directory = Path.GetDirectoryName(full) ?? throw new IOException();
            var parents = new Stack<string>();
            for (string? dir = directory; dir != null; dir = Path.GetDirectoryName(dir)) parents.Push(dir);
            while (parents.TryPop(out var dir))
            {
                // FILE_TRAVERSE is a real access right: an attributes-only handle does not enforce share denial.
                var h = CreateFileW(dir, 0xA0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); held.Push(h);
                Check(h, dir, directory: true);
            }
            var file = CreateFileW(full, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero); held.Push(file);
            Check(file, full, directory: false);
            // FileStream constructed from a handle has no path; reopen under the now-pinned name.
            var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read); held.Push(stream);
            return stream;
        }
        private static void Check(SafeFileHandle h, string expected, bool directory)
        {
            if (h.IsInvalid || !GetFileInformationByHandleEx(h, 9, out var info, 8) || (info.Attributes & 0x400) != 0
                || ((info.Attributes & 0x10) != 0) != directory) throw new IOException();
            var buffer = new StringBuilder(32768);
            uint n = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Capacity, 0);
            if (n == 0 || n >= buffer.Capacity || !string.Equals(buffer.ToString().TrimEnd('\\'), (@"\\?\" + expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException();
        }
        public void Dispose() { while (held.TryPop(out var item)) item.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

    // Pure fixtures only: no filesystem, token queries, child processes or UI.
    internal static int SelfTest()
    {
        var v = new Version(1, 2, 0, 0); int adds = 0;
        int Add() { adds++; return Complete; }
        if (Provision(v, () => throw new IOException(), () => true, Add) != InventoryFailed ||
            Provision(v, () => v, () => true, Add) != Already || Provision(v, () => new Version(2, 0), () => true, Add) != Already ||
            Provision(v, () => null, () => false, Add) != LowSpace || adds != 0) return 1;
        int Branch(int code, Version? post) { bool first = true; return Provision(v, () => { if (!first) return post; first = false; return null; }, () => true, () => code); }
        if (Branch(5, v) != Failed || Branch(0, v) != Complete || Branch(0, null) != VerificationFailed || Branch(3010, v) != Restart || Branch(3010, null) != RestartUnverified) return 2;
        const string frame = "Image Version: 10.0.26100.0\n", end = "The operation completed successfully.";
        string Row(string full) => $"DisplayName : {Name}\nPackageName : {full}\n";
        var rows = Row($"{Name}_1.2.0.0_x64__{PublisherId}") + Row($"{Name}_9.0.0.0_arm64__{PublisherId}") + Row($"{Name}_9.0.0.0_x64__other");
        if (ParseInventory(frame + rows + end, "x64") != v || ParseInventory(frame + end, "x64") != null) return 3;
        try { ParseInventory(frame + "DisplayName : OpenAI.Codex\n" + end, "x64"); return 4; } catch (InvalidDataException) { }
        return 0;
    }
}
