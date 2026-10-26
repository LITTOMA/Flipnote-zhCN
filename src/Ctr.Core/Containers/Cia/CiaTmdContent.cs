namespace Ctr.Core.Containers.Cia;

public sealed record CiaTmdContent(
    uint Id,
    ushort Index,
    ushort Type,
    long Length,
    ReadOnlyMemory<byte> Hash);
