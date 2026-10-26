using Ctr.Core.Cryptography;

namespace Ctr.Core.Containers.Cia;

public sealed class CiaTicket
{
    internal const int EncryptedTitleKeyOffset = 0x1BF;
    internal const int TitleIdOffset = 0x1DC;
    internal const int CommonKeyIndexOffset = 0x1F1;

    internal CiaTicket(ReadOnlyMemory<byte> raw)
    {
        Raw = raw;
        EncryptedTitleKey = raw.Slice(EncryptedTitleKeyOffset, 16);
        TitleId = CtrBinary.ReadUInt64BigEndian(raw.Span, TitleIdOffset);
        CommonKeyIndex = raw.Span[CommonKeyIndexOffset];
    }

    public ReadOnlyMemory<byte> Raw { get; }

    public ReadOnlyMemory<byte> EncryptedTitleKey { get; }

    public ulong TitleId { get; }

    public byte CommonKeyIndex { get; }

    public byte[] DecryptTitleKey(ReadOnlySpan<byte> commonKey)
    {
        Span<byte> initializationVector = stackalloc byte[16];
        for (int index = 0; index < sizeof(ulong); index++)
        {
            initializationVector[index] = (byte)(
                TitleId >> ((7 - index) * 8));
        }

        return AesCbc.DecryptBlock(
            EncryptedTitleKey.Span,
            commonKey,
            initializationVector);
    }
}
