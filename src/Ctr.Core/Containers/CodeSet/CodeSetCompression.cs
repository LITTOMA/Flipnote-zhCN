using System.Buffers.Binary;
using Ctr.Core.Diagnostics;

namespace Ctr.Core.Containers.CodeSet;

/// <summary>
/// Reads and writes the backwards-LZ stream used by compressed Nintendo 3DS
/// ExeFS <c>.code</c> entries.
/// </summary>
public static class CodeSetCompression
{
    private const int WindowSize = 4098;
    private const int MinimumMatchLength = 3;
    private const int MaximumMatchLength = 0x0F + MinimumMatchLength;

    public static ReadOnlyMemory<byte> Decompress(
        ReadOnlySpan<byte> source)
    {
        if (source.Length < 8)
        {
            throw new CtrFormatException(
                "A compressed CodeSet must contain an 8-byte footer.");
        }

        uint regionAndHeader =
            BinaryPrimitives.ReadUInt32LittleEndian(
                source[^8..]);
        int compressedRegionLength =
            checked((int)(regionAndHeader & 0x00FF_FFFF));
        int headerLength = checked(
            (int)(regionAndHeader >> 24));
        uint additionalLength =
            BinaryPrimitives.ReadUInt32LittleEndian(
                source[^4..]);
        if (headerLength is < 8 or > 11 ||
            compressedRegionLength < headerLength ||
            compressedRegionLength > source.Length ||
            additionalLength > int.MaxValue - source.Length)
        {
            throw new CtrFormatException(
                "The compressed CodeSet footer is invalid.");
        }

        int outputLength = checked(
            source.Length + (int)additionalLength);
        byte[] output = new byte[outputLength];
        source.CopyTo(output);
        int input = source.Length - headerLength;
        int inputStart =
            source.Length - compressedRegionLength;
        int destination = outputLength;
        while (input > inputStart)
        {
            byte flags = source[--input];
            for (int bit = 0;
                 bit < 8 &&
                 input > inputStart &&
                 destination > inputStart;
                 bit++)
            {
                if ((flags & 0x80) == 0)
                {
                    output[--destination] =
                        source[--input];
                }
                else
                {
                    if (input - inputStart < 2)
                    {
                        throw new CtrFormatException(
                            "A compressed CodeSet back-reference crosses the encoded region boundary.");
                    }

                    input -= 2;
                    int token =
                        source[input] |
                        (source[input + 1] << 8);
                    int length = (token >> 12) + 3;
                    int distance = (token & 0x0FFF) + 2;
                    if (length > destination - inputStart)
                    {
                        throw new CtrFormatException(
                            "A compressed CodeSet back-reference exceeds the decoded prefix.");
                    }

                    for (int index = 0;
                         index < length;
                         index++)
                    {
                        int sourceIndex = checked(
                            destination + distance);
                        if ((uint)sourceIndex >=
                            (uint)output.Length)
                        {
                            throw new CtrFormatException(
                                "A compressed CodeSet back-reference points beyond the decoded image.");
                        }

                        output[--destination] =
                            output[sourceIndex];
                    }
                }

                flags <<= 1;
            }
        }

        if (input != inputStart ||
            destination != inputStart)
        {
            throw new CtrFormatException(
                $"The compressed CodeSet ended at input 0x{input:X} and output 0x{destination:X}; expected the unchanged prefix boundary 0x{inputStart:X}.");
        }

        return output;
    }

    /// <summary>
    /// Compresses a CodeSet image into the same backwards-LZ representation
    /// read by <see cref="Decompress"/>. The data is encoded from its end
    /// toward its front with a 4098-byte sliding window; the encoded stream
    /// is then trimmed so the longest useful prefix stays literal, aligned
    /// to four bytes, and followed by the eight-byte footer.
    /// </summary>
    public static ReadOnlyMemory<byte> Compress(
        ReadOnlySpan<byte> source)
    {
        if (source.Length <= sizeof(ulong))
        {
            throw new CtrFormatException(
                "A CodeSet must be larger than its compression footer.");
        }

        // The encoded stream is built backwards from the end of a scratch
        // buffer sized like the uncompressed image. Incompressible input
        // would run out of space before reaching the front.
        byte[] scratch = new byte[source.Length];
        var window = new MatchWindow();
        int input = source.Length;
        int output = source.Length;
        while (input > 0 && output > 0)
        {
            int flagPosition = --output;
            byte flags = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int maximumLength = Math.Min(
                    Math.Min(MaximumMatchLength, input),
                    source.Length - input);
                int offset = 0;
                int matchLength = window.Search(
                    source,
                    input,
                    out offset,
                    maximumLength);
                if (matchLength < MinimumMatchLength)
                {
                    if (output < 1)
                    {
                        throw new CtrFormatException(
                            "The CodeSet could not be encoded within its compressed bound.");
                    }

                    window.Slide(source, input, 1);
                    scratch[--output] = source[--input];
                }
                else
                {
                    if (output < 2)
                    {
                        throw new CtrFormatException(
                            "The CodeSet could not be encoded within its compressed bound.");
                    }

                    flags |= (byte)(0x80 >> bit);
                    window.Slide(source, input, matchLength);
                    input -= matchLength;
                    int lengthField =
                        matchLength - MinimumMatchLength;
                    int offsetField = offset - MinimumMatchLength;
                    scratch[--output] = (byte)(
                        (lengthField << 4) |
                        ((offsetField >> 8) & 0x0F));
                    scratch[--output] =
                        (byte)(offsetField & 0xFF);
                }

                if (input <= 0)
                {
                    break;
                }
            }

            scratch[flagPosition] = flags;
        }

