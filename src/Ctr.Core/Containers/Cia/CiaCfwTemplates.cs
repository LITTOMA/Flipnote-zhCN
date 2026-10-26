using System.Buffers.Binary;
using System.Text;
using Ctr.Core.Containers.Ncch;
using Ctr.Core.IO;

namespace Ctr.Core.Containers.Cia;

/// <summary>
/// Creates unsigned system metadata for a CFW-only CIA. The resulting
/// signatures are intentionally invalid and cannot represent a retail-signed
/// Nintendo package.
/// </summary>
public static class CiaCfwTemplates
{
    private const int TicketSize = 0x350;

    public static CiaBuildPlan CreateNcchPlan(
        NcchHeader ncchHeader,
        IReadOnlyBinarySource ncch,
        IReadOnlyBinarySource certificateChain,
        ushort titleVersion = 0,
        uint saveDataSize = 0)
    {
        ArgumentNullException.ThrowIfNull(ncchHeader);
        ArgumentNullException.ThrowIfNull(ncch);
        ArgumentNullException.ThrowIfNull(certificateChain);
        return new CiaBuildPlan(
            certificateChain,
            CreateTicket(ncchHeader.ProgramId, titleVersion),
            CreateTmd(
                ncchHeader.ProgramId,
                titleVersion,
                saveDataSize),
            [
                new CiaBuildContent(
                    0,
                    0,
                    0,
                    ncch),
            ]);
    }

    public static byte[] CreateTicket(
        ulong titleId,
        ushort titleVersion = 0)
    {
        var ticket = new byte[TicketSize];
        WriteRsa2048Sha256SignatureHeader(ticket);
        WriteAscii(
            ticket.AsSpan(0x140, 0x40),
            "Root-CA00000003-XS0000000c");
        ticket.AsSpan(0x180, 0x3C).Fill(0xFF);
        ticket[0x1BC] = 1;
        ticket.AsSpan(CiaTicket.EncryptedTitleKeyOffset, 16)
            .Fill(0xFF);
        BinaryPrimitives.WriteUInt64BigEndian(
            ticket.AsSpan(CiaTicket.TitleIdOffset),
            titleId);
        BinaryPrimitives.WriteUInt16BigEndian(
            ticket.AsSpan(0x1E6),
            titleVersion);
        ticket[0x221] = 1;

        const int contentIndexOffset = 0x2A4;
        const int contentIndexSize = 0xAC;
        ticket[contentIndexOffset + 1] = 1;
        ticket[contentIndexOffset + 3] = 0x14;
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(contentIndexOffset + 4),
            contentIndexSize);
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(contentIndexOffset + 8),
            0x14);
        ticket[contentIndexOffset + 13] = 1;
        ticket[contentIndexOffset + 15] = 0x14;

        int dataHeaderOffset = contentIndexOffset + 0x14;
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(dataHeaderOffset),
            0x28);
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(dataHeaderOffset + 4),
            1);
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(dataHeaderOffset + 8),
            0x84);
        BinaryPrimitives.WriteUInt32BigEndian(
            ticket.AsSpan(dataHeaderOffset + 12),
            0x84);
        BinaryPrimitives.WriteUInt16BigEndian(
            ticket.AsSpan(dataHeaderOffset + 16),
            3);
        ticket.AsSpan(contentIndexOffset + 0x2C, 0x80)
            .Fill(0xFF);
        return ticket;
    }

    public static byte[] CreateTmd(
        ulong titleId,
        ushort titleVersion = 0,
        uint saveDataSize = 0)
    {
        var tmd = new byte[CiaTmd.HeaderSize];
        WriteRsa2048Sha256SignatureHeader(tmd);
        WriteAscii(
            tmd.AsSpan(0x140, 0x40),
            "Root-CA00000003-CP0000000b");
        tmd[0x180] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(
            tmd.AsSpan(CiaTmd.TitleIdOffset),
            titleId);
        BinaryPrimitives.WriteUInt32BigEndian(
            tmd.AsSpan(0x194),
            0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(
            tmd.AsSpan(0x19A),
            saveDataSize);
        BinaryPrimitives.WriteUInt16BigEndian(
            tmd.AsSpan(0x1DC),
            titleVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            tmd.AsSpan(CiaTmd.ContentCountOffset),
            1);
        return tmd;
    }

    private static void WriteRsa2048Sha256SignatureHeader(
        Span<byte> destination)
    {
        destination[0] = 0;
        destination[1] = 1;
        destination[2] = 0;
        destination[3] = 4;
        destination.Slice(4, 0x100).Fill(0xFF);
    }

    private static void WriteAscii(
        Span<byte> destination,
        string value)
    {
        int written = Encoding.ASCII.GetBytes(value, destination);
        destination[written..].Clear();
    }
}
