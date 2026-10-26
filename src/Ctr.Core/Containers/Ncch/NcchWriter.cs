using System.Buffers.Binary;
using System.Security.Cryptography;
using Ctr.Core.Cryptography;
using Ctr.Core.Containers.ExeFs;
using Ctr.Core.Containers.RomFs;
using Ctr.Core.IO;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.Ncch;

public static class NcchWriter
{
    public static NcchDocument Write(
        Stream destination,
        NcchBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.HeaderTemplate);
        if (!destination.CanRead ||
            !destination.CanWrite ||
            !destination.CanSeek)
        {
            throw new ArgumentException(
                "The NCCH destination must be readable, writable, and seekable.",
                nameof(destination));
        }

        long imageStart = destination.Position;
        var header = plan.HeaderTemplate.Raw.ToArray();
        bool encrypt = plan.EncryptionKeys is not null;
        if (encrypt)
        {
            header[0x18F] &= 0xFB;
        }
        else
        {
            header[0x18F] |= 0x04;
        }

        int mediaUnit = checked(1 << (header[0x18E] + 9));
        destination.Write(header);

        WriteExtendedHeader(
            destination,
            imageStart,
            header,
            mediaUnit,
            plan.ExtendedHeader,
            plan.EncryptionKeys);
        PadImage(destination, imageStart, mediaUnit);
        WriteMediaRegion(
            destination,
            imageStart,
            header,
            mediaUnit,
            NcchRegionKind.Logo,
            0x198,
            0x130,
            plan.Logo,
            null,
            plan.EncryptionKeys);
        PadImage(destination, imageStart, mediaUnit);
        WriteMediaRegion(
            destination,
            imageStart,
            header,
            mediaUnit,
            NcchRegionKind.Plain,
            0x190,
            null,
            plan.Plain,
            null,
            plan.EncryptionKeys);
        PadImage(destination, imageStart, mediaUnit);
        WriteMediaRegion(
            destination,
            imageStart,
            header,
            mediaUnit,
            NcchRegionKind.ExeFs,
            0x1A0,
            0x1C0,
            plan.ExeFs,
            static (source, unit) =>
                Math.Min(source.Length, CtrBinary.Align(
                    ExeFsReader.HeaderSize,
                    unit)),
            plan.EncryptionKeys);
        PadImage(
            destination,
            imageStart,
            plan.AlignSectionsTo0x1000 ? 0x1000 : mediaUnit);
        WriteMediaRegion(
            destination,
            imageStart,
            header,
            mediaUnit,
            NcchRegionKind.RomFs,
            0x1B0,
            0x1E0,
            plan.RomFs,
            GetRomFsHashRegionLength,
            plan.EncryptionKeys);
        PadImage(
            destination,
            imageStart,
            plan.AlignSectionsTo0x1000 ? 0x1000 : mediaUnit);

        long imageLength = destination.Position - imageStart;
        if (imageLength % mediaUnit != 0 ||
            imageLength / mediaUnit > uint.MaxValue)
        {
            throw new CtrFormatException(
                "The rebuilt NCCH size does not fit its media-unit field.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x104),
            checked((uint)(imageLength / mediaUnit)));
        destination.Position = imageStart;
        destination.Write(header);
        destination.Position = imageStart;
        NcchDocument result = NcchReader.Read(
            new SliceStream(destination, imageStart, imageLength));
        destination.Position = imageStart + imageLength;
        return result;
    }

    private static void WriteExtendedHeader(
        Stream destination,
        long imageStart,
        byte[] header,
        int mediaUnit,
        IReadOnlyBinarySource? source,
        NcchKeySet? keys)
    {
        if (source is null)
        {
            header.AsSpan(0x160, 0x20).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(0x180),
                0);
            return;
        }

        if (source.Length < 0x800)
        {
            throw new CtrFormatException(
                "An NCCH extended header must contain the complete 0x800 bytes.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x180),
            0x400);
        byte[] hash = HashPrefix(source, 0x400);
        hash.CopyTo(header, 0x160);
        using Stream input = source.OpenRead();
        if (keys is null)
        {
            CtrBinary.CopyExactly(
                input,
                destination,
                0x800,
                "NCCH extended header");
        }
        else
        {
            var template = new NcchHeader(header);
            AesCtr.Transform(
                input,
                destination,
                keys.PrimaryKey.Span,
                NcchReader.CreateCounter(
                    template,
                    NcchRegionKind.ExtendedHeader,
                    destination.Position - imageStart),
                0x800);
        }

        _ = mediaUnit;
    }

