using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Diagnostic tool that scans installed UI string banks and prints wrapped-text cases for manual inspection. */
internal static class CuiWrappedInspection
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string root)
    {
        using var catalog = PackageCatalog.Open(root, cacheOptions: new() { Enabled = false });
        var strings = LocalizedStrings.LoadBanks(catalog, catalog.Entries.Where(entry => entry.Reference == 0x80809A88
            && entry.PackageName.StartsWith("w64_ui", StringComparison.Ordinal)).Select(entry => entry.TagHash));
        var types = CuiTypeRegistry.BuiltIn;
        int count = 0;
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == 0x80804825))
        {
            var document = CuiDocument.Read(entry.TagHash, entry.Reference, catalog.Read(entry.TagHash), strings.Get, types);
            var layouts = CuiLayout.Read(document).ToDictionary(layout => layout.Component.Offset);
            foreach (var component in document.Components)
            {
                if (!layouts.TryGetValue(component.Offset, out var layout)) continue;
                var text = component.Properties.LastOrDefault(property => property.Direct && property.Selector == 0x405
                    && property.ValueClass == 0x80804692);
                if (text is null) continue;
                uint key = CuiVisualProperties.ReadUInt(document, text);
                string? value = strings.Get(key);
                if (value is null || value.Length < 70 || value.Length > 800 || layout.Rectangle.Z < 100 || layout.Rectangle.Z > 900) continue;
                var properties = component.Properties.Where(property => property.Direct && property.Selector is >= 0x400 and <= 0x430)
                    .Select(property => $"{property.Selector:X}:{property.ValueClass:X8}:{Convert.ToHexString(document.Data.Span.Slice(property.ValueOffset, Math.Min(4, document.Data.Length - property.ValueOffset)))}");
                Console.WriteLine($"wrapped tag={entry.TagHash:X8} owner={component.Key:X} rect={layout.Rectangle} textkey={key:X8} text={System.Text.Json.JsonSerializer.Serialize(value)} props={string.Join(',', properties)}");
                if (++count == 30) return;
            }
        }
    }
}
