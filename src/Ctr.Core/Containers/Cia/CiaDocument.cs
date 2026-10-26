namespace Ctr.Core.Containers.Cia;

public sealed class CiaDocument
{
    public CiaDocument(
        ushort type,
        ushort version,
        CiaSection certificateChain,
        CiaSection ticketSection,
        CiaSection tmdSection,
        CiaSection contentSection,
        CiaSection? metaSection,
        CiaTicket ticket,
        CiaTmd tmd,
        IReadOnlyList<CiaContent> contents)
    {
        CertificateChain = certificateChain;
        TicketSection = ticketSection;
        TmdSection = tmdSection;
        ContentSection = contentSection;
        MetaSection = metaSection;
        Ticket = ticket;
        Tmd = tmd;
        Contents = contents;
        Type = type;
        Version = version;
    }

    public ushort Type { get; }

    public ushort Version { get; }

    public CiaSection CertificateChain { get; }

    public CiaSection TicketSection { get; }

    public CiaSection TmdSection { get; }

    public CiaSection ContentSection { get; }

    public CiaSection? MetaSection { get; }

    public CiaTicket Ticket { get; }

    public CiaTmd Tmd { get; }

    public IReadOnlyList<CiaContent> Contents { get; }
}
