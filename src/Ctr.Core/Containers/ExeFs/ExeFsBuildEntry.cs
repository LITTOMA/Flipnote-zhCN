using Ctr.Core.IO;

namespace Ctr.Core.Containers.ExeFs;

public sealed record ExeFsBuildEntry(
    string Name,
    IReadOnlyBinarySource Content);
