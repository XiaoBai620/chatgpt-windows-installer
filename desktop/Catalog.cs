using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace PackagePicker;
internal record Package(string Path, Version? Version, string Arch, string State, bool Eligible) {
    public string FileName => System.IO.Path.GetFileName(Path);
}
internal static class Catalog {
    internal const string Publisher="CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    static readonly XNamespace Ns="http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    internal static Package Inspect(string path,string architecture,Version os,Func<string,bool> trust) {
        Version? version=null; string arch="—";
        Package Row(string why,bool eligible=false)=>new(path,version,arch,why,eligible);
        try {
            using var zip=ZipFile.OpenRead(path);
            var entry=zip.GetEntry("AppxManifest.xml"); if(entry==null||entry.Length>4_000_000) return Row("清单缺失或过大");
            using var stream=entry.Open(); using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4_000_000});
            var root=XElement.Load(reader); var id=root.Element(Ns+"Identity");
            if(root.Name!=Ns+"Package" || id?.Attribute("Name")?.Value!="OpenAI.Codex" || !string.Equals((string?)id.Attribute("Publisher"),Publisher,StringComparison.OrdinalIgnoreCase))return Row("不是目标包身份");
            arch=(string?)id.Attribute("ProcessorArchitecture")??"—";
            if(!System.Version.TryParse((string?)id.Attribute("Version"),out version)||version.Revision<0)return Row("版本无效");
            if(!string.Equals(arch,architecture,StringComparison.OrdinalIgnoreCase))return Row("不匹配本机架构");
            var deps=root.Element(Ns+"Dependencies");
            var supported=deps?.Elements(Ns+"TargetDeviceFamily").Any(d=>(string?)d.Attribute("Name") is "Windows.Desktop" or "Windows.Universal" && System.Version.TryParse((string?)d.Attribute("MinVersion"),out var min)&&os>=min)??false;
            if(!supported)return Row("未满足系统要求");
            if(deps!.Elements().Any(x=>x.Name!=Ns+"TargetDeviceFamily"))return Row("需另核对安装依赖");
            if(!trust(path))return Row("签名未确认");
            return Row("可选 · 签名已验证",true);
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException){return Row("文件损坏或无法读取");}
    }
    internal static List<Package> Rank(IEnumerable<Package> rows)=>rows.OrderByDescending(x=>x.Eligible).ThenByDescending(x=>x.Version).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToList();
}
