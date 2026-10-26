using LibKwz;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace Flipnote.Build.Formats;

/// <summary>Apply reviewed raster masks to indexed KWZ layers without regenerating artwork.</summary>
internal static class KwzBitmap
{
    internal sealed record Recipe(int SchemaVersion,string BaseSha256,string ReviewNote,Edit[] Edits,string? FontSha256=null,string? FontFamily=null,int? NativePixelSize=null);
    internal sealed record Edit(int FirstFrame,int LastFrame,int Layer,int X,int Y,int Width,int Height,string SourceText,string Translation,string OriginalSha256,string Erase,string Ink1,string Ink2);
    public static byte[] Compile(byte[] original,string recipePath)
    {
        var recipe=JsonSerializer.Deserialize<Recipe>(File.ReadAllBytes(recipePath),Project.Json)??throw new InvalidDataException("Missing bitmap recipe.");
        if(recipe.SchemaVersion!=1 || recipe.BaseSha256!=Project.Hash(original))throw new InvalidDataException("KWZ bitmap recipe original hash mismatch.");
        using var note=new KwzFile(new MemoryStream(original),true);using var baseline=new KwzFile(new MemoryStream(original),true);
        if(recipe.Edits.Length>1000)throw new InvalidDataException("Too many bitmap edits.");
        foreach(var edit in recipe.Edits)Validate(edit,note.Header.FrameCount);
        for(int frame=0;frame<note.Header.FrameCount;frame++)
        {
            var edits=recipe.Edits.Where(e=>frame>=e.FirstFrame && frame<=e.LastFrame).ToArray();if(edits.Length==0)continue;
            byte[] pixels=baseline.FrameData.DecodeFrame(frame),changed=(byte[])pixels.Clone();var used=new HashSet<int>();
            foreach(var edit in edits)
            {
                byte[] rectangle=new byte[edit.Width*edit.Height];for(int y=0;y<edit.Height;y++)pixels.AsSpan(edit.Layer*76800+(edit.Y+y)*320+edit.X,edit.Width).CopyTo(rectangle.AsSpan(y*edit.Width));
                if(Project.Hash(rectangle)!=edit.OriginalSha256)throw new InvalidDataException($"Bitmap source pixels differ at frame {frame}, layer {edit.Layer}: {edit.SourceText}");
                bool[] erase=Mask(edit.Erase,rectangle.Length),ink1=Mask(edit.Ink1,rectangle.Length),ink2=Mask(edit.Ink2,rectangle.Length);
                for(int i=0;i<rectangle.Length;i++)
                {
                    if(!erase[i] && !ink1[i] && !ink2[i])continue;
                    if(ink1[i] && ink2[i])throw new InvalidDataException("Raster inks overlap.");
                    if(erase[i] && rectangle[i]==0)throw new InvalidDataException("Erase mask includes a non-text transparent pixel.");
                    if((ink1[i] || ink2[i]) && rectangle[i]!=0 && !erase[i])throw new InvalidDataException("New text would overwrite protected artwork.");
                    int index=edit.Layer*76800+(edit.Y+i/edit.Width)*320+edit.X+i%edit.Width;
                    if(!used.Add(index))throw new InvalidDataException("Bitmap masks overlap in the same frame and layer.");
                    changed[index]=ink1[i]?(byte)1:ink2[i]?(byte)2:(byte)0;
                }
            }
            note.FrameData.SetFramePixels(frame,changed);
        }
        byte[] result=KwzThumbnailLocalization.Apply(note.ToSignedArray());Verify(original,result,recipe);return result;
    }
    public static object Verify(byte[] original,byte[] result,string recipePath)=>Verify(original,result,JsonSerializer.Deserialize<Recipe>(File.ReadAllBytes(recipePath),Project.Json)??throw new InvalidDataException("Missing bitmap recipe."));
    private static object Verify(byte[] original,byte[] result,Recipe recipe)
    {
        using var before=new KwzFile(new MemoryStream(original),true);using var after=new KwzFile(new MemoryStream(result),true);
        if(!after.VerifySignature() || before.Header.FrameCount!=after.Header.FrameCount)throw new InvalidDataException("Localized KWZ signature/frame count failed.");
        var a=Sections(original);var b=Sections(result);
        if(!a.Keys.SequenceEqual(b.Keys))throw new InvalidDataException("KWZ section order changed.");
        foreach(var pair in a)if(pair.Key is not ("KMC" or "KMI" or "KTN") && !pair.Value.AsSpan().SequenceEqual(b[pair.Key]))throw new InvalidDataException("A protected KWZ section changed: "+pair.Key);
        object thumbnail=KwzThumbnailLocalization.VerifyThumbnail(result);
        var frames=new List<object>();long changes=0,protectedPixels=0;
        for(int frame=0;frame<before.Header.FrameCount;frame++)
        {
            byte[] source=before.FrameData.DecodeFrame(frame),target=after.FrameData.DecodeFrame(frame),expected=(byte[])source.Clone();bool[] allowed=new bool[source.Length];
            foreach(var edit in recipe.Edits.Where(e=>frame>=e.FirstFrame && frame<=e.LastFrame))
            {
                bool[] erase=Mask(edit.Erase,edit.Width*edit.Height),ink1=Mask(edit.Ink1,edit.Width*edit.Height),ink2=Mask(edit.Ink2,edit.Width*edit.Height);
                for(int i=0;i<erase.Length;i++)if(erase[i] || ink1[i] || ink2[i])
                {
                    int index=edit.Layer*76800+(edit.Y+i/edit.Width)*320+edit.X+i%edit.Width;allowed[index]=true;expected[index]=ink1[i]?(byte)1:ink2[i]?(byte)2:(byte)0;
                }
            }
            if(!target.AsSpan().SequenceEqual(expected))throw new InvalidDataException("Localized bitmap differs from its exact raster recipe: "+frame);
            int changed=0;
            for(int i=0;i<source.Length;i++){if(!allowed[i])protectedPixels++;if(source[i]==target[i])continue;if(!allowed[i])throw new InvalidDataException($"Protected pixel changed: frame {frame}, layer {i/76800}, index {i%76800}");changed++;}
            var oldMeta=before.Meta.Entries[frame];var newMeta=after.Meta.Entries[frame];
            if((oldMeta.Flags&~0xF0u)!=(newMeta.Flags&~0xF0u) || !oldMeta.FrameAuthorId.AsSpan().SequenceEqual(newMeta.FrameAuthorId) || !oldMeta.LayerDepths.AsSpan().SequenceEqual(newMeta.LayerDepths) || oldMeta.SoundEffectFlags!=newMeta.SoundEffectFlags || oldMeta.Unknown!=newMeta.Unknown || oldMeta.CameraFlags!=newMeta.CameraFlags)throw new InvalidDataException("Frame metadata changed outside compression: "+frame);
            changes+=changed;frames.Add(new { frame,changedPixels=changed,protectedPixelsUnchanged=true,sourceSha256=Project.Hash(source),outputSha256=Project.Hash(target) });
        }
        int metadataEnd=8+28*before.Header.FrameCount;
        if(!a["KMI"].AsSpan(metadataEnd).SequenceEqual(b["KMI"].AsSpan(metadataEnd)))throw new InvalidDataException("Opaque frame metadata changed.");
        return new { frames=before.Header.FrameCount,changedPixels=changes,protectedPixels,allLayersVerified=true,audioUnchanged=true,headerUnchanged=true,thumbnailRegenerated=true,thumbnail,signatureVerified=true,frameChecks=frames };
    }
    private static Dictionary<string,byte[]> Sections(byte[] bytes)
    {
        var result=new Dictionary<string,byte[]>();for(int at=0;at<bytes.Length-256;){int length=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4))+8);string magic=System.Text.Encoding.ASCII.GetString(bytes,at,3);result.Add(magic,bytes.AsSpan(at,length).ToArray());at+=length;}return result;
    }
    private static void Validate(Edit edit,int count)
    {
        if(edit.FirstFrame<0 || edit.LastFrame<edit.FirstFrame || edit.LastFrame>=count || edit.Layer is <0 or >2 || edit.X<0 || edit.Y<0 || edit.Width<1 || edit.Height<1 || edit.X+edit.Width>320 || edit.Y+edit.Height>240 || string.IsNullOrWhiteSpace(edit.SourceText) || string.IsNullOrWhiteSpace(edit.Translation))throw new InvalidDataException("Invalid bitmap text edit.");
    }
    private static bool[] Mask(string data,int pixels)
    {
        using var stream=new MemoryStream(Convert.FromBase64String(data));using var gzip=new ZLibStream(stream,CompressionMode.Decompress);byte[] packed=new byte[(pixels+7)/8];gzip.ReadExactly(packed);if(gzip.ReadByte()!=-1)throw new InvalidDataException("Oversized raster mask.");
        var result=new bool[pixels];for(int i=0;i<pixels;i++)result[i]=(packed[i/8]&(1<<(i%8)))!=0;return result;
    }
}
