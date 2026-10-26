using Ctr.Core.IO;

namespace Ctr.Core.Containers.Cia;

public sealed record CiaBuildContent(
    uint Id,
    ushort Index,
    ushort Type,
    IReadOnlyBinarySource Source);
