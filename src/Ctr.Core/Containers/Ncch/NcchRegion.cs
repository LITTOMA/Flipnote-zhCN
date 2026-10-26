namespace Ctr.Core.Containers.Ncch;

public sealed record NcchRegion(
    NcchRegionKind Kind,
    long Offset,
    long Length,
    long HashRegionLength,
    ReadOnlyMemory<byte> ExpectedHash,
    bool IsEncrypted);
