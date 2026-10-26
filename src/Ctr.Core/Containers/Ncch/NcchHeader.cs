using System.Text;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.Ncch;

/// <summary>
/// The complete 0x200-byte NCCH header together with its decoded fields.
/// The raw header is retained because Nintendo signatures cannot be
/// regenerated without the platform holder's private key.
/// </summary>
public sealed class NcchHeader
{
    public NcchHeader(ReadOnlyMemory<byte> raw)
    {
        if (raw.Length != NcchReader.HeaderSize ||
            !raw.Span.Slice(0x100, 4).SequenceEqual("NCCH"u8))
        {
            throw new CtrFormatException(
                "An NCCH header template must be exactly 0x200 bytes and contain the NCCH signature.");
        }

        Raw = raw;
        ReadOnlySpan<byte> data = raw.Span;
        ContentSize = CtrBinary.ReadUInt32LittleEndian(data, 0x104);
        PartitionId = CtrBinary.ReadUInt64LittleEndian(data, 0x108);
        Version = CtrBinary.ReadUInt16LittleEndian(data, 0x112);
        ProgramId = CtrBinary.ReadUInt64LittleEndian(data, 0x118);
        ProductCode = ReadAscii(data.Slice(0x150, 0x10));
        ExtendedHeaderSize = CtrBinary.ReadUInt32LittleEndian(data, 0x180);
        Flags = raw.Slice(0x188, 8);
        MediaUnitSize = checked(1 << (Flags.Span[6] + 9));
        IsEncrypted = (Flags.Span[7] & 0x04) == 0;
    }

    public ReadOnlyMemory<byte> Raw { get; }

    public uint ContentSize { get; }

    /// <summary>
    /// The NCCH KeyY stored in the first 16 bytes of the RSA signature.
    /// </summary>
    public ReadOnlyMemory<byte> KeyY => Raw[..16];

    public ulong PartitionId { get; }

    public ushort Version { get; }

    public ulong ProgramId { get; }

    public string ProductCode { get; }

    public uint ExtendedHeaderSize { get; }

    public ReadOnlyMemory<byte> Flags { get; }

    public int MediaUnitSize { get; }

    public bool IsEncrypted { get; }

    private static string ReadAscii(ReadOnlySpan<byte> value)
    {
        int terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(
            terminator < 0 ? value : value[..terminator]);
    }
}
