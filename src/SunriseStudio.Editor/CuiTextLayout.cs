using System.Numerics;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** One laid-out line: its text, width, baseline and the x position of each character. */
public sealed record CuiTextLine(string Text, float Width, float Baseline, IReadOnlyList<float> Positions);
/** Result of laying out a string: lines, overall size and whether any line was wrapped. */
public sealed record CuiTextLayout(IReadOnlyList<CuiTextLine> Lines, Vector2 Size, bool Wrapped);
/** Per-glyph metrics the layouter consumes. */
internal readonly record struct CuiTextGlyph(float Advance, float Left, float Top, float Width, float Height, float LineHeight);

/** The native Latin path breaks after empty glyphs and hyphens, then removes trailing empty glyphs. */
internal static class CuiTextLayouter
{
    // Native UI text is bounded before line and glyph-position arrays are allocated.
    public const int MaximumCharacters = 16384;

    /** Lays the text out at the given width, wrapping where the native path would. */
    public static CuiTextLayout Create(CuiTextAppearance style, string source, float width,
        Func<char, CuiTextGlyph> glyph, Func<char, char, float> kerning)
    {
        if (source.Length > MaximumCharacters) throw new NotSupportedException("Text exceeds the layout preview budget.");
        if (!float.IsFinite(width) || width < 0 || !float.IsFinite(style.Size) || style.Size is <= 0 or > 4096
            || !float.IsFinite(style.LineSpacing) || Math.Abs(style.LineSpacing) > 4096)
            throw new InvalidDataException("Text layout dimensions are unsupported.");
        string text = style.Format(source).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        float limit = style.AutoWidth ? style.MaximumWidth : width;
        List<CuiTextLine> lines = [];
        int start = 0;
        float baseline = style.Size, widest = 0, bottom = 0;
        bool wrapped = false, usedBreak = false;
        while (start < text.Length)
        {
            int end = start, lastBreak = -1;
            float x = 0;
            char previous = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char character = text[i];
                if (character == '\n') { end = i + 1; break; }
                var metric = glyph(character);
                float kern = previous == '\0' || character == '\u2060' ? 0 : kerning(previous, character);
                if (i > start && metric.Width != 0 && metric.Height != 0 && x + kern + metric.Left + metric.Width > limit)
                {
                    end = lastBreak < start ? i : lastBreak + 1;
                    wrapped = true;
                    usedBreak |= lastBreak >= start;
                    break;
                }
                x += kern + metric.Advance;
                if (character != '\u2060') previous = character;
                end = i + 1;
                if ((metric.Width == 0 && metric.Height == 0 && character is not '\u00A0' and not '\u202F' and not '\u2060')
                    || character is '-' or '\u00AD') lastBreak = i;
            }
            int visibleEnd = end;
            while (visibleEnd > start)
            {
                var metric = glyph(text[visibleEnd - 1] == '\n' ? ' ' : text[visibleEnd - 1]);
                if (metric.Width != 0 && metric.Height != 0) break;
                visibleEnd--;
            }
            string line = text[start..visibleEnd];
            float[] positions = new float[line.Length];
            float right = 0, lineHeight = 0;
            x = 0;
            previous = '\0';
            for (int i = start; i < end; i++)
            {
                char character = text[i] == '\n' ? ' ' : text[i];
                var metric = glyph(character);
                lineHeight = Math.Max(lineHeight, metric.LineHeight);
                if (i < visibleEnd)
                {
                    x += previous == '\0' || character == '\u2060' ? 0 : kerning(previous, character);
                    positions[i - start] = x;
                    right = Math.Max(right, x + metric.Left + metric.Width);
                    bottom = Math.Max(bottom, baseline + metric.Top + metric.Height);
                    x += metric.Advance;
                }
                if (character != '\u2060') previous = character;
            }
            if (line.Length == 0) bottom = Math.Max(bottom, baseline);
            widest = Math.Max(widest, right);
            lines.Add(new(line, right, baseline, positions));
            baseline += Math.Max(1, lineHeight + style.LineSpacing);
            start = end;
        }
        if (usedBreak) widest = limit;
        float measuredWidth = style.AutoWidth ? Math.Clamp(widest, style.MinimumWidth, style.MaximumWidth) : widest;
        float measuredHeight = style.AutoHeight ? Math.Clamp(bottom, style.MinimumHeight, style.MaximumHeight) : bottom;
        return new(lines, new(measuredWidth, measuredHeight), wrapped);
    }
}
