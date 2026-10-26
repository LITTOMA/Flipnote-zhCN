using System.Buffers.Binary;

namespace Flipnote.Build;

/// <summary>The v7.2.0 application partitions its linear arena into a fixed main heap and a remaining workspace heap.</summary>
internal static class FontMemoryBudget
{
    public const string FontPath = "content/0000/romfs/font_CN.bcfnt";
    public const string CodePath = "content/0000/exefs/.code";
    public const int HeapSizeOffset = 0x2CC;
    public const uint OriginalMainHeapBytes = 0xDF1000;
    public const uint AdditionalBytes = 3 * 1024 * 1024;
    public const uint MainHeapBytes = OriginalMainHeapBytes + AdditionalBytes;
    public const int FontHeaderBytes = 0x80;
    public const int AllocatorMarginBytes = 64 * 1024;

    public static void Validate(CiaImage image)
    {
        if (image.Resources.TryGetValue(FontPath, out var font))
            Validate(font.Data.Length, image.Resources[CodePath].Data);
    }

    public static void Validate(int fontBytes, ReadOnlySpan<byte> code)
    {
        if (code.Length < HeapSizeOffset + 4 || BinaryPrimitives.ReadUInt32LittleEndian(code[HeapSizeOffset..]) != MainHeapBytes)
            throw new InvalidDataException("The Chinese font requires the v7.2.0 main-heap expansion (+3 MiB). The old heap causes a startup allocation failure.");
        if (fontBytes <= 0 || (long)fontBytes + FontHeaderBytes + AllocatorMarginBytes > AdditionalBytes)
            throw new InvalidDataException("Chinese font exceeds its additional runtime heap budget; revise the memory partition and test startup before growing the font.");
    }
}
