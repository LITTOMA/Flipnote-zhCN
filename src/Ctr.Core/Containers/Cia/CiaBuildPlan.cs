using Ctr.Core.IO;

namespace Ctr.Core.Containers.Cia;

public sealed record CiaBuildPlan(
    IReadOnlyBinarySource CertificateChain,
    ReadOnlyMemory<byte> TicketTemplate,
    ReadOnlyMemory<byte> TmdTemplate,
    IReadOnlyList<CiaBuildContent> Contents,
    IReadOnlyBinarySource? Meta = null,
    bool MarkSignaturesAsFake = true,
    ushort Type = 0,
    ushort Version = 0);
