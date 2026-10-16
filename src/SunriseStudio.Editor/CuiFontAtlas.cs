using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** Only validated alpha sprites reach ImGui; font outlines stay inside Windows DirectWrite. */
public sealed class CuiFontAtlas : IDisposable
{
    // A fixed 48-pixel em keeps zoomed glyphs readable without an atlas for every style size.
    private const float EmPixels = 48;
    // These preview limits bound allocation before any glyph or atlas buffer is created.
    private const int AtlasWidth = 1024, MaximumHeight = 8192, MaximumGlyphs = 4096;
    private sealed record Glyph(CuiBakedGlyph Bitmap, Vector2 UvMin, Vector2 UvMax,
        uint Font, ushort Index, float LineHeight);
    private readonly Dictionary<char, Glyph> glyphs = [];
    private readonly CuiDirectWrite writer = new();
    private readonly Dictionary<uint, CuiDirectWrite.Face> faces = [];
    private readonly Dictionary<(char Left, char Right), float> kernings = [];
    private readonly Dictionary<(CuiTextAppearance Style, string Text, float Width), CuiTextLayout> layouts = [];
    private GpuTexture? texture;

    /**
     * An isolated loader never installs or exposes project fonts to other applications.
     * @param renderer Device that owns the uploaded atlas.
     * @param appearances Prepared text and font choices for this font set.
     */
    public CuiFontAtlas(Dx11Renderer renderer, IEnumerable<CuiTextAppearance> appearances)
    {
        List<(char Character, CuiBakedGlyph Bitmap, int X, int Y, uint Font, ushort Index, float LineHeight)> packed = [];
        int x = 1, y = 1, rowHeight = 0;
        try
        {
            foreach (var group in appearances.SelectMany(style => style.Fonts).GroupBy(use => use.Face.DataTag)
                .OrderBy(group => group.Min(use => use.Order)))
            {
                var face = group.First().Face;
                char[] characters = group.SelectMany(use => use.Characters).Distinct().Order().ToArray();
                if (characters.Length > MaximumGlyphs - packed.Count) throw new NotSupportedException("Too many native font glyphs for this preview.");
                var native = writer.Open(face.Font.Bytes);
                faces.Add(face.DataTag, native);
                foreach (char character in characters)
                {
                    ushort index = native.Glyph(character);
                    if (index != face.Font.GlyphIndex(character))
                        throw new NotSupportedException("The font's Unicode map differs from DirectWrite.");
                    if (index == 0) continue;
                    var bitmap = native.Bake(index, EmPixels);
                    if (x + bitmap.Width + 1 > AtlasWidth) { x = 1; y += rowHeight + 1; rowHeight = 0; }
                    if (bitmap.Width >= AtlasWidth || y + bitmap.Height + 1 > MaximumHeight)
                        throw new NotSupportedException("Native font atlas exceeds the preview texture budget.");
                    packed.Add((character, bitmap, x, y, face.DataTag, index, native.LineHeight));
                    x += bitmap.Width + 1; rowHeight = Math.Max(rowHeight, bitmap.Height);
                }
            }
            if (packed.Count == 0) throw new InvalidDataException("The native font atlas contains no supported glyphs.");
            int height = Math.Max(1, y + rowHeight + 1);
            byte[] pixels = new byte[checked(AtlasWidth * height * 4)];
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = 255;
            foreach (var glyph in packed)
            {
                var bitmap = glyph.Bitmap;
                for (int row = 0; row < bitmap.Height; row++)
                    for (int column = 0; column < bitmap.Width; column++)
                    {
                        int target = ((glyph.Y + row) * AtlasWidth + glyph.X + column) * 4;
                        pixels[target + 3] = bitmap.Alpha[row * bitmap.Width + column];
                    }
                glyphs.TryAdd(glyph.Character, new(bitmap, new(glyph.X / (float)AtlasWidth, glyph.Y / (float)height),
                    new((glyph.X + bitmap.Width) / (float)AtlasWidth, (glyph.Y + bitmap.Height) / (float)height),
                    glyph.Font, glyph.Index, glyph.LineHeight));
            }
            texture = renderer.CreateTexture(AtlasWidth, height, pixels);
        }
        catch { Dispose(); throw; }
    }

