using System.Numerics;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Resolves CUI text styles to font faces and glyph coverage for layout preview. A font face: its header and data tags and the parsed OpenType font. */
public sealed record CuiFontFace(uint HeaderTag, uint DataTag, string Name, CuiOpenTypeFont Font);
/** A font face and the characters it supplies, with its priority Order. */
public sealed record CuiFontGlyphs(CuiFontFace Face, string Characters, int Order);
/** Resolved text style: size, alignment, casing, colour and the fonts to use; Error is set when resolution failed. */
public sealed record CuiTextAppearance(uint SourceTag, uint StyleHash, float Size, int Alignment, int Casing,
    Vector4 Color, IReadOnlyList<CuiFontGlyphs> Fonts, string Error)
{
    public string FontSetKey { get; init; } = "";
    public bool AutoWidth { get; init; }
    public bool AutoHeight { get; init; }
    public float MinimumWidth { get; init; }
    public float MaximumWidth { get; init; } = float.MaxValue;
    public float MinimumHeight { get; init; }
    public float MaximumHeight { get; init; } = float.MaxValue;
    public float LineSpacing { get; init; } = 1;
    public string Typeface => Fonts.FirstOrDefault()?.Face.Name ?? "";
    /** ASCII conversion avoids inventing the game's separate non-ASCII character mappings. */
    public string Format(string text) => Casing == 0 ? text : string.Create(text.Length, (Text: text, Mode: Casing), (span, state) =>
    {
        for (int i = 0; i < span.Length; i++)
        {
            char value = state.Text[i];
            span[i] = state.Mode == 1 && value is >= 'a' and <= 'z' ? (char)(value - 'a' + 'A')
                : state.Mode == 2 && value is >= 'A' and <= 'Z' ? (char)(value - 'A' + 'a') : value;
        }
    });
}

/** Native screen font sets retain filename order and never require installing fonts into Windows. */
internal sealed class CuiFonts
{
    // The native text constructor selects this style when no override is stored.
    public const uint DefaultStyle = 0xDCE0E62D;
    // The native text constructor selects the default foreground palette identity.
    private const uint DefaultColor = 0xBD77BB98;
    private const uint SheetClass = 0x808047C5, ManifestClass = 0x808047C7, HeaderClass = 0x808047CA;
    private sealed record FontRange(string Name, int Minimum, int Maximum);
    private sealed record Style(uint Source, uint Hash, byte Size, FontRange[] Fonts, byte[] Definition, TagReader Reader);
    private sealed record Header(uint Tag, uint Data, string Name, int Length);
    private readonly PackageCatalog catalog;
    private readonly CancellationToken token;
    private readonly Dictionary<uint, List<Style>> styles = [];
    private readonly Dictionary<string, List<Header>> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CuiFontFace> faces = new(StringComparer.OrdinalIgnoreCase);

