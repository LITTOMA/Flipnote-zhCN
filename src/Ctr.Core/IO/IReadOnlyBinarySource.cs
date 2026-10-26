namespace Ctr.Core.IO;

/// <summary>
/// Opens repeatable, read-only access to binary input without tying format
/// writers to files, directories, or a particular project layout.
/// </summary>
public interface IReadOnlyBinarySource
{
    long Length { get; }

    Stream OpenRead();
}
