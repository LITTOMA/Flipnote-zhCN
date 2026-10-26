using System.Security.Cryptography;

namespace Ctr.Core.Cryptography;

public static class AesCbc
{
    public static byte[] DecryptBlock(
        ReadOnlySpan<byte> encrypted,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> initializationVector)
    {
        ValidateParameters(encrypted.Length, key, initializationVector);
        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = initializationVector.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform transform = aes.CreateDecryptor();
        byte[] input = encrypted.ToArray();
        return transform.TransformFinalBlock(input, 0, input.Length);
    }

    public static void Decrypt(
        Stream source,
        Stream destination,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> initializationVector,
        long byteCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ValidateParameters(byteCount, key, initializationVector);

        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.IV = initializationVector.ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform transform = aes.CreateDecryptor();
        using var crypto = new CryptoStream(
            source,
            transform,
            CryptoStreamMode.Read,
            leaveOpen: true);
        CopyExactly(crypto, destination, byteCount);
    }

    private static void ValidateParameters(
        long length,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> initializationVector)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException(
                "AES-CBC requires a 16-byte key.",
                nameof(key));
        }

        if (initializationVector.Length != 16)
        {
            throw new ArgumentException(
                "AES-CBC requires a 16-byte initialization vector.",
                nameof(initializationVector));
        }

        if (length % 16 != 0)
        {
            throw new ArgumentException(
                "Unpadded AES-CBC data must be a multiple of 16 bytes.",
                nameof(length));
        }
    }

    private static void CopyExactly(
        Stream source,
        Stream destination,
        long length)
    {
        var buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining != 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Unexpected end of AES-CBC stream.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
