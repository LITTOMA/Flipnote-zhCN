using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.CodeSet;

/// <summary>
/// Reads and updates the CodeSet compression flag in a Nintendo 3DS
/// extended header.
/// </summary>
public static class CodeSetExtendedHeader
{
    private const int MinimumLength = 0x400;
    private const int FlagsOffset = 0x0D;
    private const byte CompressedCodeFlag = 1;

    public static bool IsCompressed(
        ReadOnlySpan<byte> extendedHeader)
    {
        Validate(extendedHeader);
        return (extendedHeader[FlagsOffset] &
            CompressedCodeFlag) != 0;
    }

    public static ReadOnlyMemory<byte> WithCompression(
        ReadOnlySpan<byte> extendedHeader,
        bool compressed)
    {
        Validate(extendedHeader);
        byte[] result = extendedHeader.ToArray();
        result[FlagsOffset] = compressed
            ? (byte)(result[FlagsOffset] |
                CompressedCodeFlag)
            : (byte)(result[FlagsOffset] &
                ~CompressedCodeFlag);
        return result;
    }

    private static void Validate(
        ReadOnlySpan<byte> extendedHeader)
    {
        if (extendedHeader.Length < MinimumLength)
        {
            throw new CtrFormatException(
                $"A Nintendo 3DS extended header must contain at least 0x{MinimumLength:X} bytes.");
        }
    }
}
