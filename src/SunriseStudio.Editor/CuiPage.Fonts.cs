using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using System.Numerics;

namespace SunriseStudio.Editor;

/** CuiPage part: builds the preview's font atlases and reports fonts that fail to load. */
public sealed partial class CuiPage
{
    private readonly Dictionary<string, CuiFontAtlas> fontAtlases = [];
    private readonly Dictionary<string, string> fontErrors = [];

    private void PrepareFonts(Dx11Renderer renderer)
    {
        if (preview is null) return;
        var appearances = preview.LocalContent.Values.Concat(preview.ComposedContent.Values)
            .Select(content => content.NativeText).OfType<CuiTextAppearance>().Where(style => style.Fonts.Count != 0);
        foreach (var group in appearances.GroupBy(style => style.FontSetKey))
            try { fontAtlases.Add(group.Key, new(renderer, group)); }
            catch (Exception error) when (error is not OutOfMemoryException) { fontErrors[group.Key] = error.Message; }
    }

    private void DisposeFonts()
    {
        foreach (var atlas in fontAtlases.Values) atlas.Dispose();
        fontAtlases.Clear(); fontErrors.Clear();
    }

    /** The stored rectangle, with an auto-sized side taken from the native text layout. */
    private Vector4 TextRectangle(Vector4 stored, CuiWidgetContent? content)
    {
        if (content?.NativeText is { } style && (style.AutoWidth || style.AutoHeight)
            && fontAtlases.TryGetValue(style.FontSetKey, out var atlas))
        {
            var layout = atlas.Layout(style, content.Text, stored.Z);
            return stored with { Z = style.AutoWidth ? layout.Size.X : stored.Z, W = style.AutoHeight ? layout.Size.Y : stored.W };
        }
        return stored;
    }
}
