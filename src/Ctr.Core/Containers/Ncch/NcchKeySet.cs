using Ctr.Core.Cryptography;

namespace Ctr.Core.Containers.Ncch;

/// <summary>
/// Already-scrambled normal keys used by NCCH. KeyX material is deliberately
/// not embedded in the library.
/// </summary>
public sealed class NcchKeySet
{
    public NcchKeySet(
        ReadOnlyMemory<byte> primaryKey,
        ReadOnlyMemory<byte> secondaryKey)
    {
        if (primaryKey.Length != 16)
        {
            throw new ArgumentException(
                "The NCCH primary normal key must be 16 bytes.",
                nameof(primaryKey));
        }

        if (secondaryKey.Length != 16)
        {
            throw new ArgumentException(
                "The NCCH secondary normal key must be 16 bytes.",
                nameof(secondaryKey));
        }

        PrimaryKey = primaryKey.ToArray();
        SecondaryKey = secondaryKey.ToArray();
    }

    public ReadOnlyMemory<byte> PrimaryKey { get; }

    public ReadOnlyMemory<byte> SecondaryKey { get; }

    public static NcchKeySet FromKeyXs(
        NcchHeader header,
        ReadOnlySpan<byte> primaryKeyX,
        ReadOnlySpan<byte> secondaryKeyX)
    {
        ArgumentNullException.ThrowIfNull(header);
        return new NcchKeySet(
            CtrKeyScrambler.DeriveNormalKey(
                primaryKeyX,
                header.KeyY.Span),
            CtrKeyScrambler.DeriveNormalKey(
                secondaryKeyX,
                header.KeyY.Span));
    }
}
