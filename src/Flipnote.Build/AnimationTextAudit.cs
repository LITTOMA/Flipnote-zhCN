using LibKwz;
using Flipnote.Build.Formats;
using System.IO.Compression;
using System.Text.Json;

namespace Flipnote.Build;

/// <summary>Find every occurrence of reviewed source text in fully decoded layers, independently of edit ranges.</summary>
internal static class AnimationTextAudit
{
    internal sealed record TextCheck(string SourceText,int Layer,int ReferenceFrame,int[] MatchingFrames,int[] OutsideLayoutFrames,int[] MissingRecipeFrames);
    internal sealed record AnimationCheck(string Id,string Path,int Frames,TextCheck[] Texts);
    internal sealed record Audit(bool Complete,int FramesScanned,int TextTemplates,int TextOccurrences,AnimationCheck[] Animations,string[] Problems);
    private sealed record Pattern(string Text,int Layer,int ReferenceFrame,int First,int Last,int[] Positions,byte[] Values,int[] Strokes,HashSet<int>[] ErasedByFrame);

    public static void RequireComplete(Audit audit)
    {
        if(!audit.Complete)throw new InvalidDataException("Animation text coverage is incomplete: "+string.Join("; ",audit.Problems));
    }

    public static Audit Check(CiaImage image,string directory)
    {
        using var coverage=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"coverage.json")));
        using var layout=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"translation-layout.json")));
        var catalog=coverage.RootElement.GetProperty("animations").EnumerateArray().ToArray();
        var specs=layout.RootElement.GetProperty("animations").EnumerateArray().ToDictionary(a=>a.GetProperty("index").GetInt32());
        var reports=new List<AnimationCheck>();var problems=new List<string>();int framesScanned=0,templates=0,occurrences=0;
        foreach(var resource in image.Resources.Values.Where(n=>n.Path.EndsWith(".kwz",StringComparison.OrdinalIgnoreCase)))
        {
            var entries=catalog.Where(a=>a.GetProperty("path").GetString()==resource.Path && a.GetProperty("sha256").GetString()==Project.Hash(resource.Data)).ToArray();
            if(entries.Length!=1)throw new InvalidDataException("Missing or duplicate animation review record: "+resource.Path);
            var entry=entries[0];string id=entry.GetProperty("id").GetString()!;int index=int.Parse(id,System.Globalization.CultureInfo.InvariantCulture);
            using var note=new KwzFile(new MemoryStream(resource.Data),true);int count=note.Header.FrameCount;
            if(count!=entry.GetProperty("frames").GetInt32())throw new InvalidDataException("Animation review frame count differs: "+id);
            bool hasText=entry.GetProperty("containsJapanese").GetBoolean();var patterns=new List<Pattern>();
            if(hasText)
            {
                if(!specs.TryGetValue(index,out var animation))throw new InvalidDataException("Missing text layout: "+id);
                var recipe=Project.Read<KwzBitmap.Recipe>(Path.Combine(directory,id+"-bitmap.json"));
                if(recipe.BaseSha256!=Project.Hash(resource.Data))throw new InvalidDataException("Text coverage recipe identity differs: "+id);
                foreach(var spec in animation.GetProperty("texts").EnumerateArray())
                {
                    string text=spec.GetProperty("sourceText").GetString()!;int layer=spec.GetProperty("layer").GetInt32(),reference=spec.GetProperty("referenceFrame").GetInt32();
                    if(layer is <0 or >2 || reference<0 || reference>=count)throw new InvalidDataException("Invalid text reference: "+text);
                    byte[] decoded=note.FrameData.DecodeFrame(reference);var positions=new SortedSet<int>();
                    foreach(var bound in spec.GetProperty("eraseBounds").EnumerateArray())
                    {
                        int[] r=bound.EnumerateArray().Select(v=>v.GetInt32()).ToArray();
                        if(r.Length!=4 || r[0]<0 || r[1]<0 || r[2]<1 || r[3]<1 || r[0]+r[2]>320 || r[1]+r[3]>240)throw new InvalidDataException("Invalid text bounds: "+text);
                        for(int y=r[1];y<r[1]+r[3];y++)for(int x=r[0];x<r[0]+r[2];x++)positions.Add(layer*76800+y*320+x);
                    }
                    int[] coordinates=positions.ToArray(),strokes=coordinates.Where(p=>decoded[p]!=0).ToArray();
                    if(strokes.Length==0)throw new InvalidDataException("Empty reference text: "+text);
                    var erased=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
                    foreach(var edit in recipe.Edits.Where(e=>e.SourceText==text && e.Layer==layer))
                    {
                        if(edit.FirstFrame<0 || edit.LastFrame>=count || edit.LastFrame<edit.FirstFrame || edit.Width<1 || edit.Height<1 || edit.X<0 || edit.Y<0 || edit.X+edit.Width>320 || edit.Y+edit.Height>240)throw new InvalidDataException("Invalid text edit bounds: "+text);
                        using var input=new MemoryStream(Convert.FromBase64String(edit.Erase));using var compressed=new ZLibStream(input,CompressionMode.Decompress);
                        byte[] mask=new byte[(edit.Width*edit.Height+7)/8];compressed.ReadExactly(mask);
                        if(compressed.ReadByte()!=-1)throw new InvalidDataException("Oversized text erasure mask.");
                        for(int p=0;p<edit.Width*edit.Height;p++)if((mask[p/8]&(1<<(p%8)))!=0)
                            for(int f=edit.FirstFrame;f<=edit.LastFrame;f++)erased[f].Add(layer*76800+(edit.Y+p/edit.Width)*320+edit.X+p%edit.Width);
                    }
                    patterns.Add(new(text,layer,reference,spec.GetProperty("firstFrame").GetInt32(),spec.GetProperty("lastFrame").GetInt32(),coordinates,coordinates.Select(p=>decoded[p]).ToArray(),strokes,erased));
                }
                if(patterns.Count==0)throw new InvalidDataException("Animation with Japanese has no text templates: "+id);
            }
            var matches=patterns.Select(_=>new List<int>()).ToArray();var outside=patterns.Select(_=>new List<int>()).ToArray();var missing=patterns.Select(_=>new List<int>()).ToArray();
            // Always scan the entire animation, never only firstFrame..lastFrame.
            for(int frame=0;frame<count;frame++)
            {
                byte[] pixels=note.FrameData.DecodeFrame(frame);framesScanned++;
                for(int p=0;p<patterns.Count;p++)
                {
                    var pattern=patterns[p];bool equal=true;
                    for(int at=0;at<pattern.Positions.Length;at++)if(pixels[pattern.Positions[at]]!=pattern.Values[at]){equal=false;break;}
                    if(!equal)continue;
                    matches[p].Add(frame);occurrences++;
                    if(frame<pattern.First || frame>pattern.Last)outside[p].Add(frame);
                    if(pattern.Strokes.Any(position=>!pattern.ErasedByFrame[frame].Contains(position)))missing[p].Add(frame);
                }
            }
            var checks=new List<TextCheck>();
            for(int p=0;p<patterns.Count;p++)
            {
                var pattern=patterns[p];templates++;
                checks.Add(new(pattern.Text,pattern.Layer,pattern.ReferenceFrame,matches[p].ToArray(),outside[p].ToArray(),missing[p].ToArray()));
                if(outside[p].Count>0)problems.Add($"{id} '{pattern.Text}' outside layout at zero-based frames {string.Join(',',outside[p])}");
                if(missing[p].Count>0)problems.Add($"{id} '{pattern.Text}' missing erasure at zero-based frames {string.Join(',',missing[p])}");
            }
            reports.Add(new(id,resource.Path,count,checks.ToArray()));
        }
        if(reports.Count!=catalog.Length)throw new InvalidDataException("Animation review inventory differs from CIA.");
        return new(problems.Count==0,framesScanned,templates,occurrences,reports.ToArray(),problems.ToArray());
    }
}
