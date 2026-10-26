using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Flipnote.Build.Formats;
using LibKwz;

namespace Flipnote.Build;

internal static class SelfTest
{
    public static void Run(Project project,CiaImage expected,List<ChangeReport> changes)
    {
        var checks=new List<string>();
        string? oracle=Environment.GetEnvironmentVariable("FLIPNOTE_3DSTOOL");
        if(oracle!=null && !File.Exists(oracle))throw new FileNotFoundException("Optional decoder not found.",oracle);
        void Check(bool success,string message) { if(!success)throw new InvalidDataException("Self-test failed: "+message);checks.Add(message);Console.Error.WriteLine("PASS "+message); }
        void Reject(Action action,string message) { bool rejected=false;try { action(); }catch(Exception e) when(e is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException){rejected=true;}Check(rejected,message); }
        string scratch=Path.Combine(project.Root,".work","selftest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try
        {
            var original=project.OpenInput();var untouched=original.Resources.ToDictionary(p=>p.Key,p=>Project.Hash(p.Value.Original));
            string animationSources=Path.Combine(project.Root,"overlays","zh-CN","animations");
            var textAudit=AnimationTextAudit.Check(original,animationSources);
            Check(textAudit.Complete && textAudit.FramesScanned==506 && textAudit.TextTemplates==48,"Every reviewed text template is covered wherever it appears across all 506 decoded frames");
            string textScratch=Path.Combine(scratch,"text-coverage");Directory.CreateDirectory(textScratch);
            foreach(string file in Directory.GetFiles(animationSources,"*.json"))File.Copy(file,Path.Combine(textScratch,Path.GetFileName(file)));
            string tutorialRecipe=Path.Combine(textScratch,"06-bitmap.json");var fullRecipe=Project.Read<KwzBitmap.Recipe>(tutorialRecipe);
            foreach(int omittedFrame in new[]{33,58})
            {
                var shortened=new List<KwzBitmap.Edit>();
                foreach(var captionEdit in fullRecipe.Edits)
                {
                    if(omittedFrame<captionEdit.FirstFrame || omittedFrame>captionEdit.LastFrame){shortened.Add(captionEdit);continue;}
                    if(captionEdit.FirstFrame<omittedFrame)shortened.Add(captionEdit with { LastFrame=omittedFrame-1 });
                    if(omittedFrame<captionEdit.LastFrame)shortened.Add(captionEdit with { FirstFrame=omittedFrame+1 });
                }
                Project.SaveJson(tutorialRecipe,fullRecipe with { Edits=shortened.ToArray() });
                Reject(()=>AnimationTextAudit.RequireComplete(AnimationTextAudit.Check(original,textScratch)),"Text coverage rejects omitted boundary frame "+omittedFrame);
            }
            Project.SaveJson(tutorialRecipe,fullRecipe);
            string textLayout=Path.Combine(textScratch,"translation-layout.json");var shortenedLayout=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(textLayout))!;
            var tutorial=shortenedLayout["animations"]!.AsArray().Single(a=>a!["index"]!.GetValue<int>()==6)!;
            tutorial["texts"]!.AsArray().Single(t=>t!["sourceText"]!.GetValue<string>()=="アニメです。")!["firstFrame"]=34;
            Project.Save(textLayout,JsonSerializer.SerializeToUtf8Bytes(shortenedLayout,Project.Json));
            Reject(()=>AnimationTextAudit.RequireComplete(AnimationTextAudit.Check(original,textScratch)),"Text coverage scans outside declared layout ranges even when the bitmap recipe is complete");
            Check(original.Resources.Values.All(n=>n.Original.AsSpan().SequenceEqual(n.Build())),"Untouched resources preserve original compressed bytes");
            int msbtCount=0;
            foreach(var node in original.Resources.Values.Where(n=>n.Data.AsSpan().StartsWith("MsgStdBn"u8)))
            {
                var msbt=new Msbt(node.Data);var texts=msbt.Texts();var json=JsonSerializer.SerializeToElement(texts.Select((message,id)=>new { id,message }));
                if(!node.Data.AsSpan().SequenceEqual(msbt.Apply(json)))throw new InvalidDataException("MSBT no-op differs: "+node.Path);msbtCount++;
            }
            Check(msbtCount>=63,"Every original MSBT exports and imports without byte changes");
            var logoTextures=original.Resources.Values.Where(n=>n.Path.EndsWith(".bclim",StringComparison.OrdinalIgnoreCase) && (n.Path.Contains("TexturePack/JP/") || n.Path.EndsWith("/TheaterTitleLogoJp.bclim"))).ToArray();
            foreach(var textureNode in logoTextures)
            {
                var texture=new Bclim(textureNode.Data);
                using var png=SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(texture.ToPng());
                if(!texture.Encode(png).AsSpan().SequenceEqual(textureNode.Data))throw new InvalidDataException("BCLIM PNG roundtrip differs: "+textureNode.Path);
            }
            Check(logoTextures.Length==7,"All seven original logo textures roundtrip through PNG without any byte changes");
            var alphaTexture=new Bclim(logoTextures.First(n=>n.Path.EndsWith("/TitleLogo1.bclim")).Data);
            using(var pixels=alphaTexture.Decode())
            {
                var old=pixels[9,17];pixels[9,17]=new(255,255,255,old.A==255?(byte)0:(byte)255);
                byte[] modified=alphaTexture.Encode(pixels);using var decoded=new Bclim(modified).Decode();
                bool exact=true;for(int y=0;y<pixels.Height;y++)for(int x=0;x<pixels.Width;x++)exact&=pixels[x,y].Equals(decoded[x,y]);
                Check(exact && modified.AsSpan(modified.Length-40).SequenceEqual(logoTextures.First(n=>n.Path.EndsWith("/TitleLogo1.bclim")).Data.AsSpan(modified.Length-40)),"A4 texture editing preserves all other texels and the complete original footer");
                pixels[0,0]=new(255,0,0,255);Reject(()=>alphaTexture.Encode(pixels),"A4 rejects colored artwork instead of silently discarding color");
            }
            using(var wrongSize=new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8,8))Reject(()=>alphaTexture.Encode(wrongSize),"BCLIM rejects PNG dimensions that would break layout coordinates");
            byte[] badTexture=(byte[])logoTextures[0].Data.Clone();badTexture[^40]=0;
            Reject(()=>new Bclim(badTexture),"Malformed BCLIM footer is rejected");
            string sample=changes.First(c=>c.Kind=="msbt").Target;var messages=new Msbt(original.Resources[sample].Data);string[] before=messages.Texts();
            BannerSelfTest.Run(project,original,expected,scratch,Check,Reject);
            var replacement=JsonSerializer.SerializeToElement(new[]{new { id=0,message="测试\n<00.01:0200><E073><003C>",before=before[0] }});
            string[] after=new Msbt(messages.Apply(replacement)).Texts();
            Check(after[0]=="测试\n<00.01:0200><E073><003C>" && before.Skip(1).SequenceEqual(after.Skip(1)),"Partial message changes preserve other entries and control tags");
            Reject(()=>messages.Apply(JsonSerializer.SerializeToElement(new[]{new { id=0,message="a" },new { id=0,message="b" }})),"Duplicate message IDs are rejected");
            Reject(()=>messages.Apply(JsonSerializer.SerializeToElement(new[]{new { id=0,message="a",before="wrong" }})),"Stale message before-text is rejected");
            Reject(()=>messages.Apply(JsonSerializer.SerializeToElement(new[]{new { id=0,message="<bad>" }})),"Malformed control tags are rejected");
            foreach(int size in new[]{0,1,2,17,272,273,65553,100000})
            {
                byte[] bytes=Enumerable.Range(0,size).Select(i=>(byte)(i%19)).ToArray();byte[] compressed=Lz11.Encode(bytes);
                Check(bytes.AsSpan().SequenceEqual(Lz11.Decode(compressed)),"LZ11 roundtrip length "+size);
                if(size>0 && oracle!=null)Oracle(oracle,scratch,compressed,bytes,"lzex");
            }
            if(oracle!=null)Check(true,"3dstool independently decodes short and long LZ11 matches");
            Reject(()=>Lz11.Decode([0x11,1,0,0]),"Truncated LZ11 stream is rejected");
            foreach(var changed in expected.Resources.Values.Where(n=>n.IsChanged && n.Compression=="blz"))
            {
                byte[] encoded=changed.Build();var decoded=new ResourceNode("test.blz",encoded);
                if(oracle!=null)Oracle(oracle,scratch,encoded,decoded.Data,"blz");
            }
            if(oracle!=null)Check(true,"3dstool independently decodes every changed BLZ container and CodeSet");
            var nested=original.Resources.Values.First(n=>n.Path.Contains(".arc!/") && n.Path.Contains(".blz!/") && n.Children.Count==0);
            byte[] edit=(byte[])nested.Data.Clone();edit[^1]^=1;nested.Replace(edit);
            byte[] nestedBuild=original.Build();var nestedRead=new CiaImage(nestedBuild);
            Check(nestedRead.Resources[nested.Path].Data.AsSpan().SequenceEqual(edit),"A doubly nested archive edit survives DARC/LZ11/RomFS/CIA rebuilding");
            Check(nestedRead.Resources.Where(p=>p.Value.Children.Count==0 && p.Key!=nested.Path).All(p=>Project.Hash(p.Value.Original)==untouched[p.Key]),"Nested archive edit preserves every other leaf resource");
            byte[] built=expected.Build();var actual=new CiaImage(built);project.Verify(expected,actual);
            Check(true,"Full localized CIA passes all hashes and every resource comparison");
            Check(built.AsSpan().SequenceEqual(expected.Build()),"Repeated CIA builds are byte-for-byte deterministic");
            foreach(var change in changes.Where(c=>c.Kind=="msbt"))
            {
                using var json=JsonDocument.Parse(File.ReadAllBytes(project.Source(project.Manifest.Operations.Single(o=>o.Target==change.Target))));
                var translated=new Msbt(actual.Resources[change.Target].Data);string[] actualTexts=translated.Texts();
                foreach(var message in json.RootElement.EnumerateArray())
                {
                    int id=message.GetProperty("id").GetInt32();string wanted=translated.NormalizeText(message.GetProperty("message").GetString()!);
                    if(actualTexts[id]!=wanted)throw new InvalidDataException($"Translated message differs: {change.Target} [{id}] expected {JsonSerializer.Serialize(wanted)} actual {JsonSerializer.Serialize(actualTexts[id])}");
                }
            }
            Check(true,"All translated messages match their JSON sources in the rebuilt CIA");
            byte[] font=actual.Resources["content/0000/romfs/font_CN.bcfnt"].Data;Bcfnt.Validate(font);
            byte[] fontCode=actual.Resources[FontMemoryBudget.CodePath].Data;
            FontMemoryBudget.Validate(font.Length,fontCode);
            Check(true,"Chinese font, its header and allocator margin fit the expanded runtime heap budget");
            byte[] oldHeap=(byte[])fontCode.Clone();System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oldHeap.AsSpan(FontMemoryBudget.HeapSizeOffset),FontMemoryBudget.OriginalMainHeapBytes);
            Reject(()=>FontMemoryBudget.Validate(font.Length,oldHeap),"The old heap partition that black-screens with the expanded font is rejected");
            Reject(()=>FontMemoryBudget.Validate((int)FontMemoryBudget.AdditionalBytes,fontCode),"Font growth beyond the tested runtime memory budget is rejected");
            string fontManifest=project.Source(project.Manifest.Operations.Single(o=>o.Kind=="bcfnt"));
            if(File.Exists(FontUnification.PolicyPath(fontManifest)))
            {
                string fontTest=Path.Combine(scratch,"font-style");Directory.CreateDirectory(fontTest);
                string copy=Path.Combine(fontTest,Path.GetFileName(fontManifest));
                using var sourceFont=JsonDocument.Parse(File.ReadAllBytes(fontManifest));
                int pages=sourceFont.RootElement.GetProperty("textureInfo").GetProperty("sheetCount").GetInt32();
                File.Copy(fontManifest,copy);File.Copy(FontUnification.PolicyPath(fontManifest),FontUnification.PolicyPath(copy));
                for(int p=0;p<pages;p++)File.Copy(fontManifest[..^14]+"_sheet"+p+".png",copy[..^14]+"_sheet"+p+".png");
                var policy=Project.Read<FontStylePolicy>(FontUnification.PolicyPath(copy));
                var altered=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(copy))!;
                var preserved=policy.Preserved.First().Value;
                altered["glyphWidths"]![preserved.Index.ToString()]!["char"]=preserved.Advance+1;
                Project.Save(copy,JsonSerializer.SerializeToUtf8Bytes(altered,Project.Json));
                Reject(()=>FontUnification.Validate(copy),"Font policy rejects changed widths for protected Japanese/symbol glyphs");
                File.Copy(fontManifest,copy,true);
                int glyphId=policy.Unified.First().Value.Index;
                var grid=sourceFont.RootElement.GetProperty("textureInfo").GetProperty("sheetInfo");
                int cols=grid.GetProperty("cols").GetInt32(),slots=cols*grid.GetProperty("rows").GetInt32(),slot=glyphId%slots;
                string sheetPath=copy[..^14]+"_sheet"+(glyphId/slots)+".png";
                using(var bitmap=SkiaSharp.SKBitmap.Decode(sheetPath))
                {
                    int x=slot%cols*25+1,y=slot/cols*26+1;var pixel=bitmap.GetPixel(x,y);
                    bitmap.SetPixel(x,y,new SkiaSharp.SKColor(255,255,255,pixel.Alpha==0?(byte)255:(byte)0));
                    using var png=bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);File.WriteAllBytes(sheetPath,png.ToArray());
                }
                Reject(()=>FontUnification.Validate(copy),"Font policy rejects a stale Han/English bitmap even when glyph coverage is complete");
            }
            byte[] corrupt=(byte[])built.Clone();corrupt[(int)actual.Document.Contents[0].Offset+0x300]^=1;
            Reject(()=>new CiaImage(corrupt),"Damaged CIA content fails hash validation");
            var patch=Project.CapturePatch([1,2,3,4],[1,7,8,4]);Check(Project.ApplyPatch([1,2,3,4],patch).AsSpan().SequenceEqual(new byte[]{1,7,8,4}),"Compact code patches reproduce their declared result");
            Reject(()=>Project.ApplyPatch([1,2,3,5],patch),"Wrong code base is rejected before patching");
            Reject(()=>project.OutputPath(project.Settings.Input),"CIA build cannot overwrite the source ROM");
            Reject(()=>project.Source(project.Manifest.Operations[0] with { Source="assets:../manifest.json" }),"Overlay source traversal is rejected");
            // Renaming an open file is legal on Unix. Force a portable rename failure instead.
            string atomic=Path.Combine(scratch,"occupied");Directory.CreateDirectory(atomic);
            string sentinel=Path.Combine(atomic,"keep.bin");File.WriteAllBytes(sentinel,[1,2,3]);
            Reject(()=>Project.Save(atomic,[4,5,6]),"A directory collision prevents output replacement");
            Check(File.ReadAllBytes(sentinel).AsSpan().SequenceEqual(new byte[]{1,2,3}) && !Directory.EnumerateFiles(scratch,"*.tmp").Any(),"Failed save preserves destination and cleans its temporary file");
            string projectPath=Path.Combine(scratch,"object.kwzproj");
            using(var note=KwzFile.CreateBlank(2,12))
            using(var zip=ZipFile.Open(projectPath,ZipArchiveMode.Create))
            {
                using(var entry=zip.CreateEntry("base.kwz").Open())entry.Write(note.ToSignedArray());
                using var meta=zip.CreateEntry("project.json").Open();
                JsonSerializer.Serialize(meta,new { Format="KWZStudioProject",Version=1,CurrentFrame=0,Objects=new[]{new { Id=Guid.NewGuid(),Frame=0,Layer=0,Kind=1,X=10,Y=10,Width=20,Height=20,Ink=(byte)2,Text="",FontSize=24,FontFamily="Microsoft YaHei",Filled=true,StrokeWidth=1 }} });
            }
            byte[] animation=KwzProject.Compile(projectPath);
            using(var note=new KwzFile(new MemoryStream(animation),true))Check(note.VerifySignature() && note.FrameData.DecodeFrame(0)[15*320+15]==2 && note.FrameData.DecodeFrame(1).All(p=>p==0),"Headless KWZ project export signs shapes and preserves following frames");
            string bitmapPath=Path.Combine(scratch,"bitmap.json");
            var bitmapEdit=new KwzBitmap.Edit(0,0,0,0,0,1,1,"original text","译文",Project.Hash([0]),Mask(false),Mask(true),Mask(false));
            var bitmapRecipe=new KwzBitmap.Recipe(1,Project.Hash(animation),"Synthetic one-pixel edit exercises protected pixels and inter-frame dependencies.",[bitmapEdit]);
            Project.SaveJson(bitmapPath,bitmapRecipe);byte[] bitmapResult=KwzBitmap.Compile(animation,bitmapPath);
            using(var note=new KwzFile(new MemoryStream(bitmapResult),true))Check(note.FrameData.DecodeFrame(0)[0]==1 && note.FrameData.DecodeFrame(0)[15*320+15]==2 && note.FrameData.DecodeFrame(1).All(p=>p==0),"Bitmap editing preserves existing artwork and successor frames");
            var unsafeEdit=bitmapEdit with { X=15,Y=15,OriginalSha256=Project.Hash([2]) };
            Project.SaveJson(bitmapPath,bitmapRecipe with { Edits=[unsafeEdit] });
            Reject(()=>KwzBitmap.Compile(animation,bitmapPath),"New bitmap text cannot overwrite artwork outside the erasure mask");
            Project.SaveJson(bitmapPath,bitmapRecipe with { Edits=[bitmapEdit with { OriginalSha256=Project.Hash([2]) }] });
            Reject(()=>KwzBitmap.Compile(animation,bitmapPath),"Stale bitmap source-region hash is rejected");
            Project.SaveJson(bitmapPath,bitmapRecipe);
            using(var tampered=new KwzFile(new MemoryStream(bitmapResult),true))
            {
                byte[] pixels=tampered.FrameData.DecodeFrame(1);pixels[500]=1;tampered.FrameData.SetFramePixels(1,pixels);
                Reject(()=>KwzBitmap.Verify(animation,tampered.ToSignedArray(),bitmapPath),"Bitmap audit rejects unrelated pixel changes in other frames");
            }
            using(var tampered=new KwzFile(new MemoryStream(bitmapResult),true))
            {
                tampered.Header.FrameSpeed=8;
                Reject(()=>KwzBitmap.Verify(animation,tampered.ToSignedArray(),bitmapPath),"Bitmap audit rejects animation timing changes");
            }
            var kwzBase=project.OpenInput();var kwzNode=kwzBase.Resources.Values.First(n=>n.Path.EndsWith(".kwz",StringComparison.Ordinal));kwzNode.Replace(animation);
            var kwzBuilt=new CiaImage(kwzBase.Build());
            Check(kwzBuilt.Resources[kwzNode.Path].Data.AsSpan().SequenceEqual(animation),"KWZ replacement survives its compressed archive and CIA rebuild");
            Check(project.OpenInput().Sha256==project.Settings.InputSha256,"Original CIA remains byte-identical");
            var report=new { independentCompressionDecoder=oracle==null?"not requested":"3dstool",passed=checks.Count,checks,inputSha256=expected.Sha256,outputSha256=Project.Hash(built),resourceCount=actual.Resources.Count,msbtFiles=msbtCount,overlays=changes.Count };
            Project.SaveJson(Path.Combine(project.Root,"artifacts/selftest.json"),report);Console.WriteLine(JsonSerializer.Serialize(report,Project.Json));
        }
        finally { Directory.Delete(scratch,true); }
    }
    private static string Mask(bool bit)
    {
        using var output=new MemoryStream();using(var encoder=new ZLibStream(output,CompressionLevel.SmallestSize,true))encoder.WriteByte(bit?(byte)1:(byte)0);
        return Convert.ToBase64String(output.ToArray());
    }
    private static void Oracle(string root,string scratch,byte[] compressed,byte[] decoded,string format)
    {
        string input=Path.Combine(scratch,"oracle.bin"),output=Path.Combine(scratch,"oracle-decoded.bin");File.WriteAllBytes(input,compressed);if(File.Exists(output))File.Delete(output);
        var start=new ProcessStartInfo(root) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        foreach(string arg in new[]{"-uvf",input,"--compress-type",format,"--compress-out",output})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(30000)) { process.Kill();throw new TimeoutException("Independent decoder timed out."); }
        if(process.ExitCode!=0 || !File.ReadAllBytes(output).AsSpan().SequenceEqual(decoded))throw new InvalidDataException("Independent "+format+" decoding failed: "+stdout.Result+stderr.Result);
    }
}
