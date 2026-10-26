namespace Flipnote.Build.Formats;

internal static class Lz11
{
    public static byte[] Decode(byte[] source)
    {
        if(source.Length<4 || source[0]!=0x11)throw new InvalidDataException("Invalid LZ11 header.");
        int length=source[1]|source[2]<<8|source[3]<<16,p=4;
        if(length==0) { if(source.Length<8)throw new InvalidDataException("Truncated LZ11 length.");length=BitConverter.ToInt32(source,4);p=8; }
        if(length<0 || length>256*1024*1024)throw new InvalidDataException("LZ11 exceeds resource size limit.");
        var result=new byte[length];int at=0;
        int Read()=>p<source.Length?source[p++]:throw new InvalidDataException("Truncated LZ11 stream.");
        while(at<length)
        {
            int flags=Read();
            for(int bit=7;bit>=0 && at<length;bit--)
            {
                if((flags&(1<<bit))==0) { result[at++]=(byte)Read();continue; }
                int a=Read(),kind=a>>4,count,offset;
                if(kind==0) { int b=Read(),c=Read();count=((a&15)<<4|b>>4)+0x11;offset=((b&15)<<8|c)+1; }
                else if(kind==1) { int b=Read(),c=Read(),d=Read();count=((a&15)<<12|b<<4|c>>4)+0x111;offset=((c&15)<<8|d)+1; }
                else { count=kind+1;offset=((a&15)<<8|Read())+1; }
                if(offset>at || count>length-at)throw new InvalidDataException("Invalid LZ11 back-reference.");
                for(int i=0;i<count;i++) { result[at]=result[at-offset];at++; }
            }
        }
        return result;
    }
    public static byte[] Encode(byte[] data)
    {
        using var stream=new MemoryStream();
        if(data.Length==0 || data.Length>0xffffff) { stream.Write([0x11,0,0,0]);stream.Write(BitConverter.GetBytes(data.Length)); }
        else stream.Write([(byte)0x11,(byte)data.Length,(byte)(data.Length>>8),(byte)(data.Length>>16)]);
        var chains=new Dictionary<int,List<int>>();int p=0;
        int Key(int i)=>data[i]<<16|data[i+1]<<8|data[i+2];
        while(p<data.Length)
        {
            long flagsAt=stream.Position;stream.WriteByte(0);byte flags=0;
            for(int bit=7;bit>=0 && p<data.Length;bit--)
            {
                int best=0,distance=0,max=Math.Min(0x10110,data.Length-p);
                if(max>=3 && chains.TryGetValue(Key(p),out var candidates))
                {
                    int searched=0;
                    for(int k=candidates.Count-1;k>=0 && searched++<128;k--)
                    {
                        int from=candidates[k],delta=p-from;if(delta>4096)break;
                        int length=0;while(length<max && data[from+length]==data[p+length])length++;
                        if(length>best) { best=length;distance=delta;if(best==max)break; }
                    }
                }
                int used=best>=3?best:1;
                if(best>=3)
                {
                    flags|=(byte)(1<<bit);int d=distance-1;
                    if(best<=16)stream.Write([(byte)((best-1)<<4|d>>8),(byte)d]);
                    else if(best<=0x110) { int n=best-0x11;stream.Write([(byte)(n>>4),(byte)(n<<4|d>>8),(byte)d]); }
                    else { int n=best-0x111;stream.Write([(byte)(0x10|n>>12),(byte)(n>>4),(byte)(n<<4|d>>8),(byte)d]); }
                }
                else stream.WriteByte(data[p]);
                for(int i=0;i<used;i++)
                {
                    if(p+2<data.Length) { int key=Key(p);if(!chains.TryGetValue(key,out var list))chains[key]=list=[];list.Add(p);if(list.Count>8192)list.RemoveRange(0,list.Count-4096); }
                    p++;
                }
            }
            long end=stream.Position;stream.Position=flagsAt;stream.WriteByte(flags);stream.Position=end;
        }
        while(stream.Length%4!=0)stream.WriteByte(0);
        return stream.ToArray();
    }
}
