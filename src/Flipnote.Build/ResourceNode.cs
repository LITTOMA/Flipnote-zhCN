using Ctr.Core.Containers.CodeSet;
using System.Security.Cryptography;
using Flipnote.Build.Formats;

namespace Flipnote.Build;

internal sealed class ResourceNode
{
    public string Path { get; }
    public byte[] Original { get; }
    public byte[] Data { get; private set; }
    public string Compression { get; private set; }="none";
    public IReadOnlyList<ResourceNode> Children=>_archive?.Children??[];
    private Darc? _archive;
    private bool _changed;
    private byte[] _decoded;
    public bool IsChanged=>_changed || Children.Any(c=>c.IsChanged);
    public ResourceNode(string path,byte[] original,bool expand=true,int depth=0)
    {
        Path=path;Original=Data=original;
        if(depth>12 || original.Length>256*1024*1024)throw new InvalidDataException("Resource nesting/size limit exceeded.");
        if(expand)
        {
            if(path.EndsWith(".blz",StringComparison.OrdinalIgnoreCase))
            {
                if(original.Length>4 && original[0]==0x11) { Data=Lz11.Decode(original);Compression="lz11"; }
                else { Data=DecodeBlz(original);Compression="blz"; }
            }
            if(Data.AsSpan().StartsWith("darc"u8))_archive=new Darc(Data,path,depth);
        }
        _decoded=Data;
    }
    public void DecodeCode() { Data=_decoded=DecodeBlz(Original);Compression="blz"; }
    public void MarkAdded()=>_changed=true;
    public void Replace(byte[] bytes) { if(_archive!=null)throw new InvalidDataException("Replace archive members instead of the archive: "+Path);Data=bytes;_changed=!Data.AsSpan().SequenceEqual(_decoded); }
    public byte[] Build()
    {
        if(!IsChanged)return Original;
        byte[] data=_archive?.Build()??Data;
        return Compression switch { "blz"=>CodeSetCompression.Compress(data).ToArray(),"lz11"=>Lz11.Encode(data),_=>data };
    }
    public object Describe()=>new { path=Path,size=Data.Length,storedSize=Original.Length,compression=Compression,sha256=Convert.ToHexString(SHA256.HashData(Data)),magic=Convert.ToHexString(Data.AsSpan(0,Math.Min(8,Data.Length))) };
    private static byte[] DecodeBlz(byte[] bytes)
    {
        if(bytes.Length<8 || (long)bytes.Length+System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length-4))>256*1024*1024)throw new InvalidDataException("BLZ resource exceeds the decoded size limit.");
        return CodeSetCompression.Decompress(bytes).ToArray();
    }
}
