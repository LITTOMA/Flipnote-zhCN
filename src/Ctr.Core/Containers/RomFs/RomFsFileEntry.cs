namespace Ctr.Core.Containers.RomFs;

public sealed record RomFsFileEntry(
    string Path,
    long Offset,
    long Length);
