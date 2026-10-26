using System.Buffers.Binary;
using System.Text;
using Ctr.Core.Containers.ExeFs;
using Ctr.Core.Containers.Ncch;

namespace Ctr.Core;

public sealed record SmdhIcons(byte[] Raw, string JapaneseTitle, string EnglishTitle,
    byte[] SmallRgb, byte[] LargeRgb);

/// <summary>Read-only NCSD -> application NCCH -> verified ExeFS icon -> SMDH.</summary>
public static class SmdhIconReader
{
    public const int SmdhSize = 0x36C0;

    public static SmdhIcons ReadCartridge(Stream cartridge, ulong expectedProgramId)
    {
        if (!cartridge.CanRead || !cartridge.CanSeek) throw new ArgumentException("A seekable ROM stream is required.");
        byte[] header = new byte[0x200];
        cartridge.Position = 0;
        cartridge.ReadExactly(header);
        if (!header.AsSpan(0x100, 4).SequenceEqual("NCSD"u8) || header[0x18E] > 7 ||
            header[0x18D] is not (1 or 2))
            throw new InvalidDataException("Unsupported NCSD cartridge header.");
        long unit = 0x200L << header[0x18E];
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x120)) * unit;
        long length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x124)) * unit;
        if (offset < 0x200 || length < 0x200 || offset > cartridge.Length || length > cartridge.Length - offset)
            throw new InvalidDataException("Application partition lies outside the cartridge.");
        using var partition = new PartitionStream(cartridge, offset, length);
        NcchDocument ncch = NcchReader.Read(partition);
        if (ncch.Header.ProgramId != expectedProgramId)
            throw new InvalidDataException($"Unexpected application title: {ncch.Header.ProgramId:X16}.");
        NcchRegion region = ncch.Regions.Single(item => item.Kind == NcchRegionKind.ExeFs);
        if (region.Length > 64 * 1024 * 1024) throw new InvalidDataException("Unreasonably large ExeFS.");
        using var exeFs = new MemoryStream();
        NcchReader.CopyRegion(partition, ncch, NcchRegionKind.ExeFs, exeFs, RetailNcchKeys.Create(ncch.Header));
        ExeFsDocument entries = ExeFsReader.Read(exeFs);
        ExeFsEntry icon = entries.Entries.Single(item => item.Name == "icon");
        if (icon.Length != SmdhSize) throw new InvalidDataException("The ExeFS icon is not a complete SMDH.");
        using var smdh = new MemoryStream();
        ExeFsReader.CopyEntry(exeFs, icon, smdh); // verifies SHA-256 before decoding
        return Decode(smdh.ToArray());
    }

    public static SmdhIcons Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length != SmdhSize || !data[..4].SequenceEqual("SMDH"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 0)
            throw new InvalidDataException("Invalid SMDH header, size or version.");
        return new(data.ToArray(), ReadTitle(data.Slice(8, 0x80)), ReadTitle(data.Slice(0x208, 0x80)),
            DecodeRgb565(data.Slice(0x2040, 0x480), 24), DecodeRgb565(data.Slice(0x24C0, 0x1200), 48));
    }

    private static string ReadTitle(ReadOnlySpan<byte> data) => Encoding.Unicode.GetString(data).Split('\0')[0];

    // 8x8 row-major tiles, with Morton-interleaved x/y coordinates within each tile.
    private static byte[] DecodeRgb565(ReadOnlySpan<byte> data, int size)
    {
        byte[] rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int morton = 0;
            for (int bit = 0; bit < 3; bit++)
                morton |= ((x >> bit) & 1) << (2 * bit) | ((y >> bit) & 1) << (2 * bit + 1);
            int tiled = ((y / 8) * (size / 8) + x / 8) * 64 + morton;
            ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(tiled * 2, 2));
            int target = (y * size + x) * 3;
            int red = (pixel >> 11) & 31, green = (pixel >> 5) & 63, blue = pixel & 31;
            rgb[target] = (byte)((red << 3) | (red >> 2));
            rgb[target + 1] = (byte)((green << 2) | (green >> 4));
            rgb[target + 2] = (byte)((blue << 3) | (blue >> 2));
        }
        return rgb;
    }

    private sealed class PartitionStream(Stream source, long offset, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            source.Position = offset + _position;
            int read = source.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]);
            _position += read;
            return read;
        }
        public override long Seek(long value, SeekOrigin origin)
        {
            long target = checked(value + (origin switch
            {
                SeekOrigin.Begin => 0, SeekOrigin.Current => _position, SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            }));
            if (target < 0 || target > length) throw new IOException("Partition seek out of bounds.");
            return _position = target;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}
