namespace Ctr.Core.Containers.ExeFs;

public sealed record ExeFsEntry(
    string Name,
    int HeaderIndex,
    long Offset,
    long Length,
    ReadOnlyMemory<byte> ExpectedHash);
