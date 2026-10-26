namespace Ctr.Core.Containers.RomFs;

public sealed record RomFsLayout(
    long ImageLength,
    long Level0Offset,
    long Level0Length,
    long Level1Offset,
    long Level1Length,
    long Level2Offset,
    long Level2Length,
    long Level3Offset,
    long Level3Length,
    int BlockSize);
