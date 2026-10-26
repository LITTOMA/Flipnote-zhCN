using System.Buffers;
using System.Buffers.Binary;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers;

internal static class CtrBinary
{
    private const int CopyBufferSize = 1024 * 1024;

    public static long Align(long value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        return checked(((value + alignment - 1) / alignment) * alignment);
    }

    public static int Align(int value, int alignment)
    {
        return checked((int)Align((long)value, alignment));
    }

    public static uint ReadUInt32LittleEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    public static ushort ReadUInt16LittleEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    public static ulong ReadUInt64LittleEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    }

    public static ushort ReadUInt16BigEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    }

    public static uint ReadUInt32BigEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }

    public static ulong ReadUInt64BigEndian(
        ReadOnlySpan<byte> data,
        int offset)
    {
        EnsureRange(data.Length, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
    }

    public static void ReadExactlyAt(
        Stream source,
        long offset,
        Span<byte> destination,
        string regionName)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException(
                "The source stream must support seeking.",
                nameof(source));
        }

        source.Position = offset;
        try
        {
            source.ReadExactly(destination);
        }
        catch (EndOfStreamException paradox)
        {
            throw new CtrFormatException(
                $"Unexpected end of stream while reading {regionName}.",
                paradox);
        }
    }

    public static void CopyExactly(
        Stream source,
        Stream destination,
        long byteCount,
        string regionName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long remaining = byteCount;
            while (remaining != 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = source.Read(buffer, 0, wanted);
                if (read == 0)
                {
                    throw new CtrFormatException(
                        $"Unexpected end of stream while copying {regionName}.");
                }

                destination.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void PadTo(Stream destination, int alignment)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek)
        {
            throw new ArgumentException(
                "The destination stream must support seeking.",
                nameof(destination));
        }

        long alignedLength = Align(destination.Position, alignment);
        WriteZeros(destination, alignedLength - destination.Position);
    }

    public static void WriteZeros(Stream destination, long count)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Span<byte> zeros = stackalloc byte[256];
        while (count != 0)
        {
            int length = (int)Math.Min(zeros.Length, count);
            destination.Write(zeros[..length]);
            count -= length;
        }
    }

    public static void EnsureRange(
        long containerLength,
        long offset,
        long length)
    {
        if (offset < 0 ||
            length < 0 ||
            offset > containerLength ||
            length > containerLength - offset)
        {
            throw new CtrFormatException(
                $"Region 0x{offset:X}+0x{length:X} lies outside a " +
                $"0x{containerLength:X}-byte container.");
        }
    }
}
