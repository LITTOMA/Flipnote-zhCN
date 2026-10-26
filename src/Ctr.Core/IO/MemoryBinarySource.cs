namespace Ctr.Core.IO;

/// <summary>
/// An immutable in-memory binary source, primarily useful for generated
/// sections and small format records.
/// </summary>
public sealed class MemoryBinarySource : IReadOnlyBinarySource
{
    private readonly ReadOnlyMemory<byte> _data;

    public MemoryBinarySource(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public long Length => _data.Length;

    public Stream OpenRead()
    {
        return new MemoryStream(_data.ToArray(), writable: false);
    }
}
