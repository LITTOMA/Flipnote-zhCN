using System.Buffers.Binary;
using System.Security.Cryptography;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.Cia;

public static class CiaWriter
{
    public static CiaDocument WriteCfwInstallable(
        Stream destination,
        CiaBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.CertificateChain);
        ArgumentNullException.ThrowIfNull(plan.Contents);
        if (!destination.CanRead ||
            !destination.CanWrite ||
            !destination.CanSeek)
        {
            throw new ArgumentException(
                "The CIA destination must be readable, writable, and seekable.",
                nameof(destination));
        }

        ValidatePlan(plan);
        byte[] ticket = BuildTicket(plan);
        byte[] tmd = BuildTmd(plan);
        long imageStart = destination.Position;
        var header = new byte[CiaReader.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0),
            CiaReader.HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            plan.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(6),
            plan.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8),
            checked((uint)plan.CertificateChain.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x0C),
            checked((uint)ticket.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x10),
            checked((uint)tmd.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x14),
            checked((uint)(plan.Meta?.Length ?? 0)));

        long contentLength = CalculateContentLength(plan.Contents);
        BinaryPrimitives.WriteUInt64LittleEndian(
            header.AsSpan(0x18),
            checked((ulong)contentLength));
        foreach (CiaBuildContent content in plan.Contents)
        {
            header[0x20 + (content.Index / 8)] |=
                (byte)(1 << (7 - (content.Index % 8)));
        }

        destination.Write(header);
        PadImage(destination, imageStart);
        using (Stream certificates = plan.CertificateChain.OpenRead())
        {
            CtrBinary.CopyExactly(
                certificates,
                destination,
                plan.CertificateChain.Length,
                "CIA certificate chain");
        }

        PadImage(destination, imageStart);
        destination.Write(ticket);
        PadImage(destination, imageStart);
        destination.Write(tmd);
        PadImage(destination, imageStart);

        long contentStart = destination.Position;
        foreach (CiaBuildContent content in plan.Contents)
        {
            PadImage(destination, contentStart);
            using Stream input = content.Source.OpenRead();
            CtrBinary.CopyExactly(
                input,
                destination,
                content.Source.Length,
                $"CIA content {content.Index}");
        }

        long contentEnd = checked(contentStart + contentLength);
        if (destination.Position > contentEnd)
        {
            throw new CtrFormatException(
                "CIA contents exceed their declared section length.");
        }

        CtrBinary.WriteZeros(
            destination,
            contentEnd - destination.Position);
        if (plan.Meta is not null)
        {
            PadImage(destination, imageStart);
            using Stream meta = plan.Meta.OpenRead();
            CtrBinary.CopyExactly(
                meta,
                destination,
                plan.Meta.Length,
                "CIA meta section");
        }

        long imageLength = destination.Position - imageStart;
        destination.Position = imageStart;
        var view = new ReadOnlySliceStream(
            destination,
            imageStart,
            imageLength);
        CiaDocument document = CiaReader.Read(view);
        destination.Position = imageStart + imageLength;
        return document;
    }

    private static byte[] BuildTicket(CiaBuildPlan plan)
    {
        byte[] ticket = plan.TicketTemplate.ToArray();
        if (ticket.Length <= CiaTicket.CommonKeyIndexOffset)
        {
            throw new CtrFormatException(
                "The CIA ticket template is truncated.");
        }

        ulong titleId = CtrBinary.ReadUInt64BigEndian(
            plan.TmdTemplate.Span,
            CiaTmd.TitleIdOffset);
        BinaryPrimitives.WriteUInt64BigEndian(
            ticket.AsSpan(CiaTicket.TitleIdOffset),
            titleId);
        if (plan.MarkSignaturesAsFake)
        {
            ticket.AsSpan(4, 0x100).Fill(0xFF);
        }

        return ticket;
    }

    private static byte[] BuildTmd(CiaBuildPlan plan)
    {
        int length = checked(
            CiaTmd.HeaderSize +
            (plan.Contents.Count * CiaTmd.ContentRecordSize));
        var tmd = new byte[length];
        plan.TmdTemplate.Span[
            ..Math.Min(plan.TmdTemplate.Length, CiaTmd.HeaderSize)]
            .CopyTo(tmd);
        if (plan.MarkSignaturesAsFake)
        {
            tmd.AsSpan(4, 0x100).Fill(0xFF);
        }

        BinaryPrimitives.WriteUInt16BigEndian(
            tmd.AsSpan(CiaTmd.ContentCountOffset),
            checked((ushort)plan.Contents.Count));
        tmd.AsSpan(0x204, 64 * 0x24).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(
            tmd.AsSpan(0x204),
            0);
        BinaryPrimitives.WriteUInt16BigEndian(
            tmd.AsSpan(0x206),
            checked((ushort)plan.Contents.Count));

        for (int index = 0; index < plan.Contents.Count; index++)
        {
            CiaBuildContent content = plan.Contents[index];
            int offset = CiaTmd.HeaderSize +
                (index * CiaTmd.ContentRecordSize);
            BinaryPrimitives.WriteUInt32BigEndian(
                tmd.AsSpan(offset),
                content.Id);
            BinaryPrimitives.WriteUInt16BigEndian(
                tmd.AsSpan(offset + 4),
                content.Index);
            BinaryPrimitives.WriteUInt16BigEndian(
                tmd.AsSpan(offset + 6),
                (ushort)(content.Type & 0xFFFE));
            BinaryPrimitives.WriteUInt64BigEndian(
                tmd.AsSpan(offset + 8),
                checked((ulong)content.Source.Length));
            HashSource(content).CopyTo(tmd, offset + 16);
        }

        SHA256.HashData(
            tmd.AsSpan(
                CiaTmd.HeaderSize,
                plan.Contents.Count * CiaTmd.ContentRecordSize),
            tmd.AsSpan(0x208, 0x20));
        SHA256.HashData(
            tmd.AsSpan(0x204, 64 * 0x24),
            tmd.AsSpan(0x1E4, 0x20));
        return tmd;
    }

    private static byte[] HashSource(CiaBuildContent content)
    {
        using Stream input = content.Source.OpenRead();
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long remaining = content.Source.Length;
        while (remaining != 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            input.ReadExactly(buffer.AsSpan(0, wanted));
            hash.AppendData(buffer, 0, wanted);
            remaining -= wanted;
        }

        return hash.GetHashAndReset();
    }

    private static long CalculateContentLength(
        IReadOnlyList<CiaBuildContent> contents)
    {
        long length = 0;
        foreach (CiaBuildContent content in contents)
        {
            length = CtrBinary.Align(
                length,
                CiaReader.SectionAlignment);
            length = checked(length + content.Source.Length);
        }

        return length;
    }

    private static void ValidatePlan(CiaBuildPlan plan)
    {
        if (plan.CertificateChain.Length > uint.MaxValue)
        {
            throw new CtrFormatException(
                "The CIA certificate chain exceeds the format limit.");
        }

        if (plan.TmdTemplate.Length < CiaTmd.HeaderSize)
        {
            throw new CtrFormatException(
                "The CIA TMD template is truncated.");
        }

        if (plan.Contents.Count is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plan),
                "A CIA must contain between 1 and 1024 contents.");
        }

        var indexes = new HashSet<ushort>();
        foreach (CiaBuildContent content in plan.Contents)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(content.Source);
            ArgumentOutOfRangeException.ThrowIfNegative(
                content.Source.Length);
            if (content.Index >= 0x2000)
            {
                throw new CtrFormatException(
                    $"CIA content index {content.Index} exceeds the header bit array.");
            }

            if (!indexes.Add(content.Index))
            {
                throw new CtrFormatException(
                    $"Duplicate CIA content index {content.Index}.");
            }
        }
    }

    private static void PadImage(Stream destination, long imageStart)
    {
        long relative = destination.Position - imageStart;
        long aligned = CtrBinary.Align(
            relative,
            CiaReader.SectionAlignment);
        CtrBinary.WriteZeros(destination, aligned - relative);
    }

    private sealed class ReadOnlySliceStream(
        Stream source,
        long start,
        long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

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
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            Position = target;
            return target;
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
