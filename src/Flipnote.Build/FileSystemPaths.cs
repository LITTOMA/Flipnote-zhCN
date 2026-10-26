namespace Flipnote.Build;

internal static class FileSystemPaths
{
    // Be conservative on macOS, where the default filesystem ignores case.
    // Virtual CIA resource names always use ordinal comparison independently.
    public static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
