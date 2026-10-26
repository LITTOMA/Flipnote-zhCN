using System.Security.Cryptography;
using System.Text;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.ExeFs;

public static class ExeFsReader
{
    public const int HeaderSize = 0x200;
    public const int MaximumEntryCount = 8;

    public static ExeFsDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The ExeFS source must be readable and seekable.",
                nameof(source));
        }

        var header = new byte[HeaderSize];
        CtrBinary.ReadExactlyAt(source, 0, header, "ExeFS header");

        if (!header.AsSpan(0x80, 0x80).SequenceEqual(new byte[0x80]))
        {
            throw new CtrFormatException(
                "The ExeFS reserved header region is not zero-filled.");
        }

        var entries = new List<ExeFsEntry>(MaximumEntryCount);
        for (int index = 0; index < MaximumEntryCount; index++)
        {
            ReadOnlySpan<byte> record = header.AsSpan(index * 0x10, 0x10);
            int nameLength = record[..8].IndexOf((byte)0);
            if (nameLength < 0)
            {
                nameLength = 8;
            }

            if (nameLength == 0)
            {
                continue;
            }

            string name = Encoding.ASCII.GetString(record[..nameLength]);
            long relativeOffset = CtrBinary.ReadUInt32LittleEndian(record, 8);
            long length = CtrBinary.ReadUInt32LittleEndian(record, 12);
            long absoluteOffset = checked(HeaderSize + relativeOffset);
            CtrBinary.EnsureRange(
                source.Length,
                absoluteOffset,
                length);

            int hashOffset = 0x1E0 - (index * 0x20);
            entries.Add(
                new ExeFsEntry(
                    name,
                    index,
                    absoluteOffset,
                    length,
                    header.AsMemory(hashOffset, 0x20)));
        }

        return new ExeFsDocument(entries, source.Length);
    }

    public static void CopyEntry(
        Stream source,
        ExeFsEntry entry,
        Stream destination,
        bool verifyHash = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

        source.Position = entry.Offset;
        if (!verifyHash)
        {
            CtrBinary.CopyExactly(
                source,
                destination,
                entry.Length,
                $"ExeFS entry '{entry.Name}'");
            return;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hashingDestination =
            new HashingWriteStream(destination, hash);
        CtrBinary.CopyExactly(
            source,
            hashingDestination,
            entry.Length,
            $"ExeFS entry '{entry.Name}'");

        byte[] actualHash = hash.GetHashAndReset();
        if (!actualHash.AsSpan().SequenceEqual(entry.ExpectedHash.Span))
        {
            throw new CtrFormatException(
                $"ExeFS entry '{entry.Name}' failed SHA-256 verification.");
        }
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
