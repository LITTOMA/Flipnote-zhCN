using System.IO.Compression;
using Flipnote.Build.Formats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Flipnote.Build;

internal static class BannerSelfTest
{
    public static void Run(Project project,CiaImage original,CiaImage localized,string scratch,Action<bool,string> check,Action<Action,string> reject)
    {
        const string target="content/0000/exefs/banner";
        byte[] raw=original.Resources[target].Data;var before=new Banner(raw);var after=new Banner(localized.Resources[target].Data);
        check(before.Regions.Length==2 && before.Regions.Sum(r=>r.Textures.Length)==7 && before.Regions[1].Slots.SequenceEqual(new[]{9}),"Banner reads the common model and Japanese texture override through their dictionaries");
        int count=0;
        foreach(var region in before.Regions)foreach(var texture in region.Textures.Where(t=>t.Format==9))
        {
            using var pixels=Banner.Decode(region,texture);
            if(!Banner.Encode(texture,pixels).AsSpan().SequenceEqual(region.Data.AsSpan(texture.Offset,texture.Length)))throw new InvalidDataException("Banner texture roundtrip differs.");
            count++;
        }
        check(count==6,"All six LA4 banner textures roundtrip through decoded pixels without byte changes");
        var first=before.Regions[0];var tex=first.Textures[0];string png=Path.Combine(scratch,"banner-noop.png");
        using(var pixels=Banner.Decode(first,tex))pixels.SaveAsPng(png);
        byte[] mask=new byte[tex.Width*tex.Height/8];
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.SmallestSize,true))zlib.Write(mask);
        var edit=new Banner.Edit(0,tex.Name,Project.Hash(first.Data[tex.Offset..(tex.Offset+tex.Length)]),Path.GetFileName(png),Convert.ToBase64String(compressed.ToArray()));
        check(before.Apply(new(1,new[]{edit}),Path.Combine(scratch,"banner.json")).AsSpan().SequenceEqual(raw),"No-op banner import preserves the complete original compressed file");
        using(var pixels=Banner.Decode(first,tex))
        {
            pixels[0,0]=new Rgba32(255,0,0,255);
            reject(()=>Banner.Encode(tex,pixels),"LA4 banner encoding rejects colored source pixels");
        }
        using(var wrong=new Image<Rgba32>(8,8))reject(()=>Banner.Encode(tex,wrong),"Banner rejects resized artwork");
        var operation=project.Manifest.Operations.Single(o=>o.Kind=="banner-textures");string recipePath=project.Source(operation);var recipe=Project.Read<Banner.Recipe>(recipePath);
        var firstEdit=recipe.Textures[0];
        reject(()=>before.Apply(new(1,new[]{firstEdit with { AllowedMask=edit.AllowedMask }}),recipePath),"Banner rejects changes outside the reviewed title pixel mask");
        bool opaquePreserved=before.Sound.AsSpan().SequenceEqual(after.Sound);
        for(int r=0;r<before.Regions.Length;r++)
        {
            byte[] expected=(byte[])before.Regions[r].Data.Clone();
            foreach(var t in before.Regions[r].Textures.Where(t=>recipe.Textures.Any(e=>before.Regions[r].Slots.Contains(e.Slot)&&e.Texture==t.Name)))
                after.Regions[r].Data.AsSpan(t.Offset,t.Length).CopyTo(expected.AsSpan(t.Offset));
            opaquePreserved&=expected.AsSpan().SequenceEqual(after.Regions[r].Data);
        }
        check(opaquePreserved,"Banner model, animation, materials, unused textures and complete sound remain byte-identical");
        var bad=(byte[])raw.Clone();System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(8),raw.Length);
        reject(()=>new Banner(bad),"Banner rejects invalid region offsets before decompressing");
    }
}
