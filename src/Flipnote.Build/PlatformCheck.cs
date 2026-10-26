using System.Runtime.InteropServices;
using System.Text.Json;
using LibKwz;
using SkiaSharp;

namespace Flipnote.Build;

/// <summary>Runs in native Windows/macOS/Linux CI without a game image or display server.</summary>
internal static class PlatformCheck
{
    public static void Run(string root)
    {
        var checks = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks.Add(text); }
        using (var bitmap = new SKBitmap(16,16))
        {
            bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(4,7,SKColors.White);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png,100);
            using var decoded = SKBitmap.Decode(png);
            Check(decoded.GetPixel(4,7)==SKColors.White && decoded.GetPixel(0,0).Alpha==0,"Native Skia PNG roundtrip");
        }
        string directory=Path.Combine(root,"overlays/zh-CN/animations");
        using var layout=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory,"translation-layout.json")));
        string path=Path.Combine(directory,layout.RootElement.GetProperty("font").GetString()!);
        Check(Project.Hash(File.ReadAllBytes(path))==layout.RootElement.GetProperty("fontSha256").GetString(),"Pinned pixel font hash");
        using (var face=SKTypeface.FromFile(path))
        using (var paint=new SKPaint { Typeface=face,TextSize=12,Color=SKColors.White,IsAntialias=false })
        using (var bitmap=new SKBitmap(128,32))
        using (var canvas=new SKCanvas(bitmap))
        {
            Check(face!=null && paint.GetGlyphs("动画记事本ABC").All(g=>g!=0),"File-based Chinese and Latin font loading");
            canvas.Clear(SKColors.Transparent);canvas.DrawText("动画记事本ABC",0,20,paint);
            Check(bitmap.Pixels.Any(p=>p.Alpha==255) && bitmap.Pixels.All(p=>p.Alpha is 0 or 255),"Pixel-font rendering without antialiasing");
        }
        Check(SKFontManager.Default.FontFamilies.Any(),"System font discovery for KWZ project text objects");
        Check(FontUnification.CommonHan().Length==6763,"Complete GB2312 code-page mapping");
        using (var note=KwzFile.CreateBlank(2,12))
        {
            byte[] pixels=note.FrameData.DecodeFrame(0);pixels[10]=1;note.FrameData.SetFramePixels(0,pixels);
            note.UpdateThumbnail(0);byte[] bytes=note.ToSignedArray();
            using var decoded=new KwzFile(new MemoryStream(bytes),true);
            Check(decoded.VerifySignature() && decoded.FrameData.DecodeFrame(0)[10]==1 && decoded.FrameData.DecodeFrame(1)[10]==0,"KWZ compression, JPEG thumbnail and RSA signing");
        }
        string scratch=Path.Combine(root,".work","platform-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try
        {
            string output=Path.Combine(scratch,"中文 space.bin");Project.Save(output,[1]);Project.Save(output,[2,3]);
            Check(File.ReadAllBytes(output).AsSpan().SequenceEqual(new byte[]{2,3}),"Atomic replacement with Unicode and spaced paths");
            string occupied=Path.Combine(scratch,"directory");Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"sentinel"),"unchanged");
            bool rejected=false;try { Project.Save(occupied,[9]); }catch(Exception e) when(e is IOException or UnauthorizedAccessException){rejected=true;}
            Check(rejected && File.ReadAllText(Path.Combine(occupied,"sentinel"))=="unchanged" && !Directory.EnumerateFiles(scratch,"*.tmp").Any(),"Failed save preserves existing data and removes temporary output");
        }
        finally { Directory.Delete(scratch,true); }
        Console.WriteLine(JsonSerializer.Serialize(new { verified=true,runtime=RuntimeInformation.RuntimeIdentifier,os=RuntimeInformation.OSDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),passed=checks.Count,checks },Project.Json));
    }
}
