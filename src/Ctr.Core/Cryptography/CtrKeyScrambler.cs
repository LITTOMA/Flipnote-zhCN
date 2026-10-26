using System.Buffers.Binary;

namespace Ctr.Core.Cryptography;

/// <summary>
/// Implements the Nintendo 3DS key-scrambler transformation. It derives a
/// short-lived AES normal key from a caller-owned KeyX and an NCCH header's
/// KeyY; it does not locate, persist, or expose platform KeyX material.
/// </summary>
public static class CtrKeyScrambler
{
    private static readonly UInt128 ScramblerConstant =
        ((UInt128)0x1FF9E9AAC5FE0408UL << 64) | 0x024591DC5D52768AUL;

    public static byte[] DeriveNormalKey(ReadOnlySpan<byte> keyX, ReadOnlySpan<byte> keyY)
    {
        if (keyX.Length != 16)
        {
            throw new ArgumentException("A 3DS KeyX must contain exactly 16 bytes.", nameof(keyX));
        }

        if (keyY.Length != 16)
        {
            throw new ArgumentException("A 3DS KeyY must contain exactly 16 bytes.", nameof(keyY));
        }

        UInt128 x = BinaryPrimitives.ReadUInt128BigEndian(keyX);
        UInt128 y = BinaryPrimitives.ReadUInt128BigEndian(keyY);
        UInt128 mixed = RotateLeft(x, 2) ^ y;
        UInt128 normalKey = RotateLeft(unchecked(mixed + ScramblerConstant), 87);

        var result = new byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(result, normalKey);
        return result;
    }

    private static UInt128 RotateLeft(UInt128 value, int bits)
    {
        bits &= 127;
        return bits == 0
            ? value
            : (value << bits) | (value >> (128 - bits));
    }
}
