using System.IO;
using System.IO.Compression;
namespace PackagePicker;
internal static class CatalogTests {
    internal static int Run(){var dir=Directory.CreateTempSubdirectory();try{
        Package Mock(string filename,string version,string arch="x64",string publisher=Catalog.Publisher,string min="10.0.19041.0",bool trusted=true){var path=Path.Combine(dir.FullName,filename+".msix");using(var z=ZipFile.Open(path,ZipArchiveMode.Create))using(var w=new StreamWriter(z.CreateEntry("AppxManifest.xml").Open()))w.Write($"<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Identity Name='OpenAI.Codex' Publisher='{publisher}' ProcessorArchitecture='{arch}' Version='{version}'/><Dependencies><TargetDeviceFamily Name='Windows.Desktop' MinVersion='{min}'/></Dependencies></Package>");return Catalog.Inspect(path,"x64",new Version("10.0.26100.0"),_=>trusted);}
        var newest=Mock("unknown-name","1.10.0.0");var older=Mock("newest","1.9.0.0");var wrong=Mock("arm","9.0.0.0","arm64");var other=Mock("other","8.0.0.0",publisher:"Other");var future=Mock("future","7.0.0.0",min:"12.0.0.0");var unsigned=Mock("unsigned","6.0.0.0",trusted:false);
        var list=Catalog.Rank(new[]{wrong,older,unsigned,other,future,newest});if(list.Count!=6||list[0]!=newest||list[1]!=older||list.Count(x=>x.Eligible)!=2)return 2;
        if(Trust.Verify(unsigned.Path))return 3;
        var broken=Path.Combine(dir.FullName,"broken.msix");File.WriteAllText(broken,"invalid");if(Catalog.Inspect(broken,"x64",new Version(),_=>true).Eligible)return 4;
        return 0;
    }finally{dir.Delete(true);}}
}
