namespace Ctr.Core.Containers.Ncch;

public sealed class NcchDocument
{
    public NcchDocument(
        NcchHeader header,
        IReadOnlyList<NcchRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(regions);
        Header = header;
        Regions = regions;
    }

    public NcchHeader Header { get; }

    public IReadOnlyList<NcchRegion> Regions { get; }
}
