namespace Ctr.Core.Containers.ExeFs;

/// <summary>
/// Parsed ExeFS metadata. Entry offsets are absolute relative to the start of
/// the ExeFS image and can be copied independently by <see cref="ExeFsReader"/>.
/// </summary>
public sealed class ExeFsDocument
{
    public ExeFsDocument(
        IReadOnlyList<ExeFsEntry> entries,
        long imageLength)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageLength, 0);
        Entries = entries;
        ImageLength = imageLength;
    }

    public IReadOnlyList<ExeFsEntry> Entries { get; }

    public long ImageLength { get; }
}
