using System.Runtime.InteropServices;
namespace PackagePicker;
internal static class Trust {
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct FileInfo {public uint Size; public string Path; public IntPtr Handle,Subject;}
    [StructLayout(LayoutKind.Sequential)] struct Data {public uint Size; public IntPtr Policy,Client; public uint Ui,Revocation,Choice; public IntPtr File; public uint Action; public IntPtr State,Url; public uint Flags,Context; public IntPtr SignatureSettings;}
    [DllImport("wintrust.dll",ExactSpelling=true)] static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref Data data);
    // Local Windows trust policy, no UI or network retrieval. Never treat unknown as valid.
    internal static bool Verify(string path){
        var info=new FileInfo{Size=(uint)Marshal.SizeOf<FileInfo>(),Path=path};
        var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>()); Marshal.StructureToPtr(info,ptr,false);
        try {var data=new Data{Size=(uint)Marshal.SizeOf<Data>(),Ui=2,Choice=1,File=ptr,Flags=0x1000}; var policy=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");return WinVerifyTrust(new IntPtr(-1),ref policy,ref data)==0;}
        finally {Marshal.DestroyStructure<FileInfo>(ptr);Marshal.FreeHGlobal(ptr);}
    }
}
