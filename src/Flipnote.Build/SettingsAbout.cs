using System.Buffers.Binary;
using System.Text;

namespace Flipnote.Build;

internal static class SettingsAbout
{
    public static string TextPath(Project project) => Path.Combine(project.Root, "overlays", "zh-CN", "settings", "texts.json");
    public static Dictionary<string,string> Texts(Project project) => Project.Read<Dictionary<string,string>>(TextPath(project));
    public static void Validate(Project project, CiaImage image)
    {
        string directory = Path.GetDirectoryName(TextPath(project))!;
        var sources = Project.Read<Dictionary<string,string>>(Path.Combine(directory,"source-hashes.json"));
        string[] sourceNames = ["about.c", "hooks.S", "link.ld", "author.py"];
        if (!sources.Keys.Order().SequenceEqual(sourceNames.Order())) throw new InvalidDataException("Unexpected Settings source manifest.");
        foreach (string file in sourceNames)
            if (Project.Hash(File.ReadAllBytes(Path.Combine(project.Root,"src","SettingsAbout",file))) != sources[file])
                throw new InvalidDataException("Settings source changed without regenerating its patches: " + file);
        byte[] code = image.Resources[FontMemoryBudget.CodePath].Data;
        var text = Texts(project);
        string[] keys = ["title", "projectTab", "thirdPartyTab", "closeLabel", "projectInfo", "thirdPartyInfo"];
        if (!text.Keys.Order().SequenceEqual(keys.Order())) throw new InvalidDataException("Unexpected Settings text keys.");
        foreach (string key in keys)
        {
            int capacity = key.EndsWith("Info", StringComparison.Ordinal) ? 400 : 20;
            if (string.IsNullOrWhiteSpace(text[key]) || text[key].Length > capacity || text[key].Contains('\0') ||
                code.AsSpan(0x273300, 0xd00).IndexOf(Encoding.Unicode.GetBytes(text[key]+"\0")) < 0)
                throw new InvalidDataException("Settings text is invalid or its native patch is stale: " + key + ". Regenerate with src/SettingsAbout/author.py.");
        }
        byte[] ex = image.Resources["content/0000/exheader"].Data;
        uint textSize = BinaryPrimitives.ReadUInt32LittleEndian(ex.AsSpan(24));
        if (textSize <= 0x273300 || textSize > 0x274000)
            throw new InvalidDataException("Settings hook must fit the existing text page.");
    }
}
