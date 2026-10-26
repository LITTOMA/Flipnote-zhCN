using Ctr.Core.Containers.Ncch;

namespace Ctr.Core;

/// <summary>
/// Public retail NCCH KeyX values used by common 3DS tooling. These are
/// key-scrambler inputs, not a CIA common key or console-unique secret.
/// Seed crypto is deliberately unsupported.
/// </summary>
public static class RetailNcchKeys
{
    private static ReadOnlySpan<byte> Slot0x18 =>
    [
        0x82, 0xE9, 0xC9, 0xBE, 0xBF, 0xB8, 0xBD, 0xB8,
        0x75, 0xEC, 0xC0, 0xA0, 0x7D, 0x47, 0x43, 0x74,
    ];

    private static ReadOnlySpan<byte> Slot0x1B =>
    [
        0x45, 0xAD, 0x04, 0x95, 0x39, 0x92, 0xC7, 0xC8,
        0x93, 0x72, 0x4A, 0x9A, 0x7B, 0xCE, 0x61, 0x82,
    ];

    private static ReadOnlySpan<byte> Slot0x25 =>
    [
        0xCE, 0xE7, 0xD8, 0xAB, 0x30, 0xC0, 0x0D, 0xAE,
        0x85, 0x0E, 0xF5, 0xE3, 0x82, 0xAC, 0x5A, 0xF3,
    ];

    private static ReadOnlySpan<byte> Slot0x2C =>
    [
        0xB9, 0x8E, 0x95, 0xCE, 0xCA, 0x3E, 0x4D, 0x17,
        0x1F, 0x76, 0xA9, 0x4D, 0xE9, 0x34, 0xC0, 0x53,
    ];

    private static ReadOnlySpan<byte> SystemFixedKey =>
    [
        0x52, 0x7C, 0xE6, 0x30, 0xA9, 0xCA, 0x30, 0x5F,
        0x36, 0x96, 0xF3, 0xCD, 0xE9, 0x54, 0x19, 0x4B,
    ];

    public static NcchKeySet? Create(NcchHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!header.IsEncrypted)
        {
            return null;
        }

        ReadOnlySpan<byte> flags = header.Flags.Span;
        if ((flags[7] & 0x01) != 0)
        {
            byte[] key = UsesSystemFixedKey(header)
                ? SystemFixedKey.ToArray()
                : new byte[16];
            return new NcchKeySet(key, key);
        }

        if ((flags[7] & 0x20) != 0)
        {
            throw new NotSupportedException(
                "Seed crypto requires title-specific seed material.");
        }

        return NcchKeySet.FromKeyXs(
            header,
            Slot0x2C,
            SelectSecondaryKeyX(flags[3]));
    }

    private static ReadOnlySpan<byte> SelectSecondaryKeyX(byte selector) =>
        selector switch
        {
            0x01 => Slot0x25,
            0x0A => Slot0x18,
            0x0B => Slot0x1B,
            _ => Slot0x2C,
        };

    private static bool UsesSystemFixedKey(NcchHeader header)
    {
        uint programIdHigh = checked((uint)(header.ProgramId >> 32));
        return (programIdHigh >> 14) == 0x10 &&
            (programIdHigh & 0x10) != 0;
    }
}
