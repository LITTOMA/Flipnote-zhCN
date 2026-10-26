using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Flipnote.Build;

internal static class FontCoverage
{
    public static string PlainText(string message)=>Regex.Replace(message,"<[^>]*>",match=>
    {
        string token=match.Value[1..^1];
        return token.Length==4 && ushort.TryParse(token,System.Globalization.NumberStyles.HexNumber,null,out ushort code) && (code<0xe000 || code>0xf8ff)?((char)code).ToString():"";
    });
    public static char[] Required(Project project)
    {
        var result=new HashSet<char>();
        foreach(var operation in project.Manifest.Operations.Where(o=>o.Kind=="msbt"))
        {
            using var json=JsonDocument.Parse(File.ReadAllBytes(project.Source(operation)));
            foreach(var message in json.RootElement.EnumerateArray())foreach(char c in PlainText(message.GetProperty("message").GetString()!))if(!char.IsWhiteSpace(c))result.Add(c);
        }
        if(result.Any(char.IsSurrogate))throw new InvalidDataException("The font profile supports BMP characters only.");
        return result.Order().ToArray();
    }
    public static object Extend(Project project,string fontPath)
    {
        string manifest=project.Source(project.Manifest.Operations.Single(o=>o.Kind=="bcfnt"));
        if(File.Exists(FontUnification.PolicyPath(manifest)))return FontUnification.Run(project,fontPath,extending:true);
        var root=JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject();var map=root["glyphMap"]!.AsObject();var widths=root["glyphWidths"]!.AsObject();
        char[] missing=Required(project).Where(c=>!map.ContainsKey(c.ToString())).ToArray();
        if(missing.Length==0)return new { added=0,glyphs=widths.Count };
        using var typeface=SKTypeface.FromFile(fontPath)??throw new InvalidDataException("Cannot open source font.");
        using var paint=new SKPaint { Typeface=typeface,TextSize=24,Color=SKColors.White,IsAntialias=true,SubpixelText=false };
        foreach(char c in missing)if(paint.GetGlyphs(c.ToString()).Single()==0)throw new InvalidDataException("Source font lacks glyph: "+c);
        var texture=root["textureInfo"]!;var sheetInfo=texture["sheetInfo"]!;var glyph=texture["glyph"]!;
        int w=sheetInfo["width"]!.GetValue<int>(),h=sheetInfo["height"]!.GetValue<int>(),cols=sheetInfo["cols"]!.GetValue<int>(),rows=sheetInfo["rows"]!.GetValue<int>();
        int gw=glyph["width"]!.GetValue<int>(),gh=glyph["height"]!.GetValue<int>(),baseline=glyph["baseline"]!.GetValue<int>(),oldCount=texture["sheetCount"]!.GetValue<int>();
        if(gw!=24 || gh!=25 || baseline!=21)throw new InvalidDataException("font-extend currently targets the 24x25 Note font profile.");
        string stem=manifest[..^14];var sheets=new Dictionary<int,SKBitmap>();var outputs=new Dictionary<string,byte[]>();
        try
        {
            foreach(char c in missing)
            {
                int index=widths.Count,page=index/(cols*rows),slot=index%(cols*rows);
                if(!sheets.TryGetValue(page,out var sheet))
                {
                    sheet=page<oldCount?SKBitmap.Decode(stem+"_sheet"+page+".png"):new SKBitmap(w,h);
                    if(sheet==null || sheet.Width!=w || sheet.Height!=h)throw new InvalidDataException("Invalid font sheet.");
                    if(page>=oldCount)sheet.Erase(SKColors.Transparent);sheets.Add(page,sheet);
                }
                paint.TextSize=24;var bounds=new SKRect();float advance=paint.MeasureText(c.ToString(),ref bounds);
                if(bounds.Width>gw || bounds.Height>gh){paint.TextSize*=Math.Min(gw/bounds.Width,gh/bounds.Height);advance=paint.MeasureText(c.ToString(),ref bounds);}
                int glyphWidth=Math.Min(gw,(int)Math.Ceiling(bounds.Width)),left=(int)Math.Floor(bounds.Left),charWidth=(int)Math.Ceiling(advance);
                float x=slot%cols*(gw+1)+1,y=slot/cols*(gh+1)+1;
                using(var canvas=new SKCanvas(sheet))
                {
                    canvas.Save();canvas.ClipRect(new SKRect(x,y,x+gw,y+gh));
                    using(var clear=new SKPaint { BlendMode=SKBlendMode.Clear })canvas.DrawRect(x,y,gw,gh,clear);
                    float drawBaseline=Math.Clamp(baseline,-bounds.Top,gh-bounds.Bottom);
                    canvas.DrawText(c.ToString(),x-bounds.Left,y+drawBaseline,paint);canvas.Restore();
                }
                map[c.ToString()]=index;widths[index.ToString()]=new JsonObject { ["left"]=left,["glyph"]=glyphWidth,["char"]=charWidth };
            }
            texture["sheetCount"]=(widths.Count+cols*rows-1)/(cols*rows);
            foreach(var (page,sheet) in sheets){using var png=sheet.Encode(SKEncodedImageFormat.Png,100);outputs[stem+"_sheet"+page+".png"]=png.ToArray();}
            // Publish bitmap sheets before the manifest, so an interrupted update cannot reference absent sheets.
            foreach(var (path,bytes) in outputs)Project.Save(path,bytes);
            Project.Save(manifest,JsonSerializer.SerializeToUtf8Bytes(root,Project.Json));
            var report=new { added=missing.Length,characters=new string(missing),glyphs=widths.Count,sourceFont=Path.GetFileName(fontPath),sourceFontSha256=Project.Hash(File.ReadAllBytes(fontPath)),family=typeface.FamilyName,pixelSize=24,metrics="natural left bearing and proportional advance",sheets=outputs.Keys.Select(Path.GetFileName).ToArray() };
            Project.SaveJson(Path.Combine(Path.GetDirectoryName(manifest)!,"glyph-extension.json"),report);return report;
        }
        finally { foreach(var bitmap in sheets.Values)bitmap.Dispose(); }
    }
}
