using System.Text;

namespace Flipnote.Build;

/// <summary>A bounded, hash-locked recipe supporting insertion and copies from the original resource.</summary>
internal static class ResourcePatch
{
    internal sealed record Segment(int? CopyOffset = null, int? Length = null, string? Data = null);
    internal sealed record Recipe(string BaseSha256, string ResultSha256, int ResultLength, Segment[] Segments);
    public static byte[] Apply(byte[] original, Recipe recipe)
    {
        if (!Project.Hash(original).Equals(recipe.BaseSha256, StringComparison.OrdinalIgnoreCase) ||
            recipe.Segments is null || recipe.ResultLength < 0 || recipe.ResultLength > 16 * 1024 * 1024)
            throw new InvalidDataException("Resource patch base hash or result size mismatch.");
        using var output = new MemoryStream(recipe.ResultLength);
        foreach (var segment in recipe.Segments)
        {
            ReadOnlySpan<byte> bytes;
            if (segment.Data is not null)
            {
                if (segment.CopyOffset is not null || segment.Length is not null)
                    throw new InvalidDataException("A resource patch segment cannot both copy and insert.");
                if (segment.Data.Length / 2 > recipe.ResultLength - output.Length)
                    throw new InvalidDataException("Resource patch literal exceeds output size.");
                try { bytes = Convert.FromHexString(segment.Data); }
                catch (FormatException e) { throw new InvalidDataException("Invalid resource patch hex.", e); }
            }
            else
            {
                if (segment.CopyOffset is not int offset || segment.Length is not int length ||
                    offset < 0 || length <= 0 || offset > original.Length - length)
                    throw new InvalidDataException("Resource patch copy is outside the original.");
                bytes = original.AsSpan(offset, length);
            }
            if (bytes.IsEmpty || bytes.Length > recipe.ResultLength - output.Length)
                throw new InvalidDataException("Resource patch exceeds its declared output size.");
            output.Write(bytes);
        }
        byte[] result = output.ToArray();
        if (result.Length != recipe.ResultLength || !Project.Hash(result).Equals(recipe.ResultSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resource patch result length/hash mismatch.");
        return result;
    }
}
