using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.RomFs;

public static class RomFsReader
{
    public const int IvfcHeaderSize = 0x5C;
    public const int RomFsMetadataHeaderSize = 0x28;
    public const int DefaultBlockSize = 0x1000;
    private const uint IvfcSignature = 0x43465649;
    private const uint InvalidOffset = uint.MaxValue;

    public static RomFsDocument Read(
        Stream source,
        bool verifyIntegrity = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(
                "The RomFS source must be readable and seekable.",
                nameof(source));
        }

        var header = new byte[IvfcHeaderSize];
        CtrBinary.ReadExactlyAt(source, 0, header, "RomFS IVFC header");
        if (CtrBinary.ReadUInt32LittleEndian(header, 0) != IvfcSignature)
        {
            throw new CtrFormatException(
                "The RomFS image does not start with an IVFC header.");
        }

        uint headerSize = CtrBinary.ReadUInt32LittleEndian(header, 0x54);
        if (headerSize < IvfcHeaderSize || headerSize > DefaultBlockSize)
        {
            throw new CtrFormatException(
                $"Unsupported RomFS IVFC header size 0x{headerSize:X}.");
        }

        uint level0Length = CtrBinary.ReadUInt32LittleEndian(header, 8);
        (long level1Logical, long level1Length, int level1Block) =
            ReadLevel(header, 0x0C);
        (long level2Logical, long level2Length, int level2Block) =
            ReadLevel(header, 0x24);
        (long level3Logical, long level3Length, int level3Block) =
            ReadLevel(header, 0x3C);

        if (level1Block != DefaultBlockSize ||
            level2Block != DefaultBlockSize ||
            level3Block != DefaultBlockSize)
        {
            throw new CtrFormatException(
                "Only the standard 0x1000-byte RomFS IVFC block size is supported.");
        }

        if (level1Logical != 0 ||
            level2Logical != CtrBinary.Align(level1Length, DefaultBlockSize) ||
            level3Logical != CtrBinary.Align(
                level2Logical + level2Length,
                DefaultBlockSize))
        {
            throw new CtrFormatException(
                "RomFS IVFC logical level offsets are inconsistent.");
        }

        long level0Offset = CtrBinary.Align(headerSize, 0x20);
        long level3Offset = CtrBinary.Align(
            level0Offset + level0Length,
            DefaultBlockSize);
        long level1Offset = checked(
            level3Offset +
            CtrBinary.Align(level3Length, DefaultBlockSize));
        long level2Offset = checked(
            level1Offset +
            CtrBinary.Align(level1Length, DefaultBlockSize));
        long imageLength = CtrBinary.Align(
            level2Offset + level2Length,
            DefaultBlockSize);

        CtrBinary.EnsureRange(source.Length, level0Offset, level0Length);
        CtrBinary.EnsureRange(source.Length, level1Offset, level1Length);
        CtrBinary.EnsureRange(source.Length, level2Offset, level2Length);
        CtrBinary.EnsureRange(source.Length, level3Offset, level3Length);

        var layout = new RomFsLayout(
            imageLength,
            level0Offset,
            level0Length,
            level1Offset,
            level1Length,
            level2Offset,
            level2Length,
            level3Offset,
            level3Length,
            DefaultBlockSize);

        if (verifyIntegrity)
        {
            VerifyIntegrity(source, layout);
        }

