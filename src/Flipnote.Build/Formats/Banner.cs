using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Flipnote.Build.Formats;

/// <summary>CBMD banner texture overlays. Model, animation and sound data remain opaque and unchanged.</summary>
internal sealed class Banner
{
    public sealed record Recipe(int SchemaVersion, Edit[] Textures);
    public sealed record Edit(int Slot, string Texture, string BaseSha256, string Image, string AllowedMask);
    public sealed record Texture(string Name, int Width, int Height, int Format, int Levels, int Offset, int Length);
    public sealed record Region(int[] Slots, byte[] Stored, byte[] Data, Texture[] Textures);
    private readonly byte[] original;
    public Region[] Regions { get; }
    public byte[] Sound { get; }

    private static void Range(byte[] data, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size) throw new InvalidDataException("Banner reference exceeds its container.");
    }
    private static int U32(byte[] data, int at)
    {
        Range(data, at, 4); uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        if (value > int.MaxValue) throw new InvalidDataException("Banner integer exceeds supported range.");
        return (int)value;
    }
    private static int Relative(byte[] data, int at)
    {
        Range(data, at, 4); long value = (long)at + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
        if (value < 0 || value >= data.Length) throw new InvalidDataException("Invalid CGFX relative reference.");
        return (int)value;
    }
    private static string Name(byte[] data, int at)
    {
        Range(data, at, 1); int end = Array.IndexOf(data, (byte)0, at);
        if (end < at || end - at > 255) throw new InvalidDataException("Invalid CGFX texture name.");
        return Encoding.UTF8.GetString(data, at, end - at);
    }
    private static void Magic(byte[] data, int at, ReadOnlySpan<byte> magic)
    {
        Range(data, at, magic.Length);
        if (!data.AsSpan(at, magic.Length).SequenceEqual(magic)) throw new InvalidDataException("Unsupported banner structure.");
    }

    public Banner(byte[] data)
    {
        Range(data, 0, 0x88); Magic(data, 0, "CBMD"u8);
        original = (byte[])data.Clone();
        int soundAt = U32(data, 0x84); Magic(data, soundAt, "CWAV"u8);
        if (soundAt < 0x88 || (soundAt & 31) != 0 || U32(data, soundAt + 12) != data.Length - soundAt)
            throw new InvalidDataException("Unsupported CBMD sound extent/alignment.");
        Sound = data[soundAt..];
        int[] offsets = Enumerable.Range(0, 14).Select(i => U32(data, 8 + i * 4)).ToArray();
        if (offsets[0] != 0x88 || offsets.Any(o => o != 0 && (o < 0x88 || o >= soundAt)))
            throw new InvalidDataException("Invalid CBMD graphics offsets.");
        int[] starts = offsets.Where(o => o != 0).Distinct().Order().ToArray();
        var regions = new List<Region>();
        for (int i = 0; i < starts.Length; i++)
        {
            byte[] stored = data[starts[i]..(i + 1 < starts.Length ? starts[i + 1] : soundAt)];
            if (stored.Length < 4 || stored[0] != 0x11) throw new InvalidDataException("Banner CGFX must use LZ11.");
            int length = stored[1] | stored[2] << 8 | stored[3] << 16;
            if (length == 0) length = U32(stored, 4);
            if (length is < 0x14 or > 0x80000) throw new InvalidDataException("Banner CGFX exceeds the HOME Menu 512 KiB limit.");
            byte[] cgfx = Lz11.Decode(stored);
            regions.Add(new(Enumerable.Range(0, 14).Where(s => offsets[s] == starts[i]).ToArray(), stored, cgfx, ReadTextures(cgfx)));
        }
        Regions = regions.ToArray();
    }

    private static Texture[] ReadTextures(byte[] data)
    {
        Magic(data, 0, "CGFX"u8);
        if (data[4] != 0xff || data[5] != 0xfe || U32(data, 12) != data.Length) throw new InvalidDataException("Unsupported CGFX byte order/size.");
        int header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        Magic(data, header, "DATA"u8); Range(data, header, 0x18);
        int count = U32(data, header + 16), dict = Relative(data, header + 20);
        Magic(data, dict, "DICT"u8);
        if (count > 256 || U32(data, dict + 8) != count || U32(data, dict + 4) != 28 + count * 16)
            throw new InvalidDataException("Invalid CGFX texture dictionary.");
        Range(data, dict, 28 + count * 16);
        var textures = new List<Texture>();
        for (int i = 0; i < count; i++)
        {
            int entry = dict + 28 + i * 16, obj = Relative(data, entry + 12);
            Range(data, obj, 0x5c); Magic(data, obj + 4, "TXOB"u8);
            if (U32(data, obj) != 0x20000011) throw new InvalidDataException("Only embedded 2D banner textures are supported.");
            string name = Name(data, Relative(data, entry + 8));
            if (name != Name(data, Relative(data, obj + 12))) throw new InvalidDataException("CGFX texture names disagree.");
            int width = U32(data, obj + 28), height = U32(data, obj + 24), length = U32(data, obj + 68), offset = Relative(data, obj + 72);
            if (width is < 8 or > 1024 || height is < 8 or > 1024 || (width & (width - 1)) != 0 || (height & (height - 1)) != 0 ||
                width != U32(data, obj + 64) || height != U32(data, obj + 60)) throw new InvalidDataException("Invalid CGFX texture dimensions.");
            Range(data, offset, length);
            if (offset < header + U32(data, header + 4)) throw new InvalidDataException("Texture overlaps CGFX metadata.");
            textures.Add(new(name, width, height, U32(data, obj + 52), U32(data, obj + 40), offset, length));
        }
        if (textures.Select(t => t.Name).Distinct().Count() != textures.Count) throw new InvalidDataException("Duplicate CGFX texture names.");
        var ordered = textures.OrderBy(t => t.Offset).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Offset < ordered[i - 1].Offset + ordered[i - 1].Length) throw new InvalidDataException("Overlapping banner textures.");
        return textures.ToArray();
    }

    private static void Editable(Texture texture)
    {
        if (texture.Format != 9 || texture.Levels != 1 || texture.Length != texture.Width * texture.Height)
            throw new InvalidDataException("Banner editing currently supports single-level LA4 textures only.");
    }
    private static int Index(Texture t, int x, int y) => ((y / 8) * (t.Width / 8) + x / 8) * 64 +
        (x & 1) + ((y & 1) << 1) + ((x & 2) << 1) + ((y & 2) << 2) + ((x & 4) << 2) + ((y & 4) << 3);
    public static Image<Rgba32> Decode(Region region, Texture texture)
    {
        Editable(texture); var image = new Image<Rgba32>(texture.Width, texture.Height);
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            byte value = region.Data[texture.Offset + Index(texture, x, y)], l = (byte)((value >> 4) * 17);
            image[x, y] = new(l, l, l, (byte)((value & 15) * 17));
        }
        return image;
    }
    public static byte[] Encode(Texture texture, Image<Rgba32> image)
    {
        Editable(texture);
        if (image.Width != texture.Width || image.Height != texture.Height) throw new InvalidDataException("Banner PNG dimensions differ from the original texture.");
        var result = new byte[texture.Length];
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            var p = image[x, y];
            if (p.R != p.G || p.R != p.B || p.R % 17 != 0 || p.A % 17 != 0) throw new InvalidDataException("Banner PNG must contain exact LA4 grayscale/alpha values (multiples of 17).");
            result[Index(texture, x, y)] = (byte)((p.R / 17 << 4) | (p.A / 17));
        }
        return result;
    }
    public static string ImagePath(string recipePath, Edit edit)
    {
        Darc.ValidateName(edit.Image);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recipePath))!, edit.Image);
    }
    public byte[] Apply(Recipe recipe, string recipePath)
    {
        if (recipe.SchemaVersion != 1 || recipe.Textures.Length == 0) throw new InvalidDataException("Invalid banner recipe.");
        var modified = new Dictionary<Region, byte[]>(); var seen = new HashSet<(Region, string)>();
        foreach (var edit in recipe.Textures)
        {
            var region = Regions.SingleOrDefault(r => r.Slots.Contains(edit.Slot)) ?? throw new InvalidDataException("Unknown banner graphics slot.");
            var texture = region.Textures.SingleOrDefault(t => t.Name == edit.Texture) ?? throw new InvalidDataException("Unknown banner texture.");
            if (!seen.Add((region, texture.Name))) throw new InvalidDataException("Duplicate banner texture edit.");
            if (Project.Hash(region.Data[texture.Offset..(texture.Offset + texture.Length)]) != edit.BaseSha256) throw new InvalidDataException("Banner texture base hash differs.");
            int maskSize = (texture.Width * texture.Height + 7) / 8; byte[] mask = new byte[maskSize];
            using (var compressed = new MemoryStream(Convert.FromBase64String(edit.AllowedMask)))
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                zlib.ReadExactly(mask);
                if (zlib.ReadByte() != -1) throw new InvalidDataException("Banner pixel mask has excess data.");
            }
            using var image = Image.Load<Rgba32>(ImagePath(recipePath, edit)); byte[] payload = Encode(texture, image);
            for (int y = 0; y < texture.Height; y++) for (int x = 0; x < texture.Width; x++)
            {
                int linear = y * texture.Width + x, tiled = Index(texture, x, y);
                if (payload[tiled] != region.Data[texture.Offset + tiled] && (mask[linear / 8] & (1 << (linear % 8))) == 0)
                    throw new InvalidDataException("Banner image changes a protected pixel: " + texture.Name);
            }
            if (!modified.TryGetValue(region, out var cgfx)) modified.Add(region, cgfx = (byte[])region.Data.Clone());
            payload.CopyTo(cgfx, texture.Offset);
        }
        if (modified.All(p => p.Key.Data.AsSpan().SequenceEqual(p.Value))) return (byte[])original.Clone();
        using var stream = new MemoryStream(); byte[] header = original[..0x88]; stream.Write(header);
        foreach (var region in Regions)
        {
            foreach (int slot in region.Slots) BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + slot * 4), checked((int)stream.Position));
            stream.Write(modified.TryGetValue(region, out var cgfx) ? Lz11.Encode(cgfx) : region.Stored);
        }
        while ((stream.Position & 31) != 0) stream.WriteByte(0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x84), checked((int)stream.Position));
        stream.Write(Sound); stream.Position = 0; stream.Write(header);
        byte[] result = stream.ToArray(); var reread = new Banner(result);
        if (!reread.Sound.AsSpan().SequenceEqual(Sound) || reread.Regions.Length != Regions.Length) throw new InvalidDataException("Banner sound or region layout changed.");
        for (int i = 0; i < Regions.Length; i++)
            if (!reread.Regions[i].Slots.SequenceEqual(Regions[i].Slots) || !reread.Regions[i].Data.AsSpan().SequenceEqual(modified.GetValueOrDefault(Regions[i], Regions[i].Data)))
                throw new InvalidDataException("Banner CGFX roundtrip differs.");
        return result;
    }
    public static byte[] Compile(byte[] original, string recipePath) => new Banner(original).Apply(Project.Read<Recipe>(recipePath), recipePath);
}
