using SunriseStudio.Packages;
using SunriseStudio.Formats;

/** Diagnostic tool that lists installed font and ui_global tags and their style entries for manual inspection. */
internal static class CuiFontInspection
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string root)
    {
        using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
        foreach (var tag in catalog.NamedTags.Where(tag => tag.Name.Contains("font", StringComparison.OrdinalIgnoreCase)
            || tag.Name.Contains("ui_global", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"name tag={tag.TagHash:X8} class={tag.ClassHash:X8} value={tag.Name}");
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 24 && entry.PackageName.StartsWith("w64_ui", StringComparison.Ordinal)).Take(30))
        {
            var bytes = catalog.Read(entry.TagHash);
            var font = new CuiOpenTypeFont(bytes);
            Console.WriteLine($"font tag={entry.TagHash:X8} bytes={bytes.Length} em={font.UnitsPerEm} ascent={font.Ascender} descent={font.Descender} glyphW={font.GlyphIndex('W')}");
        }
        byte[] style = BitConverter.GetBytes(0x6DFE676Du);
        foreach (var entry in catalog.Entries.Where(entry => entry.PackageName.StartsWith("w64_ui", StringComparison.Ordinal)
            && entry.Type == 8 && entry.Reference != 0x80804825 && entry.Size <= 16 * 1024 * 1024))
        {
            var bytes = catalog.Read(entry.TagHash);
            for (int at = 0; at <= bytes.Length - 4; at += 4)
                if (bytes.AsSpan(at, 4).SequenceEqual(style))
                    if (entry.Reference == 0x808047C5) Console.WriteLine($"style tag={entry.TagHash:X8} offset={at:X}");
        }
        var preview = CuiPreviewData.Read(catalog, 0x80BC722A, 0x80804825, catalog.Read(0x80BC722A),
            hash => hash == 0xF9C88800 ? "Waiting for Other Players" : null)!;
        var owner = preview.Document!.Components.First(component => component.Key == 0x543);
        var content = preview.LocalContent[owner.Offset];
        if (content.NativeText is not { } native) throw new Exception("Native text failed: " + content.Error);
        if (native.Size != 22 || native.Casing != 1 || native.Alignment != 0 || native.Fonts.Count == 0
            || !native.Typeface.Equals("nhaasgrotesktxpro-55rg.otf", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Waiting text resolved a different native style.");
        float advance = native.Format(content.Text).Sum(character => native.Fonts.First().Face.Font.Advance(character, native.Size));
        Console.WriteLine($"native_text font={native.Typeface} size={native.Size} color={native.Color} case={native.Casing} align={native.Alignment} advance={advance:F3} error={native.Error}");
    }
}
