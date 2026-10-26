using System.Buffers.Binary;
using System.Security.Cryptography;
using LibKwz;
using SixLabors.ImageSharp;

namespace Flipnote.Build;

/// <summary>Regenerate a KWZ JPEG from its localized thumbnail frame without changing animation sections.</summary>
internal static class KwzThumbnailLocalization
{
    public const int DefaultQuality = 90;

    public static byte[] Apply(byte[] localizedAnimation, int quality = DefaultQuality)
    {
        using var note = new KwzFile(new MemoryStream(localizedAnimation), true);
        Update(note, quality);
        byte[] result = note.ToSignedArray();
        Verify(localizedAnimation, result, quality);
        return result;
    }

    /// <summary>Assert that only KTN and the RSA signature changed during thumbnail generation.</summary>
    public static object Verify(byte[] before, byte[] after, int quality = DefaultQuality)
    {
        var originalSections = Sections(before);
        var updatedSections = Sections(after);
        if (!originalSections.Keys.SequenceEqual(updatedSections.Keys))
            throw new InvalidDataException("Thumbnail update changed KWZ section ordering.");
        foreach (var pair in originalSections)
            if (pair.Key != "KTN" && !pair.Value.AsSpan().SequenceEqual(updatedSections[pair.Key]))
                throw new InvalidDataException("Thumbnail update changed a protected KWZ section: " + pair.Key);
        return new
        {
            protectedSections = originalSections.Keys.Where(name => name != "KTN").ToArray(),
            protectedSectionBytesUnchanged = true,
            animationPixelsAudioAndFrameParametersUnchanged = true,
            thumbnail = VerifyThumbnail(after, quality)
        };
    }

    /// <summary>Check a generated JPEG against the animation's current selected frame.</summary>
    public static object VerifyThumbnail(byte[] animation, int quality = DefaultQuality)
    {
        using var note = new KwzFile(new MemoryStream(animation), true);
        if (!note.VerifySignature()) throw new InvalidDataException("Thumbnail KWZ signature failed.");
        byte[] actual = note.Thumbnail?.Data.ToArray() ?? throw new InvalidDataException("KWZ has no thumbnail.");
        int frame = note.Header.ThumbnailFrameIndex;
        bool whiteMatte = note.Meta.Entries[frame].PaperColorIndex == 6;
        var info = Image.Identify(actual) ?? throw new InvalidDataException("Invalid thumbnail JPEG.");
        Update(note, quality);
        if (!note.Thumbnail!.Data.AsSpan().SequenceEqual(actual))
            throw new InvalidDataException("Thumbnail JPEG does not match the localized selected frame.");
        return new
        {
            thumbnailFrameIndex = frame,
            width = info.Width,
            height = info.Height,
            quality,
            jpegSha256 = Convert.ToHexString(SHA256.HashData(actual)),
            generatedBy = "libkwz.UpdateThumbnail",
            transparentPaperCompositedOnWhite = whiteMatte,
            jpegMatchesLocalizedFrame = true,
            signatureVerified = true,
            lossyThumbnailReencoding = true,
            note = "The whole JPEG thumbnail is regenerated. JPEG pixels outside text can change; animation layer pixels, audio and frame parameters remain protected separately."
        };
    }

    private static void Update(KwzFile note, int quality)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (note.Thumbnail == null) throw new InvalidDataException("Expected an existing KWZ thumbnail.");
        int frame = note.Header.ThumbnailFrameIndex;
        if ((uint)frame >= note.Header.FrameCount) throw new InvalidDataException("Invalid thumbnail frame index.");
        // The standard library path composites onto white RGB without mutating
        // frame palette indices, then resizes and writes the JPEG.
        note.UpdateThumbnail(frame, quality);
    }

    private static Dictionary<string, byte[]> Sections(byte[] data)
    {
        var result = new Dictionary<string, byte[]>();
        for (int offset = 0; offset < data.Length - 256;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)) + 8);
            string name = System.Text.Encoding.ASCII.GetString(data, offset, 3);
            result.Add(name, data.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return result;
    }
}
