using System.Buffers.Binary;
using System.Text;

namespace Flipnote.Build.Formats;

internal sealed class Darc
{
    private readonly byte[] _original;
    private readonly int _table,_dataOffset;
    private readonly List<(int Record,int Alignment,ResourceNode Node)> _files=[];
    public IReadOnlyList<ResourceNode> Children=>_files.Select(f=>f.Node).ToArray();
    public Darc(byte[] bytes,string parent,int depth)
    {
        _original=bytes;
        if(bytes.Length<40 || !bytes.AsSpan(0,4).SequenceEqual("darc"u8) || U16(4)!=0xfeff || U16(6)!=28)throw new InvalidDataException("Unsupported DARC header: "+parent);
        if(U32(12)!=bytes.Length)throw new InvalidDataException("DARC size mismatch: "+parent);
        _table=U32(16);int tableLength=U32(20);_dataOffset=U32(24);Range(_table,tableLength);
        int count=U32(_table+8);if(count<1 || count>100000 || count*12>tableLength)throw new InvalidDataException("Invalid DARC entry table.");
        int names=checked(_table+count*12);var stack=new Stack<(int Index,int End,string Path)>();stack.Push((0,count,""));
        for(int i=1;i<count;i++)
        {
            while(stack.Count>0 && i>=stack.Peek().End)stack.Pop();
            if(stack.Count==0)throw new InvalidDataException("Invalid DARC directory bounds.");
            int record=_table+i*12,flags=U32(record);int start=checked(names+(flags&0xffffff)),end=start;
            while(end+1<_table+tableLength && U16(end)!=0)end+=2;
            if(end+1>=_table+tableLength)throw new InvalidDataException("Unterminated DARC name.");
            string name=Encoding.Unicode.GetString(bytes,start,end-start);
            if(name=="." && (flags&0x1000000)!=0)name="";
            else try { ValidateName(name); } catch(InvalidDataException e) { throw new InvalidDataException($"{parent}: entry {i}, name '{name}': {e.Message}"); }
            string path=stack.Peek().Path+name;
            if((flags&0x1000000)!=0)
            {
                int limit=U32(record+8);if(limit<=i || limit>stack.Peek().End || U32(record+4)!=stack.Peek().Index)throw new InvalidDataException("Invalid DARC directory.");
                stack.Push((i,limit,path.Length==0?"":path+"/"));
            }
            else
            {
                int offset=U32(record+4),length=U32(record+8);Range(offset,length);
                if(offset<_dataOffset)throw new InvalidDataException("DARC file overlaps metadata.");
                int alignment=4;while(alignment<128 && offset%(alignment*2)==0)alignment*=2;
                _files.Add((record,alignment,new ResourceNode(parent+"!/"+path,bytes.AsSpan(offset,length).ToArray(),depth:depth+1)));
            }
        }
        if(_files.Select(f=>f.Node.Path).Distinct(StringComparer.Ordinal).Count()!=_files.Count)throw new InvalidDataException("Duplicate DARC paths.");
    }
    public byte[] Build()
    {
        if(!_files.Any(f=>f.Node.IsChanged))return _original;
        using var stream=new MemoryStream();stream.Write(_original.AsSpan(0,_dataOffset));
        foreach(var (record,alignment,node) in _files)
        {
            while(stream.Position%alignment!=0)stream.WriteByte(0);
            int offset=checked((int)stream.Position);byte[] bytes=node.Build();stream.Write(bytes);long end=stream.Position;
            stream.Position=record+4;stream.Write(BitConverter.GetBytes(offset));stream.Write(BitConverter.GetBytes(bytes.Length));stream.Position=end;
        }
        int size=checked((int)stream.Length);stream.Position=12;stream.Write(BitConverter.GetBytes(size));return stream.ToArray();
    }
    private ushort U16(int offset) { Range(offset,2);return BinaryPrimitives.ReadUInt16LittleEndian(_original.AsSpan(offset)); }
    private int U32(int offset) { Range(offset,4);return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_original.AsSpan(offset))); }
    private void Range(int offset,int size) { if(offset<0 || size<0 || offset>_original.Length-size)throw new InvalidDataException("DARC field is outside its file."); }
    internal static void ValidateName(string name) { if(string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0','!'])>=0)throw new InvalidDataException("Invalid archive file name."); }
}
