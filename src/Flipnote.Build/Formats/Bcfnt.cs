using System.Buffers.Binary;
using System.Text.Json;
using SkiaSharp;

namespace Flipnote.Build.Formats;

internal static class Bcfnt
{
    public static byte[] Compile(string manifest)
    {
        using var json=JsonDocument.Parse(File.ReadAllBytes(manifest));var root=json.RootElement;
        if(root.GetProperty("fileType").GetString()!="cfnt" || root.GetProperty("version").GetInt32()!=0x03000000)throw new InvalidDataException("Unsupported font manifest version/type.");
        var font=root.GetProperty("fontInfo");var texture=root.GetProperty("textureInfo");var sheet=texture.GetProperty("sheetInfo");var glyph=texture.GetProperty("glyph");
        int width=Int(sheet,"width"),height=Int(sheet,"height"),count=Int(texture,"sheetCount");
        string format=sheet.GetProperty("colorFormat").GetString()!;int bits=format switch { "A4"=>4,"A8"=>8,_=>throw new InvalidDataException("Font compiler supports A4/A8 alpha sheets.") };
        if(width<8 || height<8 || width>1024 || height>1024 || (width&(width-1))!=0 || (height&(height-1))!=0 || count<1 || count>128 || (long)width*height*count>64*1024*1024)throw new InvalidDataException("Invalid font sheet dimensions.");
        var widths=root.GetProperty("glyphWidths").EnumerateObject().OrderBy(p=>int.Parse(p.Name)).ToArray();
        if(widths.Length==0 || widths.Length>65535 || widths.Where((p,i)=>p.Name!=i.ToString()).Any())throw new InvalidDataException("Font widths must have consecutive glyph IDs.");
        int cols=Int(sheet,"cols"),rows=Int(sheet,"rows"),cellWidth=Int(glyph,"width")+1,cellHeight=Int(glyph,"height")+1;
        if(cols<1 || rows<1 || cellWidth<2 || cellHeight<2 || (long)cols*cellWidth>width || (long)rows*cellHeight>height || (long)cols*rows*count<widths.Length)throw new InvalidDataException("Glyph grid cannot fit the declared font characters/sheets.");
        if(Int(font,"alterCharIdx")>=widths.Length)throw new InvalidDataException("Font fallback glyph is outside the glyph table.");
        var map=root.GetProperty("glyphMap").EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal).ToArray();
        foreach(var item in map)if(item.Name.Length!=1 || char.IsSurrogate(item.Name[0]) || item.Value.GetInt32()<0 || item.Value.GetInt32()>=widths.Length)throw new InvalidDataException("Invalid font glyph mapping.");
        if(map.Length==0 || map.Length>65535)throw new InvalidDataException("Invalid font map size.");
        string stem=manifest.EndsWith("_manifest.json",StringComparison.Ordinal)?manifest[..^14]:throw new InvalidDataException("Font filename must end with _manifest.json.");
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write("CFNT"u8);writer.Write((ushort)0xfeff);writer.Write((ushort)0x14);writer.Write(0x03000000);writer.Write(0);writer.Write(4);
        writer.Write("FINF"u8);writer.Write(0x20);writer.Write(Byte(font,"fontType"));writer.Write(Byte(font,"lineFeed"));writer.Write((ushort)Int(font,"alterCharIdx"));
        var def=font.GetProperty("defaultWidth");writer.Write(unchecked((byte)checked((sbyte)Int(def,"left"))));writer.Write(Byte(def,"glyphWidth"));writer.Write(Byte(def,"charWidth"));writer.Write(Byte(font,"encoding"));
        writer.Write(0x3c);writer.Write(0);writer.Write(0);writer.Write(Byte(font,"height"));writer.Write(Byte(font,"width"));writer.Write(Byte(font,"ascent"));writer.Write((byte)0);
        int tglp=checked((int)stream.Position),size=width*height*bits/8;
        writer.Write("TGLP"u8);writer.Write(0);writer.Write(Byte(glyph,"width"));writer.Write(Byte(glyph,"height"));writer.Write(Byte(glyph,"baseline"));writer.Write(checked((byte)widths.Max(p=>Int(p.Value,"char"))));
        writer.Write(size);writer.Write((ushort)count);writer.Write((ushort)(bits==4?11:8));writer.Write((ushort)Int(sheet,"cols"));writer.Write((ushort)Int(sheet,"rows"));writer.Write((ushort)width);writer.Write((ushort)height);writer.Write(0x80);
        while(stream.Position<0x80)writer.Write((byte)0);
        for(int page=0;page<count;page++)
        {
            using var bitmap=SKBitmap.Decode(stem+"_sheet"+page+".png")??throw new InvalidDataException("Cannot read font sheet "+page);
            if(bitmap.Width!=width || bitmap.Height!=height)throw new InvalidDataException("Font sheet dimensions differ from manifest.");
            byte[] data=new byte[size];var pixels=bitmap.Pixels;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                int morton=(x&1)|((y&1)<<1)|((x&2)<<1)|((y&2)<<2)|((x&4)<<2)|((y&4)<<3);
                int index=((y/8)*(width/8)+x/8)*64+morton;byte alpha=pixels[y*width+x].Alpha;
                if(bits==4)data[index/2]|=(byte)((alpha/17)<<((index&1)*4));else data[index]=alpha;
            }
            writer.Write(data);
        }
        int cwdh=checked((int)stream.Position);writer.Write("CWDH"u8);writer.Write(0);writer.Write((ushort)0);writer.Write((ushort)(widths.Length-1));writer.Write(0);
        foreach(var item in widths) { writer.Write(checked((sbyte)Int(item.Value,"left")));writer.Write(Byte(item.Value,"glyph"));writer.Write(Byte(item.Value,"char")); }
        while(stream.Position%4!=0)writer.Write((byte)0);
        int cmap=checked((int)stream.Position);writer.Write("CMAP"u8);writer.Write(0);writer.Write((ushort)map[0].Name[0]);writer.Write((ushort)map[^1].Name[0]);writer.Write((ushort)2);writer.Write((ushort)0);writer.Write(0);writer.Write((ushort)map.Length);
        foreach(var item in map) { writer.Write((ushort)item.Name[0]);writer.Write((ushort)item.Value.GetInt32()); }
        while(stream.Position%4!=0)writer.Write((byte)0);
        byte[] output=stream.ToArray();void Put(int p,int n)=>BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(p),n);
        Put(12,output.Length);Put(0x28,cwdh+8);Put(0x2c,cmap+8);Put(tglp+4,cwdh-tglp);Put(cwdh+4,cmap-cwdh);Put(cmap+4,output.Length-cmap);
        Validate(output);return output;
    }
    public static void Validate(byte[] data)
    {
        if(data.Length<128 || !data.AsSpan(0,4).SequenceEqual("CFNT"u8) || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12))!=data.Length)throw new InvalidDataException("Invalid CFNT header/size.");
        foreach(var (offset,magic) in new[]{(0x24,"TGLP"),(0x28,"CWDH"),(0x2c,"CMAP")})
        {
            int p=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset))-8;
            if(p<20 || p>data.Length-16 || System.Text.Encoding.ASCII.GetString(data,p,4)!=magic)throw new InvalidDataException("Invalid CFNT section pointer.");
            int size=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p+4));if(size<16 || size>data.Length-p)throw new InvalidDataException("Invalid CFNT section size.");
        }
    }
    private static int Int(JsonElement item,string name)=>item.GetProperty(name).GetInt32();
    private static byte Byte(JsonElement item,string name)=>checked((byte)Int(item,name));
}
