using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Ctr.Core.IO;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.RomFs;

public static class RomFsWriter
{
    private const uint IvfcSignature = 0x43465649;
    private const uint IvfcId = 0x00010000;
    private const uint InvalidOffset = uint.MaxValue;
    private const int EntryAlignment = 4;
    private const int FileAlignment = 0x10;

    public static RomFsDocument Write(
        Stream destination,
        IReadOnlyList<RomFsBuildFile> files)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(files);
        if (!destination.CanRead ||
            !destination.CanWrite ||
            !destination.CanSeek)
        {
            throw new ArgumentException(
                "The RomFS destination must be readable, writable, and seekable.",
                nameof(destination));
        }

        BuildTree tree = BuildTree.Create(files);
        Metadata metadata = BuildMetadata(tree);
        long imageStart = destination.Position;
        RomFsLayout layout = CreateLayout(metadata.Level3Length);
        destination.SetLength(checked(imageStart + layout.ImageLength));

        WriteIvfcHeader(destination, imageStart, layout);
        WriteLevel3(
            destination,
            imageStart + layout.Level3Offset,
            metadata,
            tree);

        byte[] level2 = HashBlocks(
            destination,
            imageStart + layout.Level3Offset,
            layout.Level3Length);
        byte[] level1 = RomFsReader.HashBlocks(
            level2,
            RomFsReader.DefaultBlockSize);
        byte[] level0 = RomFsReader.HashBlocks(
            level1,
            RomFsReader.DefaultBlockSize);

        WriteAt(destination, imageStart + layout.Level0Offset, level0);
        WriteAt(destination, imageStart + layout.Level1Offset, level1);
        WriteAt(destination, imageStart + layout.Level2Offset, level2);
        destination.Position = imageStart + layout.ImageLength;

