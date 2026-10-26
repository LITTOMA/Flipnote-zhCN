using System.Buffers.Binary;
using System.Security.Cryptography;
using Ctr.Core.Cryptography;
using Ctr.Core.Containers.ExeFs;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.Ncch;

public static class NcchReader
{
    public const int HeaderSize = 0x200;
    private static ReadOnlySpan<byte> Magic => "NCCH"u8;

    public static NcchDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The NCCH source must be readable and seekable.",
                nameof(source));
        }

        var rawHeader = new byte[HeaderSize];
        CtrBinary.ReadExactlyAt(
            source,
            0,
            rawHeader,
            "NCCH header");
        if (!rawHeader.AsSpan(0x100, 4).SequenceEqual(Magic))
        {
            throw new CtrFormatException(
                "The image does not contain an NCCH header.");
        }

        var header = new NcchHeader(rawHeader);
        long declaredLength = checked(
            (long)header.ContentSize * header.MediaUnitSize);
        CtrBinary.EnsureRange(source.Length, 0, declaredLength);
        var regions = new List<NcchRegion>(5);

        if (header.ExtendedHeaderSize != 0)
        {
            long length = checked((long)header.ExtendedHeaderSize * 2);
            CtrBinary.EnsureRange(declaredLength, HeaderSize, length);
            regions.Add(
                new NcchRegion(
                    NcchRegionKind.ExtendedHeader,
                    HeaderSize,
                    length,
                    header.ExtendedHeaderSize,
                    rawHeader.AsMemory(0x160, 0x20),
                    header.IsEncrypted));
        }

        AddMediaRegion(
            regions,
            rawHeader,
            header,
            declaredLength,
            NcchRegionKind.Plain,
            0x190,
            0,
            ReadOnlyMemory<byte>.Empty,
            false);
        AddMediaRegion(
            regions,
            rawHeader,
            header,
            declaredLength,
            NcchRegionKind.Logo,
            0x198,
            -1,
            rawHeader.AsMemory(0x130, 0x20),
            false);
        AddMediaRegion(
            regions,
            rawHeader,
            header,
            declaredLength,
            NcchRegionKind.ExeFs,
            0x1A0,
            CtrBinary.ReadUInt32LittleEndian(rawHeader, 0x1A8),
            rawHeader.AsMemory(0x1C0, 0x20),
            header.IsEncrypted);
        AddMediaRegion(
            regions,
            rawHeader,
            header,
            declaredLength,
            NcchRegionKind.RomFs,
            0x1B0,
            CtrBinary.ReadUInt32LittleEndian(rawHeader, 0x1B8),
            rawHeader.AsMemory(0x1E0, 0x20),
            header.IsEncrypted);
        return new NcchDocument(header, regions);
    }

    public static void CopyRegion(
        Stream source,
        NcchDocument document,
        NcchRegionKind kind,
        Stream destination,
        NcchKeySet? keys = null,
        bool verifyHash = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        NcchRegion region = document.Regions.Single(
            candidate => candidate.Kind == kind);
        source.Position = region.Offset;

        bool shouldVerify = verifyHash &&
            region.HashRegionLength != 0 &&
            !region.ExpectedHash.IsEmpty;
        using var hash = shouldVerify
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        Stream output = hash is null
            ? destination
            : new PrefixHashWriteStream(
                destination,
                hash,
                region.HashRegionLength);
        try
        {
            if (!region.IsEncrypted)
            {
                CtrBinary.CopyExactly(
                    source,
                    output,
                    region.Length,
                    $"NCCH {kind} region");
            }
            else
            {
                ArgumentNullException.ThrowIfNull(keys);
                CopyEncryptedRegion(
                    source,
                    output,
                    document.Header,
                    region,
                    keys);
            }
        }
        finally
        {
            if (!ReferenceEquals(output, destination))
            {
                output.Dispose();
            }
        }

        if (hash is not null)
        {
            byte[] actual = hash.GetHashAndReset();
            if (!actual.AsSpan().SequenceEqual(region.ExpectedHash.Span))
            {
                throw new CtrFormatException(
                    $"NCCH {kind} superblock hash is invalid.");
            }
        }
    }

    internal static byte[] CreateCounter(
        NcchHeader header,
        NcchRegionKind kind,
        long regionOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regionOffset);
        var counter = new byte[16];
        if (header.Version == 1)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                counter,
                header.PartitionId);
            BinaryPrimitives.WriteUInt64BigEndian(
                counter.AsSpan(8),
                checked((ulong)regionOffset));
            return counter;
        }

        if (header.Version is not 0 and not 2)
        {
            throw new CtrFormatException(
                $"Unsupported NCCH crypto format version {header.Version}.");
        }

        BinaryPrimitives.WriteUInt64BigEndian(
            counter,
            header.PartitionId);
        counter[8] = kind switch
        {
            NcchRegionKind.ExtendedHeader => 1,
            NcchRegionKind.ExeFs => 2,
            NcchRegionKind.RomFs => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return counter;
    }

    private static void AddMediaRegion(
        List<NcchRegion> regions,
        ReadOnlyMemory<byte> rawHeader,
        NcchHeader header,
        long declaredLength,
        NcchRegionKind kind,
        int headerOffset,
        long hashUnits,
        ReadOnlyMemory<byte> expectedHash,
        bool isEncrypted)
    {
        uint offsetUnits = CtrBinary.ReadUInt32LittleEndian(
            rawHeader.Span,
            headerOffset);
        uint lengthUnits = CtrBinary.ReadUInt32LittleEndian(
            rawHeader.Span,
            headerOffset + 4);
        if (lengthUnits == 0)
        {
            return;
        }

        long offset = checked((long)offsetUnits * header.MediaUnitSize);
        long length = checked((long)lengthUnits * header.MediaUnitSize);
        long hashLength = hashUnits switch
        {
            < 0 => length,
            0 => 0,
            _ => checked(hashUnits * header.MediaUnitSize),
        };
        CtrBinary.EnsureRange(declaredLength, offset, length);
        if (hashLength > length)
        {
            throw new CtrFormatException(
                $"NCCH {kind} hash region is larger than the region.");
        }

        regions.Add(
            new NcchRegion(
                kind,
                offset,
                length,
                hashLength,
                expectedHash,
                isEncrypted));
    }

    private static void CopyEncryptedRegion(
        Stream source,
        Stream destination,
        NcchHeader header,
        NcchRegion region,
        NcchKeySet keys)
    {
        byte[] counter = CreateCounter(
            header,
            region.Kind,
            region.Offset);
        if (region.Kind != NcchRegionKind.ExeFs)
        {
            ReadOnlySpan<byte> key =
                region.Kind == NcchRegionKind.RomFs
                    ? keys.SecondaryKey.Span
                    : keys.PrimaryKey.Span;
            AesCtr.Transform(
                source,
                destination,
                key,
                counter,
                region.Length);
            return;
        }

        var encryptedHeader = new byte[ExeFsReader.HeaderSize];
        source.ReadExactly(encryptedHeader);
        using var encryptedHeaderStream =
            new MemoryStream(encryptedHeader, writable: false);
        using var clearHeader = new MemoryStream();
        AesCtr.Transform(
            encryptedHeaderStream,
            clearHeader,
            keys.PrimaryKey.Span,
            counter,
            encryptedHeader.Length);
        byte[] clearHeaderBytes = clearHeader.ToArray();
        destination.Write(clearHeaderBytes);
        uint firstEntryLength = CtrBinary.ReadUInt32LittleEndian(
            clearHeaderBytes,
            0x0C);
        long secondaryLength = Math.Min(
            firstEntryLength,
            region.Length - ExeFsReader.HeaderSize);
        AesCtr.Transform(
            source,
            destination,
            keys.SecondaryKey.Span,
            counter,
            secondaryLength,
            ExeFsReader.HeaderSize);
        long remaining = region.Length -
            ExeFsReader.HeaderSize -
            secondaryLength;
        AesCtr.Transform(
            source,
            destination,
            keys.PrimaryKey.Span,
            counter,
            remaining,
            ExeFsReader.HeaderSize + secondaryLength);
    }

    private sealed class PrefixHashWriteStream(
        Stream destination,
        IncrementalHash hash,
        long hashLength) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            destination.Flush();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            AppendHash(buffer.AsSpan(offset, count));
            destination.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            AppendHash(buffer);
            destination.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Flush();
            }

            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        private void AppendHash(ReadOnlySpan<byte> buffer)
        {
            int count = checked((int)Math.Min(
                buffer.Length,
                hashLength - Math.Min(_written, hashLength)));
            if (count != 0)
            {
                hash.AppendData(buffer[..count]);
            }

            _written += buffer.Length;
        }
    }
}
