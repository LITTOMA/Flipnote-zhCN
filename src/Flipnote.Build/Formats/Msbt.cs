using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flipnote.Build.Formats;

internal sealed class Msbt
{
    private readonly byte[] _original;
    private readonly bool _little;
    private readonly List<(int Offset,int Size,string Magic)> _sections=[];
    private readonly List<byte[]> _messages=[];
    public Msbt(byte[] bytes)
    {
        _original=bytes;
        if(bytes.Length<32 || !bytes.AsSpan(0,8).SequenceEqual("MsgStdBn"u8))throw new InvalidDataException("Invalid MSBT header.");
        _little=bytes[8]==0xff && bytes[9]==0xfe;
        if(!_little && !(bytes[8]==0xfe && bytes[9]==0xff))throw new InvalidDataException("Invalid MSBT byte order.");
        if(bytes[12]!=1)throw new InvalidDataException("The Note message profile requires UTF-16 MSBT.");
        if(U32(bytes,18)!=bytes.Length)throw new InvalidDataException("MSBT file length mismatch.");
        int at=32,count=U16(bytes,14);
        for(int i=0;i<count;i++)
        {
            Range(bytes,at,16);int size=U32(bytes,at+4);Range(bytes,at+16,size);
            string magic=Encoding.ASCII.GetString(bytes,at,4);_sections.Add((at,size,magic));
            if(magic=="TXT2")
            {
                if(_messages.Count!=0)throw new InvalidDataException("Duplicate MSBT TXT2 section.");
                byte[] data=bytes.AsSpan(at+16,size).ToArray();int n=U32(data,0);if(n<0 || n>(size-4)/4)throw new InvalidDataException("Invalid MSBT message table.");
                for(int m=0;m<n;m++)
                {
                    int start=U32(data,4+m*4),end=m+1<n?U32(data,8+m*4):size;
                    if(start<4+n*4 || start%2!=0 || end<start)throw new InvalidDataException("Invalid MSBT text offsets.");
                    Range(data,start,end-start);_messages.Add(data.AsSpan(start,end-start).ToArray());
                }
            }
            at=checked((at+16+size+15)&~15);
        }
        if(_sections.Count(s=>s.Magic=="TXT2")!=1)throw new InvalidDataException("MSBT must have one TXT2 section.");
    }
    public string[] Texts()=>_messages.Select(Decode).ToArray();
    public string NormalizeText(string text)=>Decode(Encode(text));
    public byte[] Apply(JsonElement replacements)
    {
        if(replacements.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Message overlay must be an array of {id,message}.");
        var updated=_messages.ToArray();var seen=new HashSet<int>();bool changed=false;
        foreach(var item in replacements.EnumerateArray())
        {
            int id=item.GetProperty("id").GetInt32();string text=item.GetProperty("message").GetString()??throw new InvalidDataException("Null message.");
            if(!seen.Add(id) || (uint)id>=_messages.Count)throw new InvalidDataException("Duplicate/out-of-range message id: "+id);
            string old=Decode(_messages[id]);
            if(item.TryGetProperty("before",out var before) && before.GetString()!=old)throw new InvalidDataException("Message before-text mismatch: "+id);
            if(text==old)continue;
            updated[id]=Encode(text);changed=true;
        }
        if(!changed)return _original;
        byte[] txt=CiaImage.Collect(s=>
        {
            int offset=checked(4+updated.Length*4);Write32(s,updated.Length);
            foreach(var message in updated) { Write32(s,offset);offset=checked(offset+message.Length); }
            foreach(var message in updated)s.Write(message);
        });
        byte[] result=CiaImage.Collect(s=>
        {
            s.Write(_original.AsSpan(0,32));
            foreach(var section in _sections)
            {
                byte[] header=_original.AsSpan(section.Offset,16).ToArray();byte[] data=section.Magic=="TXT2"?txt:_original.AsSpan(section.Offset+16,section.Size).ToArray();
                Put32(header,4,data.Length);s.Write(header);s.Write(data);
                byte pad=section.Offset+16+section.Size<_original.Length?_original[section.Offset+16+section.Size]:(byte)0xab;
                while(s.Position%16!=0)s.WriteByte(pad);
            }
        });
        Put32(result,18,result.Length);var reread=new Msbt(result);
        foreach(int id in seen)if(reread.Decode(reread._messages[id])!=Decode(updated[id]))throw new InvalidDataException("MSBT write verification failed.");
        return result;
    }
    private string Decode(byte[] bytes)
    {
        var result=new StringBuilder();int p=0;
        while(p<bytes.Length)
        {
            int code=U16(bytes,p);p+=2;if(code==0)return result.ToString();
            if(code is 14 or 15)
            {
                int group=U16(bytes,p),type=U16(bytes,p+2);p+=4;
                if(code==15)result.Append($"<end:{group:X2}.{type:X2}>");
                else { int size=U16(bytes,p);p+=2;Range(bytes,p,size);result.Append($"<{group:X2}.{type:X2}"+(size==0?">":$":{Convert.ToHexString(bytes.AsSpan(p,size)).ToLowerInvariant()}>"));p+=size; }
            }
            else if((code>=0xe000 && code<=0xf8ff) || code==60)result.Append($"<{code:X4}>");
            else result.Append((char)code);
        }
        throw new InvalidDataException("MSBT message lacks terminator.");
    }
    private byte[] Encode(string text)
    {
        if(text.Length>1000000)throw new InvalidDataException("Message is too long.");
        return CiaImage.Collect(s=>
        {
            string normalized=text.Replace("\r\n","\n");
            for(int p=0;p<normalized.Length;p++)
            {
                char c=normalized[p];
                if(c=='<')
                {
                    int end=normalized.IndexOf('>',p);if(end<0)throw new InvalidDataException("Unclosed MSBT control tag.");
                    string token=normalized[(p+1)..end];var tag=Regex.Match(token,@"^(?<end>end:)?(?<group>[0-9a-fA-F]{2,4})\.(?<type>[0-9a-fA-F]{2,4})(?::(?<data>(?:[0-9a-fA-F]{2})*))?$");
                    if(tag.Success)
                    {
                        bool closing=tag.Groups["end"].Success;byte[] data=Convert.FromHexString(tag.Groups["data"].Value);
                        if(data.Length>ushort.MaxValue || (closing && data.Length!=0))throw new InvalidDataException("Invalid MSBT tag payload.");
                        Write16(s,closing?15:14);Write16(s,Convert.ToUInt16(tag.Groups["group"].Value,16));Write16(s,Convert.ToUInt16(tag.Groups["type"].Value,16));
                        if(!closing){Write16(s,data.Length);s.Write(data);}
                    }
                    else if(Regex.IsMatch(token,"^[0-9a-fA-F]{4}$")) { int value=Convert.ToUInt16(token,16);if(value is 0 or 14 or 15)throw new InvalidDataException("Reserved MSBT literal.");Write16(s,value); }
                    else throw new InvalidDataException("Unsupported MSBT control tag: <"+token+">");
                    p=end;
                }
                else { if(c is '\0' or '\x0e' or '\x0f')throw new InvalidDataException("Use explicit MSBT control tags.");Write16(s,c); }
            }
            Write16(s,0);
        });
    }
    private int U16(byte[] data,int p) { Range(data,p,2);return _little?BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p)):BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p)); }
    private int U32(byte[] data,int p) { Range(data,p,4);return checked((int)(_little?BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)):BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)))); }
    private void Put32(byte[] data,int p,int value) { if(_little)BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(p),value);else BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(p),value); }
    private void Write32(Stream stream,int value) { byte[] bytes=new byte[4];Put32(bytes,0,value);stream.Write(bytes); }
    private void Write16(Stream stream,int value) { Span<byte> bytes=stackalloc byte[2];if(_little)BinaryPrimitives.WriteUInt16LittleEndian(bytes,checked((ushort)value));else BinaryPrimitives.WriteUInt16BigEndian(bytes,checked((ushort)value));stream.Write(bytes); }
    private static void Range(byte[] data,int p,int n) { if(p<0 || n<0 || p>data.Length-n)throw new InvalidDataException("MSBT offset out of range."); }
}
