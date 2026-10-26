using Ctr.Core.IO;

namespace Ctr.Core.Containers.RomFs;

public sealed record RomFsBuildFile(
    string Path,
    IReadOnlyBinarySource Content);