    /** Width of the text on one unwrapped line. */
    public float Measure(CuiTextAppearance appearance, string text) => Layout(appearance, text, float.MaxValue).Size.X;

    /** Layout stays in reference pixels and is shared by drawing and automatic dimensions. */
    public CuiTextLayout Layout(CuiTextAppearance appearance, string text, float width)
    {
        var key = (appearance, text, width);
        if (layouts.TryGetValue(key, out var existing)) return existing;
        float scale = appearance.Size / EmPixels;
        var result = CuiTextLayouter.Create(appearance, text, width, character =>
        {
            bool joiner = character == '\u2060';
            if (!glyphs.TryGetValue(joiner ? ' ' : character, out var value)) return default;
            var bitmap = value.Bitmap;
            if (joiner) return new(0, 0, 0, 0, 0,
                MathF.Round(value.LineHeight * appearance.Size, MidpointRounding.AwayFromZero));
            return new(MathF.Round(bitmap.Advance * scale, MidpointRounding.AwayFromZero), bitmap.Left * scale,
                bitmap.Top * scale, bitmap.Width * scale, bitmap.Height * scale,
                MathF.Round(value.LineHeight * appearance.Size, MidpointRounding.AwayFromZero));
        }, (left, right) => Kerning(left, right, appearance.Size));
        if (layouts.Count >= 64) layouts.Clear();
        layouts.Add(key, result);
        return result;
    }

    /** Native kerning damps sub-25-pixel pairs before rounding their 26.6 result to pixels. */
    private float Kerning(char left, char right, float size)
    {
        if (!glyphs.TryGetValue(left, out var first) || !glyphs.TryGetValue(right, out var second)
            || first.Font != second.Font) return 0;
        if (!kernings.TryGetValue((left, right), out float raw))
        {
            raw = faces[first.Font].Kerning(first.Index, second.Index, EmPixels);
            if (kernings.Count >= 65536) kernings.Clear();
            kernings.Add((left, right), raw);
        }
        int fixedPoint = checked((int)MathF.Round(raw * size / EmPixels * 64, MidpointRounding.AwayFromZero));
        if (size < 25) fixedPoint = Math.Sign(fixedPoint) * (int)((Math.Abs((long)fixedPoint) * size + 12) / 25);
        return (fixedPoint + 32) >> 6;
    }

    /**
     * Positions use the native em size and baseline while the editor owns canvas zoom.
     * @param draw Current canvas draw list.
     * @param origin Widget top-left in screen pixels.
     * @param width Widget width in screen pixels.
     * @param zoom Canvas zoom.
     * @param appearance Verified native style values.
     * @param text The same text snapshot that supplied the glyph set.
     */
    public void Draw(ImDrawListPtr draw, Vector2 origin, float width, float zoom, CuiTextAppearance appearance, string text)
    {
        if (texture is null) return;
        float scale = appearance.Size * zoom / EmPixels;
        foreach (var line in Layout(appearance, text, width / zoom).Lines)
        {
            float measured = line.Width * zoom;
            float x = appearance.Alignment switch
            {
                1 => origin.X + MathF.Floor((width / zoom - line.Width) * .5f + .5f) * zoom,
                2 => origin.X + width - measured,
                _ => origin.X
            };
            for (int i = 0; i < line.Text.Length; i++)
            {
                char character = line.Text[i];
                if (!glyphs.TryGetValue(character, out var glyph)) continue;
                var bitmap = glyph.Bitmap;
                if (bitmap.Width != 0 && bitmap.Height != 0)
                {
                    Vector2 min = new(x + line.Positions[i] * zoom + bitmap.Left * scale,
                        origin.Y + line.Baseline * zoom + bitmap.Top * scale);
                    Vector2 max = min + new Vector2(bitmap.Width, bitmap.Height) * scale;
                    draw.AddImage(texture.ImGuiId, min, max, glyph.UvMin, glyph.UvMax, ImGui.GetColorU32(appearance.Color));
                }
            }
        }
    }

    /** Releases the atlas texture, the font faces and the DirectWrite writer. */
    public void Dispose()
    {
        texture?.Dispose(); texture = null; glyphs.Clear(); layouts.Clear(); kernings.Clear();
        foreach (var face in faces.Values) face.Dispose();
        faces.Clear(); writer.Dispose();
    }
}