    public CuiFonts(PackageCatalog catalog, CancellationToken token)
    {
        this.catalog = catalog; this.token = token;
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 8 && entry.Reference == ManifestClass))
        {
            token.ThrowIfCancellationRequested();
            var r = new TagReader(catalog.Read(entry.TagHash));
            var list = r.Array(8, 0x808047C9, 4);
            if (list.Count > 64) throw new InvalidDataException("Embedded font manifest exceeds the native capacity.");
            for (int i = 0; i < list.Count; i++)
            {
                uint tag = r.U32(list.Data + i * 4);
                Require(tag, HeaderClass);
                var h = new TagReader(catalog.Read(tag));
                if (!h.Has(0, 32) || h.U64(24) is 0 or > 16 * 1024 * 1024)
                    throw new InvalidDataException("Embedded font header has an invalid byte count.");
                var header = new Header(tag, h.U32(8), String(h, 16), checked((int)h.U64(24)));
                if (!headers.TryGetValue(header.Name, out var existing)) headers.Add(header.Name, existing = []);
                if (existing.All(candidate => candidate.Tag != tag)) existing.Add(header);
            }
        }
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == 8 && entry.Reference == SheetClass))
        {
            token.ThrowIfCancellationRequested();
            var bytes = catalog.Read(entry.TagHash); var r = new TagReader(bytes);
            var names = r.Array(24, 0x80804767, 8);
            var sets = r.Array(40, 0x8080475F, 40);
            var rows = r.Array(56, 0x80804756, 128);
            if (names.Count > 256 || sets.Count > 256 || rows.Count > 4096)
                throw new InvalidDataException("CUI font table exceeds preview capacity.");
            Dictionary<int, string> filenames = [];
            for (int i = 0; i < names.Count; i++) filenames.Add(names.Data + i * 8, String(r, names.Data + i * 8));
            for (int i = 0; i < rows.Count; i++)
            {
                int at = rows.Data + i * 128, set = r.Relative(at + 8);
                if (set < sets.Data || set >= sets.Data + sets.Count * 40 || (set - sets.Data) % 40 != 0)
                    throw new InvalidDataException("Text style leaves its declared font sets.");
                var refs = r.Array(set + 8, 0x80804762, 8); var ranges = r.Array(set + 24, 0x80804763, 8);
                if (refs.Count is < 1 or > 8 || ranges.Count != refs.Count)
                    throw new InvalidDataException("Text style font ranges disagree with its fonts.");
                var fontRanges = new FontRange[refs.Count];
                for (int font = 0; font < refs.Count; font++)
                {
                    if (!filenames.TryGetValue(r.Relative(refs.Data + font * 8), out string? name))
                        throw new InvalidDataException("Text style references an undeclared font.");
                    fontRanges[font] = new(name, r.I32(ranges.Data + font * 8), r.I32(ranges.Data + font * 8 + 4));
                }
                byte[] definition = bytes.AsSpan(at, 128).ToArray(); definition.AsSpan(8, 8).Clear();
                var style = new Style(entry.TagHash, r.U32(at), r.U8(at + 16), fontRanges, definition, r);
                if (!styles.TryGetValue(style.Hash, out var candidates)) styles.Add(style.Hash, candidates = []);
                candidates.Add(style);
            }
        }
    }

    /** Resolves a style hash, font size and text into a CuiTextAppearance; throws if the text exceeds the 16384-character preview budget. */
    public CuiTextAppearance Resolve(uint styleHash, ushort size, string text, IReadOnlyList<CuiAuthoredProperty> properties)
    {
        if (text.Length > 16384) throw new NotSupportedException("Text exceeds the layout preview budget.");
        if (!styles.TryGetValue(styleHash, out var candidates)) throw new NotSupportedException("Native text style is not available.");
        var style = candidates[0];
        int alignment = checked((int)Integer(0x40E, 0)), casing = checked((int)Integer(0x40A, style.Definition[17]));
        if (alignment is < 0 or > 2 || casing is < 0 or > 2)
            throw new NotSupportedException("Text alignment or case mode is unsupported.");
        if (candidates.Any(other => !other.Definition.AsSpan().SequenceEqual(style.Definition)
            || !other.Fonts.SequenceEqual(style.Fonts)))
            throw new NotSupportedException("Text style differs between screens; a screen context is required.");
        var paletteProperty = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x40D
            && row.Property.ValueClass == 0x80804692);
        uint paletteKey = paletteProperty is null ? DefaultColor : CuiVisualProperties.ReadUInt(paletteProperty.Source, paletteProperty.Property);
        Vector4 color = Palette(style.Reader, paletteKey);
        if (candidates.Any(other => Palette(other.Reader, paletteKey) != color))
            throw new NotSupportedException("Text palette differs between screens; a screen context is required.");
        var tint = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x414
            && row.Property.ValueClass == 0x80804693);
        if (tint is not null)
        {
            var data = new TagReader(tint.Source.Data); int at = tint.Property.ValueOffset;
            color *= new Vector4(Single(data, at), Single(data, at + 4), Single(data, at + 8), Single(data, at + 12));
        }
        if (!float.IsFinite(color.X + color.Y + color.Z + color.W)) throw new InvalidDataException("Text color is not finite.");
        var appearance = new CuiTextAppearance(style.Source, styleHash, size == 0 ? Math.Max(1, (int)style.Size) : size,
            alignment, casing, color, [], "");
        string formatted = appearance.Format(text);
        HashSet<char> missing = formatted.Where(character => !char.IsControl(character) && character != '\u2060').ToHashSet();
        if (formatted.Contains('\n') || formatted.Contains('\u2060')) missing.Add(' ');
        if (missing.Count == 0) missing.UnionWith(" ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
        List<CuiFontGlyphs> used = [];
        for (int order = 0; order < style.Fonts.Length; order++)
        {
            var range = style.Fonts[order];
            if (range.Minimum != 0 || range.Maximum != 0)
                throw new NotSupportedException("Native text font-range overrides are not yet supported.");
            var font = Face(range.Name);
            string characters = new(missing.Where(character => font.Font.GlyphIndex(character) != 0).Order().ToArray());
            if (characters.Length != 0) { used.Add(new(font, characters, order)); missing.ExceptWith(characters); }
            if (missing.Count == 0) break;
        }
        string error = missing.Count == 0 ? "" : "Some characters are unavailable in the native font set.";
        if (casing != 0 && text.Any(character => character > 127)) error = "Native non-ASCII casing is not implemented.";
        var auto = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x41A
            && row.Property.ValueClass == 0x8080468D);
        bool autoWidth = auto is not null && auto.Source.Data.Span[auto.Property.ValueOffset] != 0;
        float minimum = Number(0x401), maximum = Number(0x402);
        var height = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == 0x41B
            && row.Property.ValueClass == 0x8080468D);
        bool autoHeight = height is not null && height.Source.Data.Span[height.Property.ValueOffset] != 0;
        float minimumHeight = Number(0x403), maximumHeight = Number(0x404), spacing = 1 + Number(0x421);
        if (Math.Abs(spacing) > 4096 || appearance.Size > 4096)
            throw new NotSupportedException("Text layout dimensions are unsupported.");
        if (autoWidth && maximum <= 0) error = "Automatic text width has no supported maximum.";
        if (autoHeight && maximumHeight <= 0) error = "Automatic text height has no supported maximum.";
        if (text.Any(character => character >= 0x2E80)) error = "Native CJK line-break rules are not implemented.";
        return appearance with { Fonts = used, Error = error, AutoWidth = autoWidth && maximum > 0,
            MinimumWidth = Math.Max(0, minimum), MaximumWidth = maximum > 0 ? Math.Max(minimum, maximum) : float.MaxValue,
            AutoHeight = autoHeight && maximumHeight > 0, MinimumHeight = Math.Max(0, minimumHeight),
            MaximumHeight = maximumHeight > 0 ? Math.Max(minimumHeight, maximumHeight) : float.MaxValue, LineSpacing = spacing,
            FontSetKey = string.Join('|', style.Fonts.Select(font => font.Name.ToUpperInvariant())) };

        float Number(ulong selector)
        {
            var row = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == selector
                && row.Property.ValueClass == 0x80804694);
            if (row is null) return 0;
            float value = Single(new(row.Source.Data), row.Property.ValueOffset);
            if (row.Property.PoolDescriptor == 200) value = MathF.Round(value * CuiLayout.ReferenceHeight);
            if (!float.IsFinite(value)) throw new InvalidDataException("Text size limit is not finite.");
            return value;
        }

        uint Integer(ulong selector, uint fallback)
        {
            var value = properties.LastOrDefault(row => row.Property.Direct && row.Property.Selector == selector);
            if (value is null) return fallback;
            return value.Property.ValueClass == 0x80804696 ? CuiVisualProperties.ReadUInt(value.Source, value.Property)
                : throw new NotSupportedException("Native text property storage is unsupported.");
        }
    }

    private static Vector4 Palette(TagReader reader, uint key)
    {
        var colors = reader.Array(864, 0x80804770, 32);
        if (colors.Count % 4 != 0) throw new InvalidDataException("Text palettes do not have four equal ranges.");
        for (int i = 0; i < colors.Count / 4; i++)
        {
            int at = colors.Data + i * 32;
            if (reader.U32(at) == key) return new(Single(reader, at + 16), Single(reader, at + 20), Single(reader, at + 24), Single(reader, at + 28));
        }
        return Vector4.One;
    }

    private static float Single(TagReader reader, int at) => BitConverter.UInt32BitsToSingle(reader.U32(at));

    private CuiFontFace Face(string name)
    {
        if (faces.TryGetValue(name, out var existing)) return existing;
        if (!headers.TryGetValue(name, out var candidates)) throw new NotSupportedException($"Embedded font {name} is missing.");
        CuiFontFace? result = null;
        foreach (var header in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!catalog.TryGetEntry(header.Data, out var entry) || entry.Type != 24 || entry.Size != header.Length)
                throw new InvalidDataException("Embedded font payload differs from its header.");
            byte[] bytes = catalog.Read(header.Data);
            if (result is not null && !result.Font.Bytes.Span.SequenceEqual(bytes))
                throw new NotSupportedException($"Embedded font {name} differs between manifests.");
            result ??= new(header.Tag, header.Data, header.Name, new(bytes));
        }
        faces.Add(name, result!); return result!;
    }

    private void Require(uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expected)
            throw new InvalidDataException("CUI font reference has the wrong class.");
    }

    private static string String(TagReader reader, int pointer)
    {
        int at = reader.Relative(pointer), length = 0;
        while (length < 256 && reader.Has(at + length, 1) && reader.U8(at + length) != 0) length++;
        if (length is 0 or 256 || !reader.Has(at + length, 1)) throw new InvalidDataException("Invalid native font filename.");
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = reader.U8(at + i);
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