        var parsedFiles = metadata.Files
            .Select(file => new RomFsFileEntry(
                file.Path,
                checked(
                    imageStart +
                    layout.Level3Offset +
                    metadata.FileDataOffset +
                    file.DataOffset),
                file.Source.Content.Length))
            .ToArray();
        return new RomFsDocument(parsedFiles, layout);
    }

    private static Metadata BuildMetadata(BuildTree tree)
    {
        List<DirectoryRecord> directories = tree.Directories;
        List<FileRecord> files = tree.Files;

        uint directoryTableLength = 0;
        foreach (DirectoryRecord directory in directories)
        {
            directory.EntryOffset = directoryTableLength;
            directoryTableLength = checked(
                directoryTableLength +
                0x18u +
                (uint)CtrBinary.Align(
                    Encoding.Unicode.GetByteCount(directory.Name),
                    EntryAlignment));
        }

        uint fileTableLength = 0;
        foreach (FileRecord file in files)
        {
            file.EntryOffset = fileTableLength;
            fileTableLength = checked(
                fileTableLength +
                0x20u +
                (uint)CtrBinary.Align(
                    Encoding.Unicode.GetByteCount(file.Name),
                    EntryAlignment));
        }

        LinkTree(directories, files);
        uint[] directoryBuckets = CreateDirectoryBuckets(directories);
        uint[] fileBuckets = CreateFileBuckets(files);

        uint directoryHashOffset = RomFsReader.RomFsMetadataHeaderSize;
        uint directoryHashLength = checked(
            (uint)directoryBuckets.Length * sizeof(uint));
        uint directoryTableOffset = checked(
            (uint)CtrBinary.Align(
                directoryHashOffset + directoryHashLength,
                EntryAlignment));
        uint fileHashOffset = checked(
            (uint)CtrBinary.Align(
                directoryTableOffset + directoryTableLength,
                EntryAlignment));
        uint fileHashLength = checked(
            (uint)fileBuckets.Length * sizeof(uint));
        uint fileTableOffset = checked(
            (uint)CtrBinary.Align(
                fileHashOffset + fileHashLength,
                EntryAlignment));
        uint fileDataOffset = checked(
            (uint)CtrBinary.Align(
                fileTableOffset + fileTableLength,
                FileAlignment));

        long dataLength = 0;
        foreach (FileRecord file in files)
        {
            dataLength = CtrBinary.Align(dataLength, FileAlignment);
            file.DataOffset = dataLength;
            dataLength = checked(dataLength + file.Source.Content.Length);
        }

        long level3Length = checked(fileDataOffset + dataLength);
        return new Metadata(
            directories,
            files,
            directoryBuckets,
            fileBuckets,
            directoryHashOffset,
            directoryHashLength,
            directoryTableOffset,
            directoryTableLength,
            fileHashOffset,
            fileHashLength,
            fileTableOffset,
            fileTableLength,
            fileDataOffset,
            level3Length);
    }

    private static void LinkTree(
        IReadOnlyList<DirectoryRecord> directories,
        IReadOnlyList<FileRecord> files)
    {
        foreach (DirectoryRecord directory in directories)
        {
            DirectoryRecord[] childDirectories = directories
                .Where(candidate => ReferenceEquals(
                    candidate.Parent,
                    directory))
                .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)
                .ToArray();
            FileRecord[] childFiles = files
                .Where(candidate => ReferenceEquals(
                    candidate.Parent,
                    directory))
                .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)
                .ToArray();

            directory.FirstChildDirectory =
                childDirectories.Length == 0
                    ? InvalidOffset
                    : childDirectories[0].EntryOffset;
            directory.FirstChildFile =
                childFiles.Length == 0
                    ? InvalidOffset
                    : childFiles[0].EntryOffset;

            for (int index = 0; index < childDirectories.Length; index++)
            {
                childDirectories[index].NextSibling =
                    index + 1 == childDirectories.Length
                        ? InvalidOffset
                        : childDirectories[index + 1].EntryOffset;
            }

            for (int index = 0; index < childFiles.Length; index++)
            {
                childFiles[index].NextSibling =
                    index + 1 == childFiles.Length
                        ? InvalidOffset
                        : childFiles[index + 1].EntryOffset;
            }
        }
    }

    private static uint[] CreateDirectoryBuckets(
        IReadOnlyList<DirectoryRecord> directories)
    {
        var buckets = Enumerable
            .Repeat(InvalidOffset, ComputeBucketCount(directories.Count))
            .ToArray();
        foreach (DirectoryRecord directory in directories)
        {
            uint parentOffset = directory.Parent?.EntryOffset ?? 0;
            int bucket = checked(
                (int)(HashName(parentOffset, directory.Name) %
                (uint)buckets.Length));
            directory.HashSibling = buckets[bucket];
            buckets[bucket] = directory.EntryOffset;
        }

        return buckets;
    }

    private static uint[] CreateFileBuckets(
        IReadOnlyList<FileRecord> files)
    {
        var buckets = Enumerable
            .Repeat(InvalidOffset, ComputeBucketCount(files.Count))
            .ToArray();
        foreach (FileRecord file in files)
        {
            int bucket = checked(
                (int)(HashName(file.Parent.EntryOffset, file.Name) %
                (uint)buckets.Length));
            file.HashSibling = buckets[bucket];
            buckets[bucket] = file.EntryOffset;
        }

        return buckets;
    }

    private static RomFsLayout CreateLayout(long level3Length)
    {
        int blockSize = RomFsReader.DefaultBlockSize;
        long level2Length =
            checked(((level3Length + blockSize - 1) / blockSize) * 0x20);
        long level1Length =
            checked(((level2Length + blockSize - 1) / blockSize) * 0x20);
        long level0Length =
            checked(((level1Length + blockSize - 1) / blockSize) * 0x20);
        long level0Offset = CtrBinary.Align(
            RomFsReader.IvfcHeaderSize,
            0x20);
        long level3Offset = CtrBinary.Align(
            level0Offset + level0Length,
            blockSize);
        long level1Offset = checked(
            level3Offset + CtrBinary.Align(level3Length, blockSize));
        long level2Offset = checked(
            level1Offset + CtrBinary.Align(level1Length, blockSize));
        long imageLength = CtrBinary.Align(
            level2Offset + level2Length,
            blockSize);
        return new RomFsLayout(
            imageLength,
            level0Offset,
            level0Length,
            level1Offset,
            level1Length,
            level2Offset,
            level2Length,
            level3Offset,
            level3Length,
            blockSize);
    }

    private static void WriteIvfcHeader(
        Stream destination,
        long imageStart,
        RomFsLayout layout)
    {
        var header = new byte[RomFsReader.IvfcHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0),
            IvfcSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(4),
            IvfcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8),
            checked((uint)layout.Level0Length));
        WriteLevel(
            header.AsSpan(0x0C),
            0,
            layout.Level1Length);
        long level2Logical = CtrBinary.Align(
            layout.Level1Length,
            layout.BlockSize);
        WriteLevel(
            header.AsSpan(0x24),
            level2Logical,
            layout.Level2Length);
        WriteLevel(
            header.AsSpan(0x3C),
            CtrBinary.Align(
                level2Logical + layout.Level2Length,
                layout.BlockSize),
            layout.Level3Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x54),
            RomFsReader.IvfcHeaderSize);
        WriteAt(destination, imageStart, header);
    }

    private static void WriteLevel(
        Span<byte> destination,
        long logicalOffset,
        long length)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination,
            checked((ulong)logicalOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[8..],
            checked((ulong)length));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], 12);
    }

    private static void WriteLevel3(
        Stream destination,
        long level3Offset,
        Metadata metadata,
        BuildTree tree)
    {
        destination.Position = level3Offset;
        var header = new byte[RomFsReader.RomFsMetadataHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0),
            RomFsReader.RomFsMetadataHeaderSize);
        WritePair(
            header.AsSpan(4),
            metadata.DirectoryHashOffset,
            metadata.DirectoryHashLength);
        WritePair(
            header.AsSpan(0x0C),
            metadata.DirectoryTableOffset,
            metadata.DirectoryTableLength);
        WritePair(
            header.AsSpan(0x14),
            metadata.FileHashOffset,
            metadata.FileHashLength);
        WritePair(
            header.AsSpan(0x1C),
            metadata.FileTableOffset,
            metadata.FileTableLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(0x24),
            metadata.FileDataOffset);
        destination.Write(header);

        PadToRelative(
            destination,
            level3Offset,
            metadata.DirectoryHashOffset);
        WriteUInt32Array(destination, metadata.DirectoryBuckets);
        PadToRelative(
            destination,
            level3Offset,
            metadata.DirectoryTableOffset);
        foreach (DirectoryRecord directory in metadata.Directories)
        {
            WriteDirectory(destination, directory);
        }

        PadToRelative(
            destination,
            level3Offset,
            metadata.FileHashOffset);
        WriteUInt32Array(destination, metadata.FileBuckets);
        PadToRelative(
            destination,
            level3Offset,
            metadata.FileTableOffset);
        foreach (FileRecord file in metadata.Files)
        {
            WriteFile(destination, file);
        }

        PadToRelative(
            destination,
            level3Offset,
            metadata.FileDataOffset);
        foreach (FileRecord file in metadata.Files)
        {
            PadToRelative(
                destination,
                level3Offset + metadata.FileDataOffset,
                file.DataOffset);
            using Stream input = file.Source.Content.OpenRead();
            CtrBinary.CopyExactly(
                input,
                destination,
                file.Source.Content.Length,
                $"RomFS source '{file.Path}'");
        }

        _ = tree;
    }

    private static void WriteDirectory(
        Stream destination,
        DirectoryRecord directory)
    {
        byte[] name = Encoding.Unicode.GetBytes(directory.Name);
        Span<byte> fixedData = stackalloc byte[0x18];
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData,
            directory.Parent?.EntryOffset ?? 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[4..],
            directory.NextSibling);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[8..],
            directory.FirstChildDirectory);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[12..],
            directory.FirstChildFile);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[16..],
            directory.HashSibling);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[20..],
            checked((uint)name.Length));
        destination.Write(fixedData);
        destination.Write(name);
        CtrBinary.WriteZeros(
            destination,
            CtrBinary.Align(name.Length, EntryAlignment) - name.Length);
    }

    private static void WriteFile(
        Stream destination,
        FileRecord file)
    {
        byte[] name = Encoding.Unicode.GetBytes(file.Name);
        Span<byte> fixedData = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData,
            file.Parent.EntryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[4..],
            file.NextSibling);
        BinaryPrimitives.WriteUInt64LittleEndian(
            fixedData[8..],
            checked((ulong)file.DataOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(
            fixedData[16..],
            checked((ulong)file.Source.Content.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[24..],
            file.HashSibling);
        BinaryPrimitives.WriteUInt32LittleEndian(
            fixedData[28..],
            checked((uint)name.Length));
        destination.Write(fixedData);
        destination.Write(name);
        CtrBinary.WriteZeros(
            destination,
            CtrBinary.Align(name.Length, EntryAlignment) - name.Length);
    }

    private static void WritePair(
        Span<byte> destination,
        uint offset,
        uint length)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], length);
    }

    private static void WriteUInt32Array(
        Stream destination,
        IReadOnlyList<uint> values)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        foreach (uint value in values)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            destination.Write(buffer);
        }
    }

    private static byte[] HashBlocks(
        Stream source,
        long offset,
        long length)
    {
        int blockSize = RomFsReader.DefaultBlockSize;
        long blockCount = (length + blockSize - 1) / blockSize;
        if (blockCount > int.MaxValue / 0x20)
        {
            throw new CtrFormatException(
                "RomFS is too large to build its IVFC hash levels.");
        }

        var result = new byte[checked((int)blockCount * 0x20)];
        var block = new byte[blockSize];
        source.Position = offset;
        for (int index = 0; index < blockCount; index++)
        {
            block.AsSpan().Clear();
            int wanted = (int)Math.Min(
                blockSize,
                length - ((long)index * blockSize));
            source.ReadExactly(block.AsSpan(0, wanted));
            System.Security.Cryptography.SHA256.HashData(
                block,
                result.AsSpan(index * 0x20, 0x20));
        }

        return result;
    }

    private static void PadToRelative(
        Stream destination,
        long start,
        long relativeOffset)
    {
        long target = checked(start + relativeOffset);
        if (destination.Position > target)
        {
            throw new CtrFormatException(
                "RomFS metadata sections overlap while building.");
        }

        CtrBinary.WriteZeros(destination, target - destination.Position);
    }

    private static void WriteAt(
        Stream destination,
        long offset,
        ReadOnlySpan<byte> data)
    {
        destination.Position = offset;
        destination.Write(data);
    }

    private static int ComputeBucketCount(int entryCount)
    {
        int count = Math.Max(entryCount, 3);
        if (count <= 19)
        {
            return count | 1;
        }

        while (count % 2 == 0 ||
               count % 3 == 0 ||
               count % 5 == 0 ||
               count % 7 == 0 ||
               count % 11 == 0 ||
               count % 13 == 0 ||
               count % 17 == 0)
        {
            count++;
        }

        return count;
    }

    private static uint HashName(uint parentOffset, string name)
    {
        uint hash = parentOffset ^ 123456789;
        foreach (char character in name)
        {
            hash = BitOperations.RotateRight(hash, 5) ^ character;
        }

        return hash;
    }

    private sealed class BuildTree
    {
        private BuildTree(
            List<DirectoryRecord> directories,
            List<FileRecord> files)
        {
            Directories = directories;
            Files = files;
        }

        public List<DirectoryRecord> Directories { get; }

        public List<FileRecord> Files { get; }

        public static BuildTree Create(
            IReadOnlyList<RomFsBuildFile> sources)
        {
            var root = new DirectoryRecord(string.Empty, null, string.Empty);
            var directoryByPath = new Dictionary<string, DirectoryRecord>(
                StringComparer.Ordinal)
            {
                [string.Empty] = root,
            };
            var fileByPath = new Dictionary<string, RomFsBuildFile>(
                StringComparer.Ordinal);

            foreach (RomFsBuildFile source in sources)
            {
                ArgumentNullException.ThrowIfNull(source);
                ArgumentNullException.ThrowIfNull(source.Content);
                ArgumentOutOfRangeException.ThrowIfNegative(
                    source.Content.Length);
                string path = NormalizePath(source.Path);
                if (!fileByPath.TryAdd(path, source))
                {
                    throw new ArgumentException(
                        $"Duplicate RomFS file '{path}'.",
                        nameof(sources));
                }

                string[] parts = path.Split('/');
                string parentPath = string.Empty;
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    string directoryPath = string.IsNullOrEmpty(parentPath)
                        ? parts[index]
                        : $"{parentPath}/{parts[index]}";
                    if (!directoryByPath.ContainsKey(directoryPath))
                    {
                        DirectoryRecord parent = directoryByPath[parentPath];
                        directoryByPath.Add(
                            directoryPath,
                            new DirectoryRecord(
                                parts[index],
                                parent,
                                directoryPath));
                    }

                    parentPath = directoryPath;
                }
            }

            List<DirectoryRecord> directories = directoryByPath.Values
                .OrderBy(static directory =>
                    directory.Path.Count(static character => character == '/'))
                .ThenBy(static directory => directory.Path, StringComparer.Ordinal)
                .ToList();
            var files = new List<FileRecord>(fileByPath.Count);
            foreach ((string path, RomFsBuildFile source) in
                     fileByPath.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                int separator = path.LastIndexOf('/');
                string parentPath = separator < 0
                    ? string.Empty
                    : path[..separator];
                string name = separator < 0
                    ? path
                    : path[(separator + 1)..];
                files.Add(
                    new FileRecord(
                        name,
                        directoryByPath[parentPath],
                        path,
                        source));
            }

            return new BuildTree(directories, files);
        }

        private static string NormalizePath(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string normalized = path.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 ||
                parts.Any(static part =>
                    part is "." or ".." ||
                    part.Contains('\0')))
            {
                throw new ArgumentException(
                    $"Invalid RomFS path '{path}'.",
                    nameof(path));
            }

            return string.Join('/', parts);
        }
    }

    private sealed class DirectoryRecord(
        string name,
        DirectoryRecord? parent,
        string path)
    {
        public string Name { get; } = name;

        public DirectoryRecord? Parent { get; } = parent;

        public string Path { get; } = path;

        public uint EntryOffset { get; set; }

        public uint NextSibling { get; set; } = InvalidOffset;

        public uint FirstChildDirectory { get; set; } = InvalidOffset;

        public uint FirstChildFile { get; set; } = InvalidOffset;

        public uint HashSibling { get; set; } = InvalidOffset;
    }

    private sealed class FileRecord(
        string name,
        DirectoryRecord parent,
        string path,
        RomFsBuildFile source)
    {
        public string Name { get; } = name;

        public DirectoryRecord Parent { get; } = parent;

        public string Path { get; } = path;

        public RomFsBuildFile Source { get; } = source;

        public uint EntryOffset { get; set; }

        public uint NextSibling { get; set; } = InvalidOffset;

        public uint HashSibling { get; set; } = InvalidOffset;

        public long DataOffset { get; set; }
    }

    private sealed record Metadata(
        List<DirectoryRecord> Directories,
        List<FileRecord> Files,
        uint[] DirectoryBuckets,
        uint[] FileBuckets,
        uint DirectoryHashOffset,
        uint DirectoryHashLength,
        uint DirectoryTableOffset,
        uint DirectoryTableLength,
        uint FileHashOffset,
        uint FileHashLength,
        uint FileTableOffset,
        uint FileTableLength,
        uint FileDataOffset,
        long Level3Length);
}
