using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using Flipnote.Build.Formats;
using LibKwz;

namespace Flipnote.Build;

internal sealed record ProjectSettings(int SchemaVersion,string Input,string InputSha256,string TitleId,string Overlay,string Output);
internal sealed record OverlayManifest(int SchemaVersion,Dictionary<string,string> SourceRoots,OverlayOperation[] Operations);
internal sealed record OverlayOperation(string Kind,string Target,string Source,string? BaseSha256=null,bool Add=false);
internal sealed record BinaryPatch(string BaseSha256,string ResultSha256,int Length,PatchRange[] Ranges);
internal sealed record PatchRange(int Offset,string Before,string After);
internal sealed record SourceDependency(string Source,string Sha256,int Bytes);
internal sealed record ChangeReport(string Kind,string Target,string Source,string SourceSha256,string? BeforeSha256,string AfterSha256,int Size,SourceDependency[] Dependencies);

internal sealed class Project
{
    public static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true,WriteIndented=true,Encoder=JavaScriptEncoder.Create(UnicodeRanges.All),UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public string Root { get; }
    public ProjectSettings Settings { get; }
    public OverlayManifest Manifest { get; }
    public string ManifestPath { get; }
    public string InputPath { get; }
    public Project(string path,string? input=null)
    {
        path=Path.GetFullPath(path);Root=Path.GetDirectoryName(path)!;
        Settings=Read<ProjectSettings>(path);
        InputPath=input==null?Path.GetFullPath(Path.Combine(Root,Settings.Input)):Path.GetFullPath(input);
        if(Settings.SchemaVersion!=1 || !IsHash(Settings.InputSha256) || !Regex.IsMatch(Settings.TitleId,"^[0-9a-fA-F]{16}$"))throw new InvalidDataException("Invalid project version or input identity.");
        ManifestPath=Path.GetFullPath(Path.Combine(Root,Settings.Overlay));Manifest=Read<OverlayManifest>(ManifestPath);
        if(Manifest.SchemaVersion!=1 || Manifest.Operations==null || Manifest.SourceRoots==null)throw new InvalidDataException("Invalid overlay manifest.");
        if(Manifest.Operations.Select(o=>o.Target).Distinct(StringComparer.Ordinal).Count()!=Manifest.Operations.Length)throw new InvalidDataException("Overlay targets must be unique.");
        foreach(var op in Manifest.Operations)
        {
            if(op.Kind is not ("msbt" or "binary" or "patch" or "bcfnt" or "bclim" or "banner-textures" or "kwz" or "kwz-project" or "kwz-bitmap"))throw new InvalidDataException("Unknown overlay kind: "+op.Kind);
            if(!op.Add && !IsHash(op.BaseSha256))throw new InvalidDataException("Existing resources require baseSha256: "+op.Target);
            if(op.Add && op.BaseSha256!=null)throw new InvalidDataException("An added resource must not have a base hash.");
            Source(op);
        }
        foreach(string target in Manifest.Operations.Select(o=>o.Target))
            if(Manifest.Operations.Any(o=>o.Target!=target && o.Target.StartsWith(target+"!/",StringComparison.Ordinal)))throw new InvalidDataException("Overlay conflicts with a nested target: "+target);
    }
    public CiaImage OpenInput()
    {
        var image=new CiaImage(InputPath);
        if(!image.Sha256.Equals(Settings.InputSha256,StringComparison.OrdinalIgnoreCase) || image.Document.Tmd.TitleId.ToString("X16")!=Settings.TitleId.ToUpperInvariant())throw new InvalidDataException("CIA identity differs from project.json; select the correct original input.");
        return image;
    }
    public string Source(OverlayOperation operation)
    {
        int colon=operation.Source.IndexOf(':');if(colon<=0 || !Manifest.SourceRoots.TryGetValue(operation.Source[..colon],out string? location))throw new InvalidDataException("Unknown overlay source root: "+operation.Source);
        string sourceRoot=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ManifestPath)!,location));
        string relative=operation.Source[(colon+1)..];foreach(string part in relative.Split('/'))Darc.ValidateName(part);
        string source=Path.GetFullPath(Path.Combine(sourceRoot,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!source.StartsWith(sourceRoot+Path.DirectorySeparatorChar,FileSystemPaths.Comparison))throw new InvalidDataException("Source escaped its declared root.");
        if(!File.Exists(source))throw new FileNotFoundException("Overlay source is missing.",source);
        return source;
    }
    public List<ChangeReport> Apply(CiaImage image)
    {
        AnimationTextAudit.RequireComplete(AnimationTextAudit.Check(image,Path.Combine(Root,"overlays","zh-CN","animations")));
        var reports=new List<ChangeReport>();
        foreach(var operation in Manifest.Operations)
        {
            bool found=image.Resources.TryGetValue(operation.Target,out var node);
            if(operation.Add==found)throw new InvalidDataException(operation.Add?"Addition already exists: "+operation.Target:"Unknown overlay target: "+operation.Target);
            string? before=node==null?null:Hash(node.Data);
            if(!operation.Add && !before!.Equals(operation.BaseSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Base resource hash mismatch: "+operation.Target);
            string path=Source(operation);byte[] source=File.ReadAllBytes(path);
            byte[] result=operation.Kind switch
            {
                "msbt"=>new Msbt(node?.Data??throw new InvalidDataException("MSBT additions need a base template.")).Apply(JsonSerializer.Deserialize<JsonElement>(source)),
                "patch"=>ApplyPatch(node?.Data??throw new InvalidDataException("Patch requires a base file."),Read<BinaryPatch>(path)),
                "bcfnt"=>Bcfnt.Compile(path),
                "bclim"=>Bclim.Compile(node?.Data??throw new InvalidDataException("BCLIM needs an original texture template."),path),
                "banner-textures"=>Banner.Compile(node?.Data??throw new InvalidDataException("Banner needs an original CBMD template."),path),
                "kwz-project"=>KwzProject.Compile(path),
                "kwz-bitmap"=>KwzBitmap.Compile(node?.Data??throw new InvalidDataException("Bitmap recipe requires a base animation."),path),
                "kwz"=>SignKwz(source),
                _=>source
            };
            if(operation.Target.EndsWith(".msbt",StringComparison.OrdinalIgnoreCase))_ = new Msbt(result).Texts();
            if(operation.Target.EndsWith(".bcfnt",StringComparison.OrdinalIgnoreCase))Bcfnt.Validate(result);
            if(operation.Kind=="bclim")_ = new Bclim(result);
            if(operation.Target.EndsWith(".kwz",StringComparison.OrdinalIgnoreCase)) { using var note=new KwzFile(new MemoryStream(result),true);if(!note.VerifySignature())throw new InvalidDataException("KWZ overlay signature is invalid."); }
            if(operation.Add)image.AddFile(operation.Target,result);else node!.Replace(result);
            reports.Add(new(operation.Kind,operation.Target,operation.Source,Hash(source),before,Hash(result),result.Length,Dependencies(operation)));
        }
        CheckFontCoverage();FontMemoryBudget.Validate(image);return reports;
    }
    private SourceDependency[] Dependencies(OverlayOperation operation)
    {
        string path=Source(operation);var paths=new List<string> { path };
        if(operation.Kind=="bcfnt")
        {
            using var font=JsonDocument.Parse(File.ReadAllBytes(path));int count=font.RootElement.GetProperty("textureInfo").GetProperty("sheetCount").GetInt32();
            for(int i=0;i<count;i++)paths.Add(path[..^14]+"_sheet"+i+".png");
            if(File.Exists(FontUnification.PolicyPath(path)))paths.Add(FontUnification.PolicyPath(path));
        }
        string directory=Path.GetDirectoryName(path)!;
        if(operation.Kind=="banner-textures")paths.AddRange(Read<Banner.Recipe>(path).Textures.Select(e=>Banner.ImagePath(path,e)).Distinct());
        if(operation.Kind=="kwz-bitmap")paths.AddRange(new[]{"coverage.json","translation-layout.json"}.Select(name=>Path.Combine(directory,name)));
        return paths.Select(p=>{byte[] bytes=File.ReadAllBytes(p);return new SourceDependency(Path.GetRelativePath(directory,p).Replace('\\','/'),Hash(bytes),bytes.Length);}).ToArray();
    }
    public void Verify(CiaImage expected,CiaImage actual)
    {
        if(expected.Document.Tmd.TitleId!=actual.Document.Tmd.TitleId || expected.Document.Contents.Select(c=>(c.Id,c.Index)).SequenceEqual(actual.Document.Contents.Select(c=>(c.Id,c.Index)))==false)throw new InvalidDataException("CIA content identity changed.");
        if(!expected.Resources.Keys.Order().SequenceEqual(actual.Resources.Keys.Order()))throw new InvalidDataException("Rebuilt resource inventory differs.");
        foreach(var (path,node) in expected.Resources)
        {
            var reread=actual.Resources[path];
            if(!node.Build().AsSpan().SequenceEqual(reread.Original))throw new InvalidDataException("Rebuilt resource bytes differ: "+path);
        }
    }
    public string OutputPath(string? requested=null)
    {
        string path=Path.GetFullPath(Path.Combine(Root,requested??Settings.Output));
        if(path.Equals(InputPath,FileSystemPaths.Comparison))throw new InvalidDataException("Output cannot overwrite the original CIA.");
        string artifacts=Path.Combine(Root,"artifacts")+Path.DirectorySeparatorChar;
        if(!path.StartsWith(artifacts,FileSystemPaths.Comparison) || !path.EndsWith(".cia",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("CIA outputs must be under artifacts/ and use the .cia extension.");
        return path;
    }
    private void CheckFontCoverage()
    {
        var fonts=Manifest.Operations.Where(o=>o.Kind=="bcfnt").ToArray();if(fonts.Length==0)return;
        var chars=FontCoverage.Required(this);
        foreach(var op in fonts)
        {
            using var font=JsonDocument.Parse(File.ReadAllBytes(Source(op)));var map=font.RootElement.GetProperty("glyphMap");
            char[] missing=chars.Where(c=>!map.TryGetProperty(c.ToString(),out _)).Order().ToArray();
            if(missing.Length!=0)throw new InvalidDataException("Font lacks translated glyphs: "+new string(missing));
            FontUnification.Validate(Source(op));
        }
    }
    public static byte[] ApplyPatch(byte[] original,BinaryPatch patch)
    {
        if(original.Length!=patch.Length || !Hash(original).Equals(patch.BaseSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Code patch base hash/length mismatch.");
        byte[] result=(byte[])original.Clone();int previousEnd=0;
        foreach(var range in patch.Ranges)
        {
            byte[] before=Convert.FromHexString(range.Before),after=Convert.FromHexString(range.After);
            if(before.Length==0 || before.Length!=after.Length || range.Offset<previousEnd || range.Offset>result.Length-before.Length || !original.AsSpan(range.Offset,before.Length).SequenceEqual(before))throw new InvalidDataException("Invalid/overlapping code patch range.");
            after.CopyTo(result,range.Offset);previousEnd=range.Offset+after.Length;
        }
        if(!Hash(result).Equals(patch.ResultSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Code patch result hash mismatch.");return result;
    }
    public static BinaryPatch CapturePatch(byte[] before,byte[] after)
    {
        if(before.Length!=after.Length)throw new InvalidDataException("Fixed-size patch cannot change resource length.");var ranges=new List<PatchRange>();
        for(int i=0;i<before.Length;i++)if(before[i]!=after[i])
        {
            int start=i;while(i+1<before.Length && before[i+1]!=after[i+1])i++;
            ranges.Add(new(start,Convert.ToHexString(before.AsSpan(start,i-start+1)),Convert.ToHexString(after.AsSpan(start,i-start+1))));
        }
        return new(Hash(before),Hash(after),before.Length,ranges.ToArray());
    }
    private static byte[] SignKwz(byte[] data) { using var note=new KwzFile(new MemoryStream(data),true);return note.ToSignedArray(); }
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsHash(string? value)=>value is { Length:64 } && value.All(Uri.IsHexDigit);
    public static T Read<T>(string path)=>JsonSerializer.Deserialize<T>(File.ReadAllBytes(path),Json)??throw new InvalidDataException("Empty JSON: "+path);
    public static void Save(string path,byte[] bytes)
    {
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(bytes);file.Flush(true); }File.Move(temporary,path,true); }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
    public static void SaveJson(string path,object value)=>Save(path,JsonSerializer.SerializeToUtf8Bytes(value,Json));
}
