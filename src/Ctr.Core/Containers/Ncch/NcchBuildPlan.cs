using Ctr.Core.IO;

namespace Ctr.Core.Containers.Ncch;

public sealed record NcchBuildPlan(
    NcchHeader HeaderTemplate,
    IReadOnlyBinarySource? ExtendedHeader,
    IReadOnlyBinarySource? Logo,
    IReadOnlyBinarySource? Plain,
    IReadOnlyBinarySource? ExeFs,
    IReadOnlyBinarySource? RomFs,
    NcchKeySet? EncryptionKeys = null,
    bool AlignSectionsTo0x1000 = true);