        return new RomFsDocument(ReadFiles(source, layout), layout);
    }

    public static void CopyFile(
        Stream source,
        RomFsFileEntry entry,
        Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        source.Position = entry.Offset;
        CtrBinary.CopyExactly(
            source,
            destination,
            entry.Length,
            $"RomFS file '{entry.Path}'");
    }

    private static (long Offset, long Length, int BlockSize) ReadLevel(
        ReadOnlySpan<byte> header,
        int offset)
    {
        long logicalOffset = checked(
            (long)CtrBinary.ReadUInt64LittleEndian(header, offset));
        long length = checked(
            (long)CtrBinary.ReadUInt64LittleEndian(header, offset + 8));
        uint blockPower = CtrBinary.ReadUInt32LittleEndian(
            header,
            offset + 16);
        if (blockPower > 30)
        {
            throw new CtrFormatException(
                $"Invalid RomFS IVFC block-size power {blockPower}.");
        }

        return (logicalOffset, length, 1 << checked((int)blockPower));
    }

    private static List<RomFsFileEntry> ReadFiles(
        Stream source,
        RomFsLayout layout)
    {
        var metadata = new byte[RomFsMetadataHeaderSize];
        CtrBinary.ReadExactlyAt(
            source,
            layout.Level3Offset,
            metadata,
            "RomFS metadata header");
        uint metadataSize = CtrBinary.ReadUInt32LittleEndian(metadata, 0);
        if (metadataSize != RomFsMetadataHeaderSize)
        {
            throw new CtrFormatException(
                $"Unsupported RomFS metadata header size 0x{metadataSize:X}.");
        }

        uint directoryTableOffset =
            CtrBinary.ReadUInt32LittleEndian(metadata, 0x0C);
        uint directoryTableLength =
            CtrBinary.ReadUInt32LittleEndian(metadata, 0x10);
        uint fileTableOffset =
            CtrBinary.ReadUInt32LittleEndian(metadata, 0x1C);
        uint fileTableLength =
            CtrBinary.ReadUInt32LittleEndian(metadata, 0x20);
        uint fileDataOffset =
            CtrBinary.ReadUInt32LittleEndian(metadata, 0x24);

        CtrBinary.EnsureRange(
            layout.Level3Length,
            directoryTableOffset,
            directoryTableLength);
        CtrBinary.EnsureRange(
            layout.Level3Length,
            fileTableOffset,
            fileTableLength);
        CtrBinary.EnsureRange(
            layout.Level3Length,
            fileDataOffset,
            0);

        var files = new List<RomFsFileEntry>();
        var visitedDirectories = new HashSet<uint>();
        ReadDirectory(
            source,
            layout,
            directoryTableOffset,
            directoryTableLength,
            fileTableOffset,
            fileTableLength,
            fileDataOffset,
            0,
            string.Empty,
            visitedDirectories,
            files);
        return files;
    }

    private static void ReadDirectory(
        Stream source,
        RomFsLayout layout,
        uint directoryTableOffset,
        uint directoryTableLength,
        uint fileTableOffset,
        uint fileTableLength,
        uint fileDataOffset,
        uint entryOffset,
        string parentPath,
        HashSet<uint> visitedDirectories,
        List<RomFsFileEntry> files)
    {
        if (!visitedDirectories.Add(entryOffset))
        {
            throw new CtrFormatException(
                $"RomFS directory chain contains a cycle at 0x{entryOffset:X}.");
        }

        byte[] entry = ReadMetadataEntry(
            source,
            layout.Level3Offset + directoryTableOffset,
            directoryTableLength,
            entryOffset,
            0x18,
            "directory");
        uint siblingOffset = CtrBinary.ReadUInt32LittleEndian(entry, 4);
        uint childDirectoryOffset =
            CtrBinary.ReadUInt32LittleEndian(entry, 8);
        uint childFileOffset =
            CtrBinary.ReadUInt32LittleEndian(entry, 12);
        string name = ReadName(entry, 0x14);
        string path = string.IsNullOrEmpty(name)
            ? parentPath
            : CombinePath(parentPath, name);

        var visitedFiles = new HashSet<uint>();
        uint currentFile = childFileOffset;
        while (currentFile != InvalidOffset)
        {
            if (!visitedFiles.Add(currentFile))
            {
                throw new CtrFormatException(
                    $"RomFS file chain contains a cycle at 0x{currentFile:X}.");
            }

            byte[] file = ReadMetadataEntry(
                source,
                layout.Level3Offset + fileTableOffset,
                fileTableLength,
                currentFile,
                0x20,
                "file");
            uint nextFile = CtrBinary.ReadUInt32LittleEndian(file, 4);
            long dataOffset = checked(
                (long)CtrBinary.ReadUInt64LittleEndian(file, 8));
            long dataLength = checked(
                (long)CtrBinary.ReadUInt64LittleEndian(file, 16));
            string fileName = ReadName(file, 0x1C);
            long absoluteOffset = checked(
                layout.Level3Offset + fileDataOffset + dataOffset);
            CtrBinary.EnsureRange(
                layout.Level3Offset + layout.Level3Length,
                absoluteOffset,
                dataLength);
            files.Add(
                new RomFsFileEntry(
                    CombinePath(path, fileName),
                    absoluteOffset,
                    dataLength));
            currentFile = nextFile;
        }

        uint currentDirectory = childDirectoryOffset;
        var siblingGuard = new HashSet<uint>();
        while (currentDirectory != InvalidOffset)
        {
            if (!siblingGuard.Add(currentDirectory))
            {
                throw new CtrFormatException(
                    "RomFS sibling directory chain contains a cycle.");
            }

            byte[] child = ReadMetadataEntry(
                source,
                layout.Level3Offset + directoryTableOffset,
                directoryTableLength,
                currentDirectory,
                0x18,
                "directory");
            uint nextDirectory =
                CtrBinary.ReadUInt32LittleEndian(child, 4);
            ReadDirectory(
                source,
                layout,
                directoryTableOffset,
                directoryTableLength,
                fileTableOffset,
                fileTableLength,
                fileDataOffset,
                currentDirectory,
                path,
                visitedDirectories,
                files);
            currentDirectory = nextDirectory;
        }

        _ = siblingOffset;
    }

    private static byte[] ReadMetadataEntry(
        Stream source,
        long tableStart,
        uint tableLength,
        uint entryOffset,
        int fixedLength,
        string entryKind)
    {
        if (entryOffset > tableLength ||
            fixedLength > tableLength - entryOffset)
        {
            throw new CtrFormatException(
                $"RomFS {entryKind} entry 0x{entryOffset:X} is outside its table.");
        }

        var fixedData = new byte[fixedLength];
        CtrBinary.ReadExactlyAt(
            source,
            tableStart + entryOffset,
            fixedData,
            $"RomFS {entryKind} entry");
        uint nameLength = CtrBinary.ReadUInt32LittleEndian(
            fixedData,
            fixedLength - 4);
        if ((nameLength & 1) != 0 ||
            nameLength > tableLength - entryOffset - fixedLength)
        {
            throw new CtrFormatException(
                $"RomFS {entryKind} name has invalid length 0x{nameLength:X}.");
        }

        var result = new byte[checked(fixedLength + (int)nameLength)];
        fixedData.CopyTo(result, 0);
        if (nameLength != 0)
        {
            CtrBinary.ReadExactlyAt(
                source,
                tableStart + entryOffset + fixedLength,
                result.AsSpan(fixedLength),
                $"RomFS {entryKind} name");
        }

        return result;
    }

    private static string ReadName(
        ReadOnlySpan<byte> entry,
        int nameLengthOffset)
    {
        int nameLength = checked(
            (int)CtrBinary.ReadUInt32LittleEndian(entry, nameLengthOffset));
        return Encoding.Unicode.GetString(
            entry.Slice(nameLengthOffset + 4, nameLength));
    }

    private static string CombinePath(string parent, string name)
    {
        return string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
    }

    private static void VerifyIntegrity(
        Stream source,
        RomFsLayout layout)
    {
        byte[] expectedLevel2 = ReadRegion(
            source,
            layout.Level2Offset,
            layout.Level2Length,
            "RomFS IVFC level 2");
        byte[] actualLevel2 = HashBlocks(
            source,
            layout.Level3Offset,
            layout.Level3Length,
            layout.BlockSize);
        VerifyHashLevel("2", expectedLevel2, actualLevel2);

        byte[] expectedLevel1 = ReadRegion(
            source,
            layout.Level1Offset,
            layout.Level1Length,
            "RomFS IVFC level 1");
        byte[] actualLevel1 = HashBlocks(
            expectedLevel2,
            layout.BlockSize);
        VerifyHashLevel("1", expectedLevel1, actualLevel1);

        byte[] expectedLevel0 = ReadRegion(
            source,
            layout.Level0Offset,
            layout.Level0Length,
            "RomFS IVFC level 0");
        byte[] actualLevel0 = HashBlocks(
            expectedLevel1,
            layout.BlockSize);
        VerifyHashLevel("0", expectedLevel0, actualLevel0);
    }

    private static byte[] ReadRegion(
        Stream source,
        long offset,
        long length,
        string name)
    {
        if (length > int.MaxValue)
        {
            throw new CtrFormatException($"{name} is too large to validate.");
        }

        var data = new byte[checked((int)length)];
        CtrBinary.ReadExactlyAt(source, offset, data, name);
        return data;
    }

    private static byte[] HashBlocks(
        Stream source,
        long offset,
        long length,
        int blockSize)
    {
        long blockCount = (length + blockSize - 1) / blockSize;
        if (blockCount > int.MaxValue / 0x20)
        {
            throw new CtrFormatException(
                "RomFS IVFC hash level is too large to validate.");
        }

        var result = new byte[checked((int)blockCount * 0x20)];
        var block = new byte[blockSize];
        source.Position = offset;
        for (int index = 0; index < blockCount; index++)
        {
            block.AsSpan().Clear();
            int wanted = (int)Math.Min(blockSize, length - ((long)index * blockSize));
            source.ReadExactly(block.AsSpan(0, wanted));
            SHA256.HashData(block, result.AsSpan(index * 0x20, 0x20));
        }

        return result;
    }

    internal static byte[] HashBlocks(
        ReadOnlySpan<byte> data,
        int blockSize)
    {
        int blockCount = checked((data.Length + blockSize - 1) / blockSize);
        var result = new byte[checked(blockCount * 0x20)];
        var block = new byte[blockSize];
        for (int index = 0; index < blockCount; index++)
        {
            block.AsSpan().Clear();
            int offset = index * blockSize;
            int length = Math.Min(blockSize, data.Length - offset);
            data.Slice(offset, length).CopyTo(block);
            SHA256.HashData(block, result.AsSpan(index * 0x20, 0x20));
        }

        return result;
    }

    private static void VerifyHashLevel(
        string level,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new CtrFormatException(
                $"RomFS IVFC level {level} failed SHA-256 verification.");
        }
    }
}
