using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Flipnote.Build;

internal sealed record FontGlyph(int Index, int Left, int Glyph, int Advance, string AlphaSha256);
internal sealed record FontStylePolicy(int SchemaVersion, string Family, string SourceFont, string SourceFontSha256,
    int PixelSize, string Scope, Dictionary<string, FontGlyph> Unified, Dictionary<string, FontGlyph> Preserved, int CommonHanCount = 0);

/// <summary>One face for every Han/English glyph; preserve kana, symbols and other existing glyphs.</summary>
internal static class FontUnification
{
    public static bool IsHan(char c) => c == '\u3007' || c is >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
    public static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\uff21' and <= '\uff3a' or >= '\uff41' and <= '\uff5a';
    public static bool Managed(char c) => IsHan(c) || IsLatin(c);
    public static string PolicyPath(string manifest) => Path.Combine(Path.GetDirectoryName(manifest)!, "glyph-unification.json");
    public static char[] CommonHan()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(936, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback);
        var characters = new HashSet<char>();
        // GB2312 Han rows 16..87; the last five cells in row 55 are unassigned.
        for (int lead = 0xb0; lead <= 0xf7; lead++) for (int trail = 0xa1; trail <= 0xfe; trail++)
        {
            if (lead == 0xd7 && trail >= 0xfa) continue;
            char c = encoding.GetString([(byte)lead, (byte)trail]).Single();
            if (!IsHan(c)) throw new InvalidDataException("Unexpected non-Han character in GB2312 repertoire.");
            characters.Add(c);
        }
        if (characters.Count != 6763) throw new InvalidDataException("Incomplete GB2312 Han repertoire.");
        return characters.Order().ToArray();
    }

    public static object Run(Project project, string fontPath, bool extending = false)
    {
        string manifest = project.Source(project.Manifest.Operations.Single(o => o.Kind == "bcfnt"));
        string policyPath = PolicyPath(manifest), fontHash = Project.Hash(File.ReadAllBytes(fontPath));
        if (extending && File.Exists(policyPath) && Project.Read<FontStylePolicy>(policyPath).SourceFontSha256 != fontHash)
            throw new InvalidDataException("The font differs from the unified face. Use font-unify to intentionally replace the complete Han/English set.");
        if (File.Exists(policyPath)) Validate(manifest);
        var root = JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject();
        var map = root["glyphMap"]!.AsObject(); var widths = root["glyphWidths"]!.AsObject();
        int originalCount = widths.Count;
        using var sheets = new FontSheets(manifest, root);
        if (map.Select(p => p.Value!.GetValue<int>()).Distinct().Count() != map.Count)
            throw new InvalidDataException("Unify requires independent glyph slots; aliased glyphs must be separated first.");
        var preserved = map.Where(p => !Managed(p.Key.Single())).ToDictionary(p => p.Key, p => sheets.Record(p.Key.Single()));
        var requested = map.Select(p => p.Key.Single()).Where(Managed)
            .Concat(CommonHan())
            .Concat(FontCoverage.Required(project).Where(c => !map.ContainsKey(c.ToString())))
            .Concat(Enumerable.Range('A', 26).Concat(Enumerable.Range('a', 26)).Select(c => (char)c))
            .Concat(Enumerable.Range(0x21, 94).Select(c => (char)c).Where(c => !map.ContainsKey(c.ToString())))
            .Distinct().Order().ToArray();
        using var typeface = SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("Cannot open source font.");
        using var paint = new SKPaint { Typeface = typeface, TextSize = 24, Color = SKColors.White, IsAntialias = true, SubpixelText = false };
        foreach (char c in requested)
            if (paint.GetGlyphs(c.ToString()).Single() == 0) throw new InvalidDataException("Source font lacks glyph: " + c);
        foreach (char c in requested)
        {
            string key = c.ToString();
            int index = map[key]?.GetValue<int>() ?? widths.Count;
            var (sheet, x, y) = sheets.Cell(index);
            paint.TextSize = 24; var bounds = new SKRect(); float advance = paint.MeasureText(key, ref bounds);
            if (bounds.Width > sheets.GlyphWidth || bounds.Height > sheets.GlyphHeight)
            {
                paint.TextSize *= Math.Min(sheets.GlyphWidth / bounds.Width, sheets.GlyphHeight / bounds.Height);
                advance = paint.MeasureText(key, ref bounds);
            }
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Save(); canvas.ClipRect(new SKRect(x, y, x + sheets.GlyphWidth, y + sheets.GlyphHeight));
                using (var clear = new SKPaint { BlendMode = SKBlendMode.Clear }) canvas.DrawRect(x, y, sheets.GlyphWidth, sheets.GlyphHeight, clear);
                float baseline = Math.Clamp(sheets.Baseline, -bounds.Top, sheets.GlyphHeight - bounds.Bottom);
                canvas.DrawText(key, x - bounds.Left, y + baseline, paint); canvas.Restore();
            }
            int right = 0;
            for (int gy = 0; gy < sheets.GlyphHeight; gy++) for (int gx = 0; gx < sheets.GlyphWidth; gx++)
            {
                byte alpha = (byte)(sheet.GetPixel(x + gx, y + gy).Alpha / 17 * 17);
                sheet.SetPixel(x + gx, y + gy, new SKColor(255, 255, 255, alpha));
                if (alpha != 0) right = Math.Max(right, gx + 1);
            }
            if (right == 0) throw new InvalidDataException("Rendered glyph is empty: " + c);
            map[key] = index;
            widths[index.ToString()] = new JsonObject { ["left"] = (int)Math.Floor(bounds.Left), ["glyph"] = Math.Max(right, (int)Math.Ceiling(bounds.Width)), ["char"] = (int)Math.Ceiling(advance) };
        }
        foreach (var (character, expected) in preserved)
            if (sheets.Record(character.Single()) != expected) throw new InvalidDataException("Protected glyph changed: " + character);
        var unified = map.Where(p => Managed(p.Key.Single())).ToDictionary(p => p.Key, p => sheets.Record(p.Key.Single()));
        // Newly required non-Han symbols are also frozen after their first render.
        foreach (var entry in map.Where(p => !Managed(p.Key.Single()))) preserved[entry.Key] = sheets.Record(entry.Key.Single());
        root["textureInfo"]!["sheetCount"] = (widths.Count + sheets.Slots - 1) / sheets.Slots;
        var policy = new FontStylePolicy(1, typeface.FamilyName, Path.GetFileName(fontPath), fontHash, 24,
            "GB2312's 6763 Han plus every existing BMP Han ideograph; complete printable ASCII and existing fullwidth English letters. Existing kana, digits, punctuation and icons preserved.", unified, preserved, CommonHan().Length);
        sheets.Save();
        Project.Save(manifest, JsonSerializer.SerializeToUtf8Bytes(root, Project.Json));
        Project.SaveJson(policyPath, policy);
        Validate(manifest);
        return new { family = typeface.FamilyName, han = unified.Keys.Count(k => IsHan(k.Single())), latin = unified.Keys.Count(k => IsLatin(k.Single())),
            preserved = preserved.Count, added = widths.Count - originalCount, glyphs = widths.Count, policy = policyPath };
    }

    public static void Validate(string manifest)
    {
        string path = PolicyPath(manifest); if (!File.Exists(path)) return;
        var policy = Project.Read<FontStylePolicy>(path);
        if (policy.SchemaVersion != 1 || policy.PixelSize != 24) throw new InvalidDataException("Unsupported font style policy.");
        var root = JsonNode.Parse(File.ReadAllBytes(manifest))!.AsObject(); var map = root["glyphMap"]!.AsObject();
        using var sheets = new FontSheets(manifest, root);
        foreach (char c in Enumerable.Range('A', 26).Concat(Enumerable.Range('a', 26)).Select(c => (char)c))
            if (!map.ContainsKey(c.ToString())) throw new InvalidDataException("Unified font lacks English letter: " + c);
        if (policy.CommonHanCount != 0)
        {
            if (policy.CommonHanCount != 6763 || CommonHan().Any(c => !map.ContainsKey(c.ToString())))
                throw new InvalidDataException("Unified font lacks GB2312 Han characters for dynamic author names.");
            if (Enumerable.Range(0x20, 95).Any(c => !map.ContainsKey(((char)c).ToString())))
                throw new InvalidDataException("Unified font lacks printable ASCII for names and IDs.");
        }
        if (map.Count != policy.Unified.Count + policy.Preserved.Count) throw new InvalidDataException("Font style policy does not cover the complete glyph map.");
        foreach (var entry in map)
        {
            var records = Managed(entry.Key.Single()) ? policy.Unified : policy.Preserved;
            if (!records.TryGetValue(entry.Key, out var expected) || sheets.Record(entry.Key.Single()) != expected)
                throw new InvalidDataException("Font glyph differs from its unified/preserved source: " + entry.Key);
        }
    }

    private sealed class FontSheets : IDisposable
    {
        private readonly string stem;
        private readonly JsonObject root;
        private readonly Dictionary<int, SKBitmap> sheets = new();
        private readonly int width, height, columns, oldCount;
        public int GlyphWidth { get; }
        public int GlyphHeight { get; }
        public int Baseline { get; }
        public int Slots { get; }
        public FontSheets(string manifest, JsonObject font)
        {
            root = font; stem = manifest[..^14];
            var texture = root["textureInfo"]!; var info = texture["sheetInfo"]!; var glyph = texture["glyph"]!;
            int Get(JsonNode item, string name) => item[name]!.GetValue<int>();
            width = Get(info, "width"); height = Get(info, "height"); columns = Get(info, "cols"); Slots = columns * Get(info, "rows"); oldCount = Get(texture, "sheetCount");
            GlyphWidth = Get(glyph, "width"); GlyphHeight = Get(glyph, "height"); Baseline = Get(glyph, "baseline");
            if (GlyphWidth != 24 || GlyphHeight != 25 || Baseline != 21 || info["colorFormat"]!.GetValue<string>() != "A4")
                throw new InvalidDataException("Font unification targets the Note 24x25 A4 profile.");
        }
        public (SKBitmap Sheet, int X, int Y) Cell(int index)
        {
            int page = index / Slots, slot = index % Slots;
            if (!sheets.TryGetValue(page, out var sheet))
            {
                sheet = page < oldCount ? SKBitmap.Decode(stem + "_sheet" + page + ".png") : new SKBitmap(width, height);
                if (sheet == null || sheet.Width != width || sheet.Height != height) throw new InvalidDataException("Invalid font sheet.");
                if (page >= oldCount) sheet.Erase(SKColors.Transparent);
                sheets.Add(page, sheet);
            }
            return (sheet, slot % columns * (GlyphWidth + 1) + 1, slot / columns * (GlyphHeight + 1) + 1);
        }
        public FontGlyph Record(char c)
        {
            int index = root["glyphMap"]![c.ToString()]!.GetValue<int>(); var metrics = root["glyphWidths"]![index.ToString()]!;
            var (sheet, x, y) = Cell(index); byte[] alpha = new byte[GlyphWidth * GlyphHeight];
            for (int gy = 0; gy < GlyphHeight; gy++) for (int gx = 0; gx < GlyphWidth; gx++)
                alpha[gy * GlyphWidth + gx] = (byte)(sheet.GetPixel(x + gx, y + gy).Alpha / 17 * 17);
            return new(index, metrics["left"]!.GetValue<int>(), metrics["glyph"]!.GetValue<int>(), metrics["char"]!.GetValue<int>(), Project.Hash(alpha));
        }
        public void Save()
        {
            foreach (var (page, bitmap) in sheets)
            {
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                Project.Save(stem + "_sheet" + page + ".png", png.ToArray());
            }
        }
        public void Dispose() { foreach (var sheet in sheets.Values) sheet.Dispose(); }
    }
}
