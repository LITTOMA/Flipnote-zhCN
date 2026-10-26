namespace Ctr.Core.IO;

/// <summary>
/// Repeatable file-backed binary source for CTR writers.
/// </summary>
public sealed class FileBinarySource : IReadOnlyBinarySource
{
    private readonly string _path;

    public FileBinarySource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Length = new FileInfo(_path).Length;
    }

    public long Length { get; }

    public Stream OpenRead() => new FileStream(
        _path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);
}
