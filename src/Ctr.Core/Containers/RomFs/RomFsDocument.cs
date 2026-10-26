namespace Ctr.Core.Containers.RomFs;

/// <summary>
/// Parsed RomFS metadata. File data remains in the source image and is
/// addressed by absolute offsets.
/// </summary>
public sealed class RomFsDocument
{
    public RomFsDocument(
        IReadOnlyList<RomFsFileEntry> files,
        RomFsLayout layout)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(layout);
        Files = files;
        Layout = layout;
    }

    public IReadOnlyList<RomFsFileEntry> Files { get; }

    public RomFsLayout Layout { get; }
}
