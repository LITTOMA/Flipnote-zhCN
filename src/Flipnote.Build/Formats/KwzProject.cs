using System.IO.Compression;
using System.Text.Json;
using LibKwz;
using SkiaSharp;

namespace Flipnote.Build.Formats;

/// <summary>CLI renderer for KWZ Studio project version 1; same pixel rules as CanvasObject.Draw.</summary>
internal static class KwzProject
{
    public static byte[] Compile(string path)
    {
        using var file=File.OpenRead(path);if(file.Length>64*1024*1024)throw new InvalidDataException("KWZ project too large.");
        using var zip=new ZipArchive(file,ZipArchiveMode.Read);
        byte[] Read(string name,int limit)
        {
            var entry=zip.GetEntry(name)??throw new InvalidDataException("Missing project entry: "+name);
            if(entry.Length>limit)throw new InvalidDataException("Project entry too large.");
            using var input=entry.Open();byte[] result=new byte[(int)entry.Length];input.ReadExactly(result);if(input.ReadByte()!=-1)throw new InvalidDataException("Project length mismatch.");return result;
        }
        using var metadata=JsonDocument.Parse(Read("project.json",4*1024*1024));var root=metadata.RootElement;
        if(root.GetProperty("Format").GetString()!="KWZStudioProject" || root.GetProperty("Version").GetInt32()!=1)throw new InvalidDataException("Unsupported KWZ project version.");
        using var note=new KwzFile(new MemoryStream(Read("base.kwz",256*1024*1024)),true);
        var objects=JsonSerializer.Deserialize<Item[]>(root.GetProperty("Objects"))??throw new InvalidDataException("Missing project objects.");
        if(objects.Length>10000 || objects.Select(o=>o.Id).Distinct().Count()!=objects.Length)throw new InvalidDataException("Invalid object count/IDs.");
        foreach(var item in objects)item.Validate(note.Header.FrameCount);
        foreach(var group in objects.GroupBy(o=>o.Frame))
        {
            byte[] pixels=note.FrameData.DecodeFrame(group.Key);foreach(var item in group)item.Draw(pixels);note.FrameData.SetFramePixels(group.Key,pixels);
        }
        if(objects.Length>0 || !note.VerifySignature())note.UpdateThumbnail(Math.Min(note.Header.ThumbnailFrameIndex,note.Header.FrameCount-1));
        return note.ToSignedArray();
    }
    private sealed record Item(Guid Id,int Frame,int Layer,int Kind,int X,int Y,int Width,int Height,byte Ink,string Text,int FontSize,string FontFamily,bool Filled,int StrokeWidth)
    {
        public void Validate(int frames)
        {
            if(Id==Guid.Empty || Frame<0 || Frame>=frames || Layer is <0 or >2 || Kind is <0 or >2 || X is <-16384 or >16384 || Y is <-16384 or >16384 || Width is <1 or >16384 || Height is <1 or >16384 || Ink>2 || FontSize is <6 or >256 || StrokeWidth is <1 or >64 || Text==null || Text.Length>10000 || string.IsNullOrWhiteSpace(FontFamily))throw new InvalidDataException("Invalid KWZ project object.");
        }
        public void Draw(byte[] pixels)
        {
            using var bitmap=new SKBitmap(320,240);using var canvas=new SKCanvas(bitmap);
            using var paint=new SKPaint { Color=SKColors.White,IsAntialias=false,StrokeWidth=StrokeWidth,Style=Filled?SKPaintStyle.Fill:SKPaintStyle.Stroke };canvas.Clear(SKColors.Transparent);
            if(Kind==0)
            {
                using var face=SKTypeface.FromFamilyName(FontFamily);
                if(face==null || !face.FamilyName.Equals(FontFamily,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Install the project's exact font before exporting: "+FontFamily);
                paint.Typeface=face;paint.TextSize=FontSize;paint.Style=SKPaintStyle.Fill;float baseline=Y-paint.FontMetrics.Ascent;
                foreach(string line in Text.Replace("\r","").Split('\n')) { canvas.DrawText(line,X,baseline,paint);baseline+=FontSize*1.25f; }
            }
            else
            {
                float inset=Filled?0:Math.Min(StrokeWidth/2f,Math.Min(Width,Height)/2f);var rect=new SKRect(X+inset,Y+inset,X+Width-inset,Y+Height-inset);
                if(Kind==1)canvas.DrawRect(rect,paint);else canvas.DrawOval(rect,paint);
            }
            var mask=bitmap.Pixels;int offset=Layer*KwzLayerCodec.PixelCount;for(int i=0;i<mask.Length;i++)if(mask[i].Alpha>=128)pixels[offset+i]=Ink;
        }
    }
}
