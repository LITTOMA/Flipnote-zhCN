using System.Buffers.Binary;
using System.Security.Cryptography;
using Ctr.Core.Cryptography;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.Cia;

public static class CiaReader
{
    public const int HeaderSize = 0x2020;
    public const int SectionAlignment = 0x40;

    public static CiaDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The CIA source must be readable and seekable.",
                nameof(source));
        }

        var header = new byte[HeaderSize];
        CtrBinary.ReadExactlyAt(source, 0, header, "CIA header");
        uint headerSize = CtrBinary.ReadUInt32LittleEndian(header, 0);
        if (headerSize != HeaderSize)
        {
            throw new CtrFormatException(
                $"Unsupported CIA header size 0x{headerSize:X}.");
        }

        ushort type = CtrBinary.ReadUInt16LittleEndian(header, 4);
        ushort version = CtrBinary.ReadUInt16LittleEndian(header, 6);
        uint certificateLength =
            CtrBinary.ReadUInt32LittleEndian(header, 8);
        uint ticketLength =
            CtrBinary.ReadUInt32LittleEndian(header, 0x0C);
        uint tmdLength =
            CtrBinary.ReadUInt32LittleEndian(header, 0x10);
        uint metaLength =
            CtrBinary.ReadUInt32LittleEndian(header, 0x14);
        long contentLength = checked(
            (long)CtrBinary.ReadUInt64LittleEndian(header, 0x18));

        long certificateOffset = CtrBinary.Align(
            headerSize,
            SectionAlignment);
        long ticketOffset = CtrBinary.Align(
            certificateOffset + certificateLength,
            SectionAlignment);
        long tmdOffset = CtrBinary.Align(
            ticketOffset + ticketLength,
            SectionAlignment);
        long contentOffset = CtrBinary.Align(
            tmdOffset + tmdLength,
            SectionAlignment);
        long metaOffset = CtrBinary.Align(
            contentOffset + contentLength,
            SectionAlignment);

        CtrBinary.EnsureRange(
            source.Length,
            certificateOffset,
            certificateLength);
        CtrBinary.EnsureRange(
            source.Length,
            ticketOffset,
            ticketLength);
        CtrBinary.EnsureRange(
            source.Length,
            tmdOffset,
            tmdLength);
        CtrBinary.EnsureRange(
            source.Length,
            contentOffset,
            contentLength);
        CiaSection? metaSection = null;
        if (metaLength != 0)
        {
            CtrBinary.EnsureRange(
                source.Length,
                metaOffset,
                metaLength);
            metaSection = new CiaSection(metaOffset, metaLength);
        }

        var ticketData = new byte[ticketLength];
        CtrBinary.ReadExactlyAt(
            source,
            ticketOffset,
            ticketData,
            "CIA ticket");
        if (ticketData.Length <= CiaTicket.CommonKeyIndexOffset)
        {
            throw new CtrFormatException("The CIA ticket is truncated.");
        }

        var ticket = new CiaTicket(ticketData);
        var tmdData = new byte[tmdLength];
        CtrBinary.ReadExactlyAt(
            source,
            tmdOffset,
            tmdData,
            "CIA TMD");
        CiaTmd tmd = ParseTmd(tmdData);
        IReadOnlyList<CiaContent> contents = MapContents(
            header,
            tmd,
            contentOffset,
            contentLength);

        return new CiaDocument(
            type,
            version,
            new CiaSection(certificateOffset, certificateLength),
            new CiaSection(ticketOffset, ticketLength),
            new CiaSection(tmdOffset, tmdLength),
            new CiaSection(contentOffset, contentLength),
            metaSection,
            ticket,
            tmd,
            contents);
    }

    public static void CopySection(
        Stream source,
        CiaSection section,
        Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(destination);
        source.Position = section.Offset;
        CtrBinary.CopyExactly(
            source,
            destination,
            section.Length,
            "CIA section");
    }

    public static void CopyContent(
        Stream source,
        CiaContent content,
        Stream destination,
        ReadOnlyMemory<byte>? titleKey = null,
        bool verifyHash = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);
        source.Position = content.Offset;

        using var hash = verifyHash
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        Stream output = hash is null
            ? destination
            : new HashingWriteStream(destination, hash);
        try
        {
            if (!content.IsEncrypted)
            {
                CtrBinary.CopyExactly(
                    source,
                    output,
                    content.Length,
                    $"CIA content {content.Index}");
            }
            else
            {
                if (!titleKey.HasValue)
                {
                    throw new CtrFormatException(
                        $"CIA content {content.Index} is encrypted but no " +
                        "decrypted title key was supplied.");
                }

                Span<byte> initializationVector = stackalloc byte[16];
                BinaryPrimitives.WriteUInt16BigEndian(
                    initializationVector,
                    content.Index);
                AesCbc.Decrypt(
                    source,
                    output,
                    titleKey.Value.Span,
                    initializationVector,
                    content.Length);
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
            if (!actual.AsSpan().SequenceEqual(content.ExpectedHash.Span))
            {
                throw new CtrFormatException(
                    $"CIA content {content.Index} failed SHA-256 verification.");
            }
        }
    }

    internal static CiaTmd ParseTmd(ReadOnlyMemory<byte> raw)
    {
        if (raw.Length < CiaTmd.HeaderSize)
        {
            throw new CtrFormatException("The CIA TMD is truncated.");
        }

        ushort count = CtrBinary.ReadUInt16BigEndian(
            raw.Span,
            CiaTmd.ContentCountOffset);
        long expectedLength = checked(
            CiaTmd.HeaderSize +
            ((long)count * CiaTmd.ContentRecordSize));
        if (raw.Length < expectedLength)
        {
            throw new CtrFormatException(
                "The CIA TMD content table is truncated.");
        }

        var contents = new List<CiaTmdContent>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = CiaTmd.HeaderSize +
                (index * CiaTmd.ContentRecordSize);
            long length = checked(
                (long)CtrBinary.ReadUInt64BigEndian(
                    raw.Span,
                    offset + 8));
            contents.Add(
                new CiaTmdContent(
                    CtrBinary.ReadUInt32BigEndian(raw.Span, offset),
                    CtrBinary.ReadUInt16BigEndian(raw.Span, offset + 4),
                    CtrBinary.ReadUInt16BigEndian(raw.Span, offset + 6),
                    length,
                    raw.Slice(offset + 16, 0x20)));
        }

        return new CiaTmd(raw, contents);
    }

    private static List<CiaContent> MapContents(
        ReadOnlySpan<byte> ciaHeader,
        CiaTmd tmd,
        long contentOffset,
        long contentLength)
    {
        var result = new List<CiaContent>();
        long relativeOffset = 0;
        foreach (CiaTmdContent content in tmd.Contents)
        {
            bool included =
                (ciaHeader[0x20 + (content.Index / 8)] &
                (1 << (7 - (content.Index % 8)))) != 0;
            if (!included)
            {
                continue;
            }

            relativeOffset = CtrBinary.Align(
                relativeOffset,
                SectionAlignment);
            CtrBinary.EnsureRange(
                contentLength,
                relativeOffset,
                content.Length);
            result.Add(
                new CiaContent(
                    content.Id,
                    content.Index,
                    content.Type,
                    contentOffset + relativeOffset,
                    content.Length,
                    content.Hash));
            relativeOffset = checked(relativeOffset + content.Length);
        }

        return result;
    }

    private sealed class HashingWriteStream(
        Stream destination,
        IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            destination.Flush();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.AppendData(buffer, offset, count);
            destination.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            destination.Write(buffer);
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
    }
}