        int encodedLength = source.Length - output;
        int remainingSource = source.Length;
        int remainingStream = encodedLength;
        int sourceSafe = 0;
        int streamSafe = 0;
        bool crossed = false;
        while (remainingSource > 0)
        {
            byte flags = scratch[
                source.Length - encodedLength +
                remainingStream - 1];
            remainingStream--;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((flags & (0x80 >> bit)) == 0)
                {
                    remainingStream--;
                    remainingSource--;
                }
                else
                {
                    int token = scratch[
                        source.Length - encodedLength +
                        remainingStream - 1];
                    remainingStream--;
                    int length =
                        ((token >> 4) & 0x0F) +
                        MinimumMatchLength;
                    remainingStream--;
                    remainingSource -= length;
                    if (remainingSource < remainingStream)
                    {
                        sourceSafe = remainingSource;
                        streamSafe = remainingStream;
                        crossed = true;
                        break;
                    }
                }

                if (remainingSource <= 0)
                {
                    break;
                }
            }

            if (crossed)
            {
                break;
            }
        }

        int keptStreamLength =
            encodedLength - streamSafe;
        int paddingOffset = sourceSafe + keptStreamLength;
        int footerOffset = Align(paddingOffset, sizeof(uint));
        int compressedLength = footerOffset + sizeof(ulong);
        int regionLength = compressedLength - sourceSafe;
        int headerLength = compressedLength - paddingOffset;
        if (compressedLength >= source.Length ||
            regionLength > 0x00FF_FFFF)
        {
            throw new CtrFormatException(
                "The CodeSet could not be compressed below its original size.");
        }

        byte[] result = new byte[compressedLength];
        source[..sourceSafe].CopyTo(result);
        int streamStart =
            source.Length - encodedLength + streamSafe;
        scratch.AsSpan(
                streamStart,
                keptStreamLength)
            .CopyTo(result.AsSpan(sourceSafe));
        result.AsSpan(
                paddingOffset,
                footerOffset - paddingOffset)
            .Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(
                footerOffset,
                sizeof(uint)),
            checked((uint)(
                regionLength |
                (headerLength << 24))));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(
                footerOffset + sizeof(uint),
                sizeof(uint)),
            checked((uint)(
                source.Length - compressedLength)));
        return result;
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    /// <summary>
    /// Hash-chain match finder over the 4098-byte window that slides
    /// backwards as encoding consumes the tail of the image.
    /// </summary>
    private sealed class MatchWindow
    {
        private readonly short[] _offset = new short[WindowSize];
        private readonly short[] _reversed = new short[WindowSize];
        private readonly short[] _byValue = new short[256];
        private readonly short[] _end = new short[256];
        private ushort _position;
        private ushort _length;

        public MatchWindow()
        {
            Array.Fill(_byValue, (short)-1);
            Array.Fill(_end, (short)-1);
        }

        public int Search(
            ReadOnlySpan<byte> source,
            int cursor,
            out int offset,
            int maximumLength)
        {
            offset = 0;
            if (maximumLength < MinimumMatchLength)
            {
                return 0;
            }

            int bestLength = MinimumMatchLength - 1;
            int? bestOffset = null;
            for (short chain = _end[source[cursor - 1]];
                 chain != -1;
                 chain = _reversed[chain])
            {
                int search = chain < _position
                    ? cursor + _position - chain
                    : cursor + _length + _position - chain;
                if (search - cursor < MinimumMatchLength ||
                    source[search - 2] != source[cursor - 2] ||
                    source[search - 3] != source[cursor - 3])
                {
                    continue;
                }

                int limit = Math.Min(
                    maximumLength,
                    search - cursor);
                int length = MinimumMatchLength;
                while (length < limit &&
                       source[search - length - 1] ==
                       source[cursor - length - 1])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestOffset = search - cursor;
                    if (length == maximumLength)
                    {
                        break;
                    }
                }
            }

            if (bestLength < MinimumMatchLength ||
                bestOffset is null)
            {
                return 0;
            }

            offset = bestOffset.Value;
            return bestLength;
        }

        public void Slide(
            ReadOnlySpan<byte> source,
            int cursor,
            int count)
        {
            for (int index = 0; index < count; index++)
            {
                SlideByte(source, cursor - index);
            }
        }

        private void SlideByte(
            ReadOnlySpan<byte> source,
            int cursor)
        {
            byte value = source[cursor - 1];
            int insert;
            if (_length == WindowSize)
            {
                byte evicted = source[cursor + WindowSize - 1];
                short next = _offset[_byValue[evicted]];
                _byValue[evicted] = next;
                if (next == -1)
                {
                    _end[evicted] = -1;
                }
                else
                {
                    _reversed[next] = -1;
                }

                insert = _position;
            }
            else
            {
                insert = _length;
            }

            short previous = _end[value];
            if (previous == -1)
            {
                _byValue[value] = (short)insert;
            }
            else
            {
                _offset[previous] = (short)insert;
            }

            _end[value] = (short)insert;
            _offset[insert] = -1;
            _reversed[insert] = previous;
            if (_length == WindowSize)
            {
                _position = (ushort)(
                    (_position + 1) % WindowSize);
            }
            else
            {
                _length++;
            }
        }
    }
}
