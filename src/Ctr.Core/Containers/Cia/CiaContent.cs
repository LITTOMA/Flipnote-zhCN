namespace Ctr.Core.Containers.Cia;

public sealed record CiaContent(
    uint Id,
    ushort Index,
    ushort Type,
    long Offset,
    long Length,
    ReadOnlyMemory<byte> ExpectedHash)
{
    public bool IsEncrypted => (Type & 1) != 0;
}
