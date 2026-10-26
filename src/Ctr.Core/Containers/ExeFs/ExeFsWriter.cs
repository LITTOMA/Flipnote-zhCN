using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ctr.Core.IO;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.ExeFs;

public static class ExeFsWriter
{
    public const int EntryAlignment = 0x200;

    public static ExeFsDocument Write(
        Stream destination,
        IReadOnlyList<ExeFsBuildEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(entries);
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException(
                "The ExeFS destination must be writable and seekable.",
                nameof(destination));
        }

        if (entries.Count > ExeFsReader.MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entries),
                $"ExeFS supports at most {ExeFsReader.MaximumEntryCount} entries.");
        }

        ValidateEntries(entries);
        long imageStart = destination.Position;
        var header = new byte[ExeFsReader.HeaderSize];
        destination.Write(header);
        var parsedEntries = new List<ExeFsEntry>(entries.Count);

        for (int index = 0; index < entries.Count; index++)
        {
            ExeFsBuildEntry entry = entries[index];
            CtrBinary.PadTo(destination, EntryAlignment);
            long absoluteOffset = destination.Position - imageStart;
            long relativeOffset = absoluteOffset - ExeFsReader.HeaderSize;

            if (relativeOffset > uint.MaxValue ||
                entry.Content.Length > uint.MaxValue)
            {
                throw new CtrFormatException(
                    $"ExeFS entry '{entry.Name}' exceeds the 32-bit format limit.");
            }

            byte[] hash;
            using (Stream input = entry.Content.OpenRead())
            using (var incrementalHash =
                   IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                if (!input.CanRead)
                {
                    throw new CtrFormatException(
                        $"ExeFS source '{entry.Name}' is not readable.");
                }

                using var hashingInput =
                    new HashingReadStream(input, incrementalHash);
                CtrBinary.CopyExactly(
                    hashingInput,
                    destination,
                    entry.Content.Length,
                    $"ExeFS source '{entry.Name}'");
                hash = incrementalHash.GetHashAndReset();
            }

            int recordOffset = index * 0x10;
            Encoding.ASCII.GetBytes(
                entry.Name,
                header.AsSpan(recordOffset, entry.Name.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(recordOffset + 8),
                checked((uint)relativeOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(recordOffset + 12),
                checked((uint)entry.Content.Length));
            hash.CopyTo(header, 0x1E0 - (index * 0x20));

            parsedEntries.Add(
                new ExeFsEntry(
                    entry.Name,
                    index,
                    absoluteOffset,
                    entry.Content.Length,
                    hash));
        }

        CtrBinary.PadTo(destination, EntryAlignment);
        long imageEnd = destination.Position;
        destination.Position = imageStart;
        destination.Write(header);
        destination.Position = imageEnd;

        return new ExeFsDocument(parsedEntries, imageEnd - imageStart);
    }

    private static void ValidateEntries(
        IReadOnlyList<ExeFsBuildEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExeFsBuildEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Content);
            if (string.IsNullOrEmpty(entry.Name) ||
                entry.Name.Length > 8 ||
                !entry.Name.All(static character =>
                    character is >= '!' and <= '~'))
            {
                throw new ArgumentException(
                    "ExeFS names must contain 1-8 printable ASCII characters.",
                    nameof(entries));
            }

            if (!names.Add(entry.Name))
            {
                throw new ArgumentException(
                    $"Duplicate ExeFS entry '{entry.Name}'.",
                    nameof(entries));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(entry.Content.Length);
        }
    }

    private sealed class HashingReadStream(
        Stream source,
        IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = source.Read(buffer);
            hash.AppendData(buffer[..read]);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
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
