using LibKwz;
using SkiaSharp;
using SixLabors.ImageSharp;
using System.Security.Cryptography;
using System.Text.Json;
using Flipnote.Build.Formats;

namespace Flipnote.Build;

/// <summary>Read every example frame directly from CIA for manual bitmap localization review.</summary>
internal static class AnimationLocalization
{
    public static void Check(CiaImage image,string recipeDirectory,string outputDirectory)
    {
        var textCoverage=AnimationTextAudit.Check(image,recipeDirectory);
        Directory.CreateDirectory(outputDirectory);var recipes=Directory.GetFiles(recipeDirectory,"*-bitmap.json").Select(path=>(Path:path,Recipe:JsonSerializer.Deserialize<KwzBitmap.Recipe>(File.ReadAllBytes(path),Project.Json)??throw new InvalidDataException("Missing bitmap recipe."))).ToArray();
        using var coverage=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recipeDirectory,"coverage.json")));
        using var layout=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recipeDirectory,"translation-layout.json")));
        string fontHash=layout.RootElement.GetProperty("fontSha256").GetString()!;int nativePixelSize=layout.RootElement.GetProperty("nativePixelSize").GetInt32();
        var reviewed=coverage.RootElement.GetProperty("animations").EnumerateArray().ToDictionary(e=>e.GetProperty("sha256").GetString()!,e=>e.GetProperty("containsJapanese").GetBoolean());
        var reports=new List<object>();var examples=new List<(string Id,int Frame,byte[] Original,byte[] Localized)>();int index=0,totalFrames=0,pending=0;
        foreach(var resource in image.Resources.Values.Where(n=>n.Path.EndsWith(".kwz",StringComparison.OrdinalIgnoreCase)))
        {
            string id=(++index).ToString("D2");using var original=new KwzFile(new MemoryStream(resource.Data),true);totalFrames+=original.Header.FrameCount;
            var matches=recipes.Where(r=>r.Recipe.BaseSha256==Project.Hash(resource.Data)).ToArray();if(matches.Length>1)throw new InvalidDataException("Duplicate animation recipe.");
            if(matches.Length==0)
            {
                bool complete=reviewed.TryGetValue(Project.Hash(resource.Data),out bool japanese) && !japanese;if(!complete)pending++;
                reports.Add(new { id,path=resource.Path,frames=original.Header.FrameCount,status=complete?"Reviewed: no Japanese text":"Pending localization recipe",originalSha256=Project.Hash(resource.Data),outputSha256=Project.Hash(resource.Data),binaryUnchanged=true });continue;
            }
            var match=matches[0];byte[] localized=KwzBitmap.Compile(resource.Data,match.Path);object verification=KwzBitmap.Verify(resource.Data,localized,match.Path);
            reports.Add(new { id,path=resource.Path,frames=original.Header.FrameCount,status="Localized with reviewed text-only bitmap masks",originalSha256=Project.Hash(resource.Data),outputSha256=Project.Hash(localized),recipe=Path.GetFileName(match.Path),verification });
            foreach(int frame in match.Recipe.Edits.Select(e=>e.FirstFrame).Distinct())examples.Add((id,frame,resource.Data,localized));
        }
        var pages=new List<string>();const int pairsPerPage=8;
        for(int first=0;first<examples.Count;first+=pairsPerPage)
        {
            int count=Math.Min(pairsPerPage,examples.Count-first);using var sheet=new SKBitmap(1280,((count+1)/2)*262);using var canvas=new SKCanvas(sheet);canvas.Clear(SKColors.White);
            using var text=new SKPaint { Color=SKColors.Black,TextSize=13,IsAntialias=false };using var separator=new SKPaint { Color=SKColors.LightGray };
            for(int at=0;at<count;at++)
            {
                var example=examples[first+at];int x=at%2*640,y=at/2*262;canvas.DrawRect(x,y,640,22,separator);canvas.DrawText($"{example.Id} / frame {example.Frame:D3} : original",x+4,y+16,text);canvas.DrawText("localized (fixed bitmap masks)",x+324,y+16,text);
                void Draw(byte[] bytes,int left)
                {
                    using var note=new KwzFile(new MemoryStream(bytes),true);var (layers,composite)=note.FrameData.FrameToImage(example.Frame);
                    try { using var buffer=new MemoryStream();composite.SaveAsPng(buffer);using var bitmap=SKBitmap.Decode(buffer.ToArray());canvas.DrawBitmap(bitmap,left,y+22); }
                    finally { composite.Dispose();foreach(var layer in layers)layer.Dispose(); }
                }
                Draw(example.Original,x);Draw(example.Localized,x+320);
            }
            string name=$"review-{first/pairsPerPage+1:D2}.png";using var png=sheet.Encode(SKEncodedImageFormat.Png,100);Project.Save(Path.Combine(outputDirectory,name),png.ToArray());pages.Add(name);
        }
        Project.SaveJson(Path.Combine(outputDirectory,"verification.json"),new { schemaVersion=1,sourceCiaSha256=image.Sha256,animations=reports.Count,frames=totalFrames,layers=totalFrames*3,font="缝合像素体 / Fusion Pixel",fontSha256=fontHash,nativePixelSize,rasterization="Native 12 px monochrome; 24 px by exact 2x nearest; original alignment anchors retained",fontConsistent=recipes.All(r=>r.Recipe.FontSha256==fontHash && r.Recipe.NativePixelSize==nativePixelSize),imageGenerationUsed=false,allAnimationsHaveReviewRecord=pending==0,allKnownTextOccurrencesCovered=textCoverage.Complete,textCoverage,pendingAnimations=pending,thumbnailPolicy="Regenerate localized examples with libkwz.UpdateThumbnail from the selected fully decoded frame, JPEG quality 90; animation pixels remain protected by exact raster masks.",previewPages=pages,reports });
        AnimationTextAudit.RequireComplete(textCoverage);
    }
    public static void Frame(CiaImage image,string resourcePath,int frame,string output)
    {
        if(!image.Resources.TryGetValue(resourcePath,out var resource))throw new InvalidDataException("Unknown animation resource.");
        using var note=new KwzFile(new MemoryStream(resource.Data),true);var (layers,composite)=note.FrameData.FrameToImage(frame);
        string directory=Path.GetDirectoryName(Path.GetFullPath(output))!;Directory.CreateDirectory(directory);
        try
        {
            composite.SaveAsPng(output+".png");for(int i=0;i<3;i++)layers[i].SaveAsPng(output+$"-layer{i}.png");
            File.WriteAllBytes(output+".pixels",note.FrameData.DecodeFrame(frame));
            Project.SaveJson(output+".json",new { path=resourcePath,frame,palette=note.Meta.Entries[frame].Flags,depths=note.Meta.Entries[frame].LayerDepths,visibility=note.Header.LayerVisibilityFlags });
        }
        finally { composite.Dispose();foreach(var layer in layers)layer.Dispose(); }
    }
    public static void Survey(CiaImage image,string directory)
    {
        Directory.CreateDirectory(directory);var report=new List<object>();int number=0;
        foreach(var resource in image.Resources.Values.Where(n=>n.Path.EndsWith(".kwz",StringComparison.OrdinalIgnoreCase)))
        {
            string id=(++number).ToString("D2");using var note=new KwzFile(new MemoryStream(resource.Data),true);
            var hashes=new List<object>();var sheets=new List<string>();const int columns=4,rows=4,thumbWidth=320,thumbHeight=240,label=18;
            for(int first=0;first<note.Header.FrameCount;first+=columns*rows)
            {
                using var sheet=new SKBitmap(columns*thumbWidth,rows*(thumbHeight+label));using var canvas=new SKCanvas(sheet);canvas.Clear(SKColors.LightGray);
                using var paint=new SKPaint { Color=SKColors.Black,TextSize=12,IsAntialias=false };
                for(int local=0;local<columns*rows && first+local<note.Header.FrameCount;local++)
                {
                    int frame=first+local;byte[] pixels=note.FrameData.DecodeFrame(frame);hashes.Add(new { frame,sha256=Convert.ToHexString(SHA256.HashData(pixels)),layerSha256=Enumerable.Range(0,3).Select(l=>Convert.ToHexString(SHA256.HashData(pixels.AsSpan(l*76800,76800)))).ToArray() });
                    var (layers,composite)=note.FrameData.FrameToImage(frame);
                    try
                    {
                        using var png=new MemoryStream();composite.SaveAsPng(png);using var bitmap=SKBitmap.Decode(png.ToArray());int x=local%columns*thumbWidth,y=local/columns*(thumbHeight+label);
                        using(var paper=new SKPaint { Color=SKColors.White })canvas.DrawRect(x,y+label,thumbWidth,thumbHeight,paper);
                        canvas.DrawBitmap(bitmap,x,y+label);canvas.DrawText($"{id} / {frame:D3}",x+3,y+13,paint);
                    }
                    finally { composite.Dispose();foreach(var layer in layers)layer.Dispose(); }
                }
                string filename=$"{id}-{first:D4}.png";using var encoded=sheet.Encode(SKEncodedImageFormat.Png,100);File.WriteAllBytes(Path.Combine(directory,filename),encoded.ToArray());sheets.Add(filename);
            }
            report.Add(new { id,path=resource.Path,sha256=Project.Hash(resource.Data),frames=note.Header.FrameCount,fps=note.Header.FramesPerSecond,thumbnailFrame=note.Header.ThumbnailFrameIndex,layerVisibility=note.Header.LayerVisibilityFlags,sheets,frameHashes=hashes });
        }
        Project.SaveJson(Path.Combine(directory,"survey.json"),new { schemaVersion=1,inputSha256=image.Sha256,animations=report });
    }
}
