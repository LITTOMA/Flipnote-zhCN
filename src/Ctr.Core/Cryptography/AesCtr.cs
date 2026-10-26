using System.Buffers;
using System.Security.Cryptography;

namespace Ctr.Core.Cryptography;

/// <summary>
/// Streaming AES-CTR transform with a big-endian 128-bit counter. Encryption
/// and decryption are the same operation.
/// </summary>
public static class AesCtr
{
    private const int BlockSize = 16;
    private const int BufferSize = 1024 * 1024;

    public static void Transform(
        Stream source,
        Stream destination,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> initialCounter,
        long byteCount,
        long counterOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ArgumentOutOfRangeException.ThrowIfNegative(counterOffset);
        if (key.Length != BlockSize)
        {
            throw new ArgumentException(
                "AES-CTR requires a 16-byte key.",
                nameof(key));
        }

        if (initialCounter.Length != BlockSize)
        {
            throw new ArgumentException(
                "AES-CTR requires a 16-byte initial counter.",
                nameof(initialCounter));
        }

        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        byte[] counter = initialCounter.ToArray();
        AddBlocks(counter, checked((ulong)(counterOffset / BlockSize)));
        int skip = checked((int)(counterOffset % BlockSize));
        byte[] input = ArrayPool<byte>.Shared.Rent(BufferSize);
        byte[] output = ArrayPool<byte>.Shared.Rent(BufferSize);
        Span<byte> keyStream = stackalloc byte[BlockSize];
        try
        {
            long remaining = byteCount;
            while (remaining != 0)
            {
                int wanted = (int)Math.Min(BufferSize, remaining);
                source.ReadExactly(input.AsSpan(0, wanted));
                int inputOffset = 0;
                while (inputOffset != wanted)
                {
                    aes.EncryptEcb(
                        counter,
                        keyStream,
                        PaddingMode.None);
                    int transformed = Math.Min(
                        BlockSize - skip,
                        wanted - inputOffset);
                    for (int index = 0; index < transformed; index++)
                    {
                        output[inputOffset + index] =
                            (byte)(input[inputOffset + index] ^
                            keyStream[skip + index]);
                    }

                    inputOffset += transformed;
                    skip = 0;
                    Increment(counter);
                }

                destination.Write(output, 0, wanted);
                remaining -= wanted;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                input.AsSpan(0, BufferSize));
            CryptographicOperations.ZeroMemory(
                output.AsSpan(0, BufferSize));
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private static void AddBlocks(Span<byte> counter, ulong blocks)
    {
        int index = counter.Length - 1;
        ulong carry = blocks;
        while (carry != 0 && index >= 0)
        {
            ulong sum = counter[index] + (carry & 0xFF);
            counter[index] = (byte)sum;
            carry = (carry >> 8) + (sum >> 8);
            index--;
        }

        if (carry != 0)
        {
            throw new OverflowException("AES-CTR counter overflow.");
        }
    }

    private static void Increment(Span<byte> counter)
    {
        for (int index = counter.Length - 1; index >= 0; index--)
        {
            counter[index]++;
            if (counter[index] != 0)
            {
                return;
            }
        }

        throw new OverflowException("AES-CTR counter overflow.");
    }
}
