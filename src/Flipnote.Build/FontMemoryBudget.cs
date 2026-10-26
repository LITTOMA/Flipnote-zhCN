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
    // The two packed resource caches have native LRU eviction (0x20C158).
    // Reduce their reservations by exactly the main-heap increment; keep their
    // backing heaps and usable capacities paired (each keeps its original 0x400 gap).
    public const int SmallCacheHeapOffset = 0x18BB50;
    public const uint SmallCacheHeapBytes = 0x1B6000;
    public const int SmallCacheCapacityOffset = 0x18BB58;
    public const int LargeCacheHeapInstructionOffset = 0x18BAC0;
    public const int LargeCacheCapacityOffset = 0x18BB5C;
    public const uint LargeCacheHeapBytes = 0x6D0000;
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
        RequireWord(code, 0x2AC, 0xE2600301, "The application must retain its original 64 MiB allocation limit.");
        RequireWord(code, SmallCacheHeapOffset, SmallCacheHeapBytes, "Small resource-cache heap does not match the balanced 64 MiB budget.");
        RequireWord(code, SmallCacheCapacityOffset, SmallCacheHeapBytes - 0x400, "Small resource-cache buffer exceeds or disagrees with its backing heap.");
        RequireWord(code, LargeCacheHeapInstructionOffset, 0xE3A0086D, "Large resource-cache heap does not match the balanced 64 MiB budget.");
        RequireWord(code, LargeCacheCapacityOffset, LargeCacheHeapBytes - 0x400, "Large resource-cache buffer exceeds or disagrees with its backing heap.");
        if (fontBytes <= 0 || (long)fontBytes + FontHeaderBytes + AllocatorMarginBytes > AdditionalBytes)
            throw new InvalidDataException("Chinese font exceeds its additional runtime heap budget; revise the memory partition and test startup before growing the font.");
    }
    private static void RequireWord(ReadOnlySpan<byte> code, int offset, uint value, string message)
    {
        if (code.Length < offset + 4 || BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]) != value)
            throw new InvalidDataException(message);
    }
}
