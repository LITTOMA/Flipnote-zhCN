using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Flipnote.Build.Formats;

/// <summary>Lossless NintendoWare CLIM container editing for the A4/LA4 UI artwork used here.</summary>
internal sealed class Bclim
{
    private readonly byte[] bytes;
    public int Width { get; }
    public int Height { get; }
    public int Format { get; }
    public int DataLength { get; }
    public string FormatName => Format == 13 ? "A4" : "LA4";

    public Bclim(byte[] data)
    {
        if (data.Length < 40) throw new InvalidDataException("Truncated BCLIM.");
        var footer = data.AsSpan(data.Length - 40);
        if (!footer[..4].SequenceEqual("CLIM"u8) || !footer.Slice(20, 4).SequenceEqual("imag"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[4..]) != 0xFEFF ||
            BinaryPrimitives.ReadUInt16LittleEndian(footer[6..]) != 20 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]) != data.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(footer[24..]) != 16)
            throw new InvalidDataException("Unsupported BCLIM header.");
        Width = BinaryPrimitives.ReadUInt16LittleEndian(footer[28..]);
        Height = BinaryPrimitives.ReadUInt16LittleEndian(footer[30..]);
        Format = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(footer[32..]));
        DataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(footer[36..]));
        if (Format is not (2 or 13)) throw new InvalidDataException("BCLIM editing supports LA4 and A4 only; found format " + Format);
        if (Width is < 1 or > 2048 || Height is < 1 or > 2048 || DataLength != data.Length - 40 ||
            DataLength != ((Width + 7) & ~7) * ((Height + 7) & ~7) / (Format == 13 ? 2 : 1))
            throw new InvalidDataException("BCLIM dimensions and payload size disagree.");
        bytes = (byte[])data.Clone();
    }

    private int Index(int x, int y)
    {
        int morton = (x & 1) | ((y & 1) << 1) | ((x & 2) << 1) | ((y & 2) << 2) |
            ((x & 4) << 2) | ((y & 4) << 3);
        return ((y / 8) * ((Width + 7) / 8) + x / 8) * 64 + morton;
    }

    public Image<Rgba32> Decode()
    {
        var image = new Image<Rgba32>(Width, Height);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int at = Index(x, y), value = Format == 13 ? (bytes[at / 2] >> ((at & 1) * 4)) & 15 : bytes[at];
            byte luminance = Format == 13 ? (byte)255 : (byte)((value >> 4) * 17);
            image[x, y] = new Rgba32(luminance, luminance, luminance, (byte)((value & 15) * 17));
        }
        return image;
    }

    public byte[] Encode(Image<Rgba32> image)
    {
        if (image.Width != Width || image.Height != Height) throw new InvalidDataException("PNG dimensions must match the original BCLIM exactly.");
        byte[] result = (byte[])bytes.Clone();
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            Rgba32 pixel = image[x, y];
            if (Format == 13 && pixel.A != 0 && (pixel.R != 255 || pixel.G != 255 || pixel.B != 255))
                throw new InvalidDataException("A4 textures require white RGB; the layout supplies their tint.");
            if (Format == 2 && (pixel.R != pixel.G || pixel.R != pixel.B))
                throw new InvalidDataException("LA4 textures require grayscale RGB.");
            int at = Index(x, y), alpha = (pixel.A + 8) / 17;
            if (Format == 13)
            {
                int shift = (at & 1) * 4;
                result[at / 2] = (byte)((result[at / 2] & ~(15 << shift)) | (alpha << shift));
            }
            else result[at] = (byte)(((pixel.R + 8) / 17 << 4) | alpha);
        }
        return result;
    }

    public static byte[] Compile(byte[] original, string pngPath)
    {
        using var image = Image.Load<Rgba32>(pngPath);
        return new Bclim(original).Encode(image);
    }

    public byte[] ToPng()
    {
        using var image = Decode(); using var stream = new MemoryStream(); image.SaveAsPng(stream); return stream.ToArray();
    }
}
