namespace Ctr.Core.Containers.Cia;

public sealed class CiaTmd
{
    public const int HeaderSize = 0xB04;
    public const int ContentRecordSize = 0x30;
    internal const int TitleIdOffset = 0x18C;
    internal const int ContentCountOffset = 0x1DE;

    internal CiaTmd(
        ReadOnlyMemory<byte> raw,
        IReadOnlyList<CiaTmdContent> contents)
    {
        Raw = raw;
        TitleId = CtrBinary.ReadUInt64BigEndian(
            raw.Span,
            TitleIdOffset);
        Contents = contents;
    }

    public ReadOnlyMemory<byte> Raw { get; }

    public ulong TitleId { get; }

    public IReadOnlyList<CiaTmdContent> Contents { get; }
}