    private static void WriteMediaRegion(
        Stream destination,
        long imageStart,
        byte[] header,
        int mediaUnit,
        NcchRegionKind kind,
        int fieldOffset,
        int? hashOffset,
        IReadOnlyBinarySource? source,
        Func<IReadOnlyBinarySource, int, long>? hashLength,
        NcchKeySet? keys)
    {
        if (source is null)
        {
            header.AsSpan(fieldOffset, 12).Clear();
            if (hashOffset.HasValue)
            {
                header.AsSpan(hashOffset.Value, 0x20).Clear();
            }

            return;
        }

        long relativeOffset = destination.Position - imageStart;
        if (relativeOffset % mediaUnit != 0)
        {
            throw new CtrFormatException(
                $"NCCH {kind} region is not media-unit aligned.");
        }

        long paddedLength = CtrBinary.Align(source.Length, mediaUnit);
        if (relativeOffset / mediaUnit > uint.MaxValue ||
            paddedLength / mediaUnit > uint.MaxValue)
        {
            throw new CtrFormatException(
                $"NCCH {kind} region exceeds the 32-bit media-unit fields.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(fieldOffset),
            checked((uint)(relativeOffset / mediaUnit)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(fieldOffset + 4),
            checked((uint)(paddedLength / mediaUnit)));

        long actualHashLength = hashLength?.Invoke(source, mediaUnit) ??
            (hashOffset.HasValue ? paddedLength : 0);
        if (kind is NcchRegionKind.ExeFs or NcchRegionKind.RomFs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(fieldOffset + 8),
                checked((uint)(actualHashLength / mediaUnit)));
        }

        if (hashOffset.HasValue)
        {
            byte[] hash = HashPaddedPrefix(
                source,
                actualHashLength);
            hash.CopyTo(header, hashOffset.Value);
        }

        using Stream input = source.OpenRead();
        if (keys is null || kind is NcchRegionKind.Logo or NcchRegionKind.Plain)
        {
            CtrBinary.CopyExactly(
                input,
                destination,
                source.Length,
                $"NCCH {kind} source");
        }
        else
        {
            WriteEncrypted(
                destination,
                input,
                new NcchHeader(header),
                kind,
                relativeOffset,
                source.Length,
                keys);
        }

        CtrBinary.WriteZeros(
            destination,
            paddedLength - source.Length);
    }

    private static void WriteEncrypted(
        Stream destination,
        Stream input,
        NcchHeader header,
        NcchRegionKind kind,
        long regionOffset,
        long length,
        NcchKeySet keys)
    {
        byte[] counter = NcchReader.CreateCounter(
            header,
            kind,
            regionOffset);
        if (kind != NcchRegionKind.ExeFs)
        {
            AesCtr.Transform(
                input,
                destination,
                kind == NcchRegionKind.RomFs
                    ? keys.SecondaryKey.Span
                    : keys.PrimaryKey.Span,
                counter,
                length);
            return;
        }

        var exeFsHeader = new byte[ExeFsReader.HeaderSize];
        input.ReadExactly(exeFsHeader);
        AesCtr.Transform(
            new MemoryStream(exeFsHeader, writable: false),
            destination,
            keys.PrimaryKey.Span,
            counter,
            exeFsHeader.Length);
        long firstEntryLength = CtrBinary.ReadUInt32LittleEndian(
            exeFsHeader,
            0x0C);
        firstEntryLength = Math.Min(
            firstEntryLength,
            length - exeFsHeader.Length);
        AesCtr.Transform(
            input,
            destination,
            keys.SecondaryKey.Span,
            counter,
            firstEntryLength,
            exeFsHeader.Length);
        long remaining = length - exeFsHeader.Length - firstEntryLength;
        AesCtr.Transform(
            input,
            destination,
            keys.PrimaryKey.Span,
            counter,
            remaining,
            exeFsHeader.Length + firstEntryLength);
    }

    private static long GetRomFsHashRegionLength(
        IReadOnlyBinarySource source,
        int mediaUnit)
    {
        using Stream input = source.OpenRead();
        Span<byte> header = stackalloc byte[RomFsReader.IvfcHeaderSize];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("IVFC"u8))
        {
            throw new CtrFormatException(
                "The NCCH RomFS source does not contain an IVFC header.");
        }

        long level0Length = CtrBinary.ReadUInt32LittleEndian(header, 8);
        return CtrBinary.Align(
            CtrBinary.Align(
                RomFsReader.IvfcHeaderSize,
                0x20) +
                level0Length,
            mediaUnit);
    }

    private static byte[] HashPrefix(
        IReadOnlyBinarySource source,
        long length)
    {
        using Stream input = source.OpenRead();
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendExactly(input, hash, length);
        return hash.GetHashAndReset();
    }

    private static byte[] HashPaddedPrefix(
        IReadOnlyBinarySource source,
        long length)
    {
        using Stream input = source.OpenRead();
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        long fromSource = Math.Min(source.Length, length);
        AppendExactly(input, hash, fromSource);
        long remaining = length - fromSource;
        Span<byte> zeros = stackalloc byte[256];
        while (remaining != 0)
        {
            int count = (int)Math.Min(zeros.Length, remaining);
            hash.AppendData(zeros[..count]);
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    private static void AppendExactly(
        Stream input,
        IncrementalHash hash,
        long length)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining != 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            input.ReadExactly(buffer.AsSpan(0, wanted));
            hash.AppendData(buffer, 0, wanted);
            remaining -= wanted;
        }
    }

    private static void PadImage(
        Stream destination,
        long imageStart,
        int alignment)
    {
        long relative = destination.Position - imageStart;
        long aligned = CtrBinary.Align(relative, alignment);
        CtrBinary.WriteZeros(destination, aligned - relative);
    }

    private sealed class SliceStream(
        Stream source,
        long start,
        long length) : Stream
    {
        private long _position;

        public override bool CanRead => source.CanRead;

        public override bool CanSeek => source.CanSeek;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, length);
                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int wanted = (int)Math.Min(count, length - _position);
            source.Position = start + _position;
            int read = source.Read(buffer, offset, wanted);
            _position += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int wanted = (int)Math.Min(buffer.Length, length - _position);
            source.Position = start + _position;
            int read = source.Read(buffer[..wanted]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = position;
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
