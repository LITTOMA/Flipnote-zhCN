namespace Ctr.Core.Diagnostics;

/// <summary>
/// Indicates that a resource violates the binary format or a declared range.
/// The exception is deliberately distinct from I/O failures so the manager can
/// show a useful, non-destructive diagnostic for a bad or unsupported ROM.
/// </summary>
public sealed class CtrFormatException : IOException
{
    public CtrFormatException(string message)
        : base(message)
    {
    }

    public CtrFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
