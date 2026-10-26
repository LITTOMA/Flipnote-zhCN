using Ctr.Core;
using Ctr.Core.Containers.Cia;
using Ctr.Core.Containers.CodeSet;
using Ctr.Core.Containers.ExeFs;
using Ctr.Core.Containers.Ncch;
using Ctr.Core.Containers.RomFs;
using Ctr.Core.IO;
using System.Security.Cryptography;

namespace Flipnote.Build;

internal sealed class CiaImage
{
    public CiaDocument Document { get; }
    public string SourcePath { get; }
    public string Sha256 { get; }
    public Dictionary<string,ResourceNode> Resources { get; }=new(StringComparer.Ordinal);
    private readonly byte[] _certificates,_ticket,_tmd;
    private readonly byte[]? _meta;
    private readonly List<ContentImage> _contents=[];
    public CiaImage(string path):this(Path.GetFullPath(path),File.OpenRead(path)) { }
    public CiaImage(byte[] bytes):this("<memory>",new MemoryStream(bytes,false)) { }
    private CiaImage(string path,Stream source)
    {
        SourcePath=path;
        using var input=source;
        Sha256=Convert.ToHexString(SHA256.HashData(input));input.Position=0;
        Document=CiaReader.Read(input);
        _certificates=Collect(s=>CiaReader.CopySection(input,Document.CertificateChain,s));
        _ticket=Collect(s=>CiaReader.CopySection(input,Document.TicketSection,s));
        _tmd=Collect(s=>CiaReader.CopySection(input,Document.TmdSection,s));
        _meta=Document.MetaSection is {} meta?Collect(s=>CiaReader.CopySection(input,meta,s)):null;
        byte[]? titleKey=Document.Contents.Any(c=>c.IsEncrypted)?RetailCiaKeys.DecryptTitleKey(Document.Ticket):null;
        try
        {
            foreach(var content in Document.Contents)
            {
                byte[] data=Collect(s=>CiaReader.CopyContent(input,content,s,titleKey));
                var image=new ContentImage(content,data);_contents.Add(image);
                string root=$"content/{content.Index:D4}";
                if(data.Length<0x104 || !data.AsSpan(0x100,4).SequenceEqual("NCCH"u8)) { Add(root+"/data",data);continue; }
                using var ncchStream=new MemoryStream(data,false);
                image.Ncch=NcchReader.Read(ncchStream);
                var keys=RetailNcchKeys.Create(image.Ncch.Header);
                foreach(var region in image.Ncch.Regions)
                    image.Regions[region.Kind]=Collect(s=>NcchReader.CopyRegion(ncchStream,image.Ncch,region.Kind,s,keys));
                foreach(var (kind,bytes) in image.Regions)
                {
                    if(kind==NcchRegionKind.ExeFs)
                    {
                        using var fs=new MemoryStream(bytes,false);var exefs=ExeFsReader.Read(fs);
                        foreach(var entry in exefs.Entries)
                        {
                            var node=Add(root+"/exefs/"+entry.Name,Collect(s=>ExeFsReader.CopyEntry(fs,entry,s)),expand:false);
                            if(entry.Name is ".code" or "code" && image.Regions.TryGetValue(NcchRegionKind.ExtendedHeader,out var exh) && CodeSetExtendedHeader.IsCompressed(exh))node.DecodeCode();
                        }
                    }
                    else if(kind==NcchRegionKind.RomFs)
                    {
                        using var fs=new MemoryStream(bytes,false);var romfs=RomFsReader.Read(fs);
                        foreach(var entry in romfs.Files)Add(root+"/romfs/"+entry.Path.TrimStart('/'),Collect(s=>RomFsReader.CopyFile(fs,entry,s)));
                    }
                    else Add(root+"/"+RegionName(kind),bytes,expand:false);
                }
            }
        }
        finally { if(titleKey!=null)CryptographicOperations.ZeroMemory(titleKey); }
    }
    public ResourceNode Add(string path,byte[] data,bool expand=true)
    {
        var node=new ResourceNode(path,data,expand);Register(node);return node;
    }
    public void AddFile(string path,byte[] data)
    {
        string[] parts=path.Split('/');
        if(parts.Length<4 || parts[0]!="content" || parts[2]!="romfs" || path.Contains('!') || !_contents.Any(c=>c.Content.Index.ToString("D4")==parts[1] && c.Ncch!=null))throw new InvalidDataException("Only existing contents' top-level RomFS file additions are supported.");
        foreach(string part in parts)Formats.Darc.ValidateName(part);
        var node=Add(path,data);node.MarkAdded();
    }
    private void Register(ResourceNode node)
    {
        if(!Resources.TryAdd(node.Path,node))throw new InvalidDataException("Duplicate resource: "+node.Path);
        foreach(var child in node.Children)Register(child);
    }
    public byte[] Build()
    {
        var contents=new List<CiaBuildContent>();
        foreach(var image in _contents)
        {
            string root=$"content/{image.Content.Index:D4}";
            var nodes=Resources.Values.Where(n=>n.Path.StartsWith(root+"/",StringComparison.Ordinal) && !n.Path.Contains('!')).ToArray();
            byte[] rebuilt=image.Bytes;
            if(nodes.Any(n=>n.IsChanged))
            {
                if(image.Ncch==null)rebuilt=Resources[root+"/data"].Build();
                else
                {
                    var regions=new Dictionary<NcchRegionKind,byte[]>(image.Regions);
                    foreach(var kind in regions.Keys.ToArray())
                    {
                        if(kind is NcchRegionKind.ExeFs or NcchRegionKind.RomFs)continue;
                        regions[kind]=Resources[root+"/"+RegionName(kind)].Build();
                    }
                    var exefsNodes=nodes.Where(n=>n.Path.StartsWith(root+"/exefs/",StringComparison.Ordinal)).ToArray();
                    if(exefsNodes.Any(n=>n.IsChanged))regions[NcchRegionKind.ExeFs]=Collect(s=>ExeFsWriter.Write(s,exefsNodes.Select(n=>new ExeFsBuildEntry(n.Path[(root.Length+7)..],new MemoryBinarySource(n.Build()))).ToArray()));
                    var romfsNodes=nodes.Where(n=>n.Path.StartsWith(root+"/romfs/",StringComparison.Ordinal)).ToArray();
                    if(romfsNodes.Any(n=>n.IsChanged))regions[NcchRegionKind.RomFs]=Collect(s=>RomFsWriter.Write(s,romfsNodes.Select(n=>new RomFsBuildFile(n.Path[(root.Length+7)..],new MemoryBinarySource(n.Build()))).ToArray()));
                    IReadOnlyBinarySource? Region(NcchRegionKind kind)=>regions.TryGetValue(kind,out var value)?new MemoryBinarySource(value):null;
                    rebuilt=Collect(s=>NcchWriter.Write(s,new NcchBuildPlan(image.Ncch.Header,Region(NcchRegionKind.ExtendedHeader),Region(NcchRegionKind.Logo),Region(NcchRegionKind.Plain),Region(NcchRegionKind.ExeFs),Region(NcchRegionKind.RomFs))));
                }
            }
            contents.Add(new(image.Content.Id,image.Content.Index,image.Content.Type,new MemoryBinarySource(rebuilt)));
        }
        return Collect(s=>CiaWriter.WriteCfwInstallable(s,new CiaBuildPlan(new MemoryBinarySource(_certificates),_ticket,_tmd,contents,_meta==null?null:new MemoryBinarySource(_meta),Type:Document.Type,Version:Document.Version)));
    }
    private static string RegionName(NcchRegionKind kind)=>kind==NcchRegionKind.ExtendedHeader?"exheader":kind.ToString().ToLowerInvariant();
    internal static byte[] Collect(Action<MemoryStream> write) { using var stream=new MemoryStream();write(stream);return stream.ToArray(); }
    private sealed class ContentImage(CiaContent content,byte[] bytes)
    {
        public CiaContent Content { get; }=content;
        public byte[] Bytes { get; }=bytes;
        public NcchDocument? Ncch { get; set; }
        public Dictionary<NcchRegionKind,byte[]> Regions { get; }=[];
    }
}
