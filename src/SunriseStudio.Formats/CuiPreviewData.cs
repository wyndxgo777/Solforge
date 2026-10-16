using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Text and bitmap content of one widget in the preview. */
public sealed record CuiWidgetContent(uint TextHash = 0, string Text = "", uint BitmapTag = 0,
    uint TextureTag = 0, string Error = "", int Group = 0, int Frame = 0, uint StyleHash = 0, ushort FontSize = 0)
{
    public IReadOnlyList<IReadOnlyList<uint>> BitmapFrames { get; init; } = [];
    public CuiTextAppearance? NativeText { get; init; }
}

/** Package reads and bitmap decoding finish before the UI uploads or borrows texture views. */
public sealed partial class CuiPreviewData
{
    public CuiDocument? Document { get; private init; }
    public CuiComposition? Composition { get; private init; }
    public IReadOnlyList<CuiLayoutRectangle> Layouts { get; private init; } = [];
    public IReadOnlyDictionary<int, CuiWidgetContent> LocalContent { get; private init; } = new Dictionary<int, CuiWidgetContent>();
    public IReadOnlyDictionary<int, CuiWidgetContent> ComposedContent { get; private init; } = new Dictionary<int, CuiWidgetContent>();
    public IReadOnlyDictionary<uint, TextureData> Textures { get; private init; } = new Dictionary<uint, TextureData>();
    public IReadOnlyList<string> Diagnostics { get; private init; } = [];

    /** Builds preview data (text, bitmaps, rectangles) for a CUI tag; returns null for classes CUI does not handle. */
    public static CuiPreviewData? Read(PackageCatalog catalog, uint tag, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash = null, CancellationToken cancellationToken = default) =>
        Read(catalog, tag, classHash, bytes, resolveHash, cancellationToken, null);

    /** As above, with a callback that reads configured tags; returns null for classes CUI does not handle. */
    public static CuiPreviewData? Read(PackageCatalog catalog, uint tag, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash, CancellationToken cancellationToken, Func<uint, byte[]>? readConfigured)
    {
        if (!CuiDocument.Supports(classHash)) return null;
        List<string> diagnostics = [];
        Dictionary<uint, TextureData> textures = [];
        Dictionary<int, CuiWidgetContent> local = [], composed = [];
        Dictionary<uint, IReadOnlyList<IReadOnlyList<uint>>> bitmapFrames = [];
        CuiFonts? fonts = null;
        string? fontSetupError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var types = CuiTypeRegistry.BuiltIn;
            var document = CuiDocument.Read(tag, classHash, bytes, resolveHash, types);
            var layouts = CuiLayout.Read(document);
            CuiComposition? composition = null;
            if (classHash == 0x8080496A)
            {
                composition = CuiComposition.Read(document, child =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!catalog.TryGetEntry(child, out var entry)) throw new InvalidDataException("CUI resource is missing.");
                    return CuiDocument.Read(child, entry.Reference, readConfigured?.Invoke(child) ?? catalog.Read(child), resolveHash, types);
                });
                foreach (var node in composition.Nodes)
                    composed[node.Id] = Content(node.Definition, node.Properties);
            }
            else
            {
                var nodes = document.Nodes.GroupBy(node => node.Id).ToDictionary(group => (ulong)group.Key, group => group.First());
                foreach (var component in document.Components)
                    if (nodes.TryGetValue(component.Key, out var node))
                        local[component.Offset] = Content(node, component.Properties.Select(property => new CuiAuthoredProperty(document, property)));
            }
            return new() { Document = document, Composition = composition, Layouts = layouts,
                LocalContent = local, ComposedContent = composed, Textures = textures, Diagnostics = diagnostics };
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        { return new() { Diagnostics = [error.Message] }; }

        CuiWidgetContent Content(CuiNode node, IEnumerable<CuiAuthoredProperty> properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = properties.Where(property => property.Property.Direct).ToArray();
            if (node.TypeHash == 0x0CE30D0B && node.AuxiliaryTypeHash == 0)
            {
                var property = rows.LastOrDefault(property => property.Property.Selector == 0x405
                    && property.Property.ValueClass == 0x80804692);
                uint hash = property is null ? 0 : CuiVisualProperties.ReadUInt(property.Source, property.Property);
                var style = rows.LastOrDefault(property => property.Property.Selector == 0x40C
                    && property.Property.ValueClass == 0x80804692);
                var size = rows.LastOrDefault(property => property.Property.Selector == 0x420
                    && property.Property.ValueClass is 0x8080468B or 0x80804696);
                uint fontSize = size is null ? 0 : CuiVisualProperties.ReadUInt(size.Source, size.Property);
                var content = new CuiWidgetContent(hash, resolveHash?.Invoke(hash) ?? "", StyleHash: style is null ? CuiFonts.DefaultStyle
                    : CuiVisualProperties.ReadUInt(style.Source, style.Property), FontSize: (ushort)fontSize);
                try
                {
                    if (fontSetupError is not null) return content with { Error = fontSetupError };
                    if (fonts is null)
                        try { fonts = new(catalog, cancellationToken); }
                        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
                        { fontSetupError = error.Message; throw; }
                    var appearance = fonts.Resolve(content.StyleHash, content.FontSize, content.Text, rows);
                    return content with { NativeText = appearance, Error = appearance.Error };
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException or OverflowException or ArgumentException)
                { return content with { Error = error.Message }; }
            }
            if (node.TypeHash != 0x7D1BD221 || node.AuxiliaryTypeHash != 0) return new();
            var bitmap = rows.LastOrDefault(property => property.Property.Selector == 0x600
                && property.Property.ValueClass == 0x80804690);
            if (bitmap is null) return new();
            uint bitmapTag = BinaryPrimitives.ReadUInt32LittleEndian(bitmap.Source.Data.Span[bitmap.Property.ValueOffset..]);
            int group = 0, frame = 0;
            IReadOnlyList<IReadOnlyList<uint>> groups = [];
            try
            {
                if (!catalog.TryGetEntry(bitmapTag, out var entry) || entry.Reference != 0x80804A69)
                    throw new NotSupportedException("Bitmap source type is unsupported.");
                if (!bitmapFrames.TryGetValue(bitmapTag, out var known))
                { known = CuiBitmap.ReadGroups(catalog.Read(bitmapTag)); bitmapFrames.Add(bitmapTag, known); }
                groups = known;
                group = Integer(0x601); frame = Integer(0x602);
                if ((uint)group >= groups.Count || (uint)frame >= groups[group].Count)
                    throw new InvalidDataException("The stored bitmap group or frame is outside its source.");
                uint textureTag = groups[group][frame];
                if (!textures.ContainsKey(textureTag)) textures.Add(textureTag, new TextureDataReader(catalog).Read(textureTag));
                return new(BitmapTag: bitmapTag, TextureTag: textureTag, Group: group, Frame: frame) { BitmapFrames = groups };
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            { return new(BitmapTag: bitmapTag, Error: error.Message, Group: group, Frame: frame) { BitmapFrames = groups }; }

            int Integer(ulong selector)
            {
                var row = rows.LastOrDefault(property => property.Property.Selector == selector);
                if (row is null) return 0;
                if (row.Property.ValueClass != 0x80804696)
                    throw new NotSupportedException("The bitmap index storage is unsupported.");
                return unchecked((int)CuiVisualProperties.ReadUInt(row.Source, row.Property));
            }
        }
    }
}

/** Bitmap groups contain frame arrays; the selected frame stores the native texture tag. */
public static class CuiBitmap
{
    /** Texture header tag of one frame of a bitmap source; throws if the group or frame is out of range. */
    public static uint ReadTexture(ReadOnlyMemory<byte> bytes, int group = 0, int frame = 0)
    {
        var groups = ReadGroups(bytes);
        if ((uint)group >= groups.Count) throw new InvalidDataException("Bitmap group is outside the source.");
        if ((uint)frame >= groups[group].Count) throw new InvalidDataException("Bitmap frame is outside the source.");
        return groups[group][frame];
    }

    /** Reads all frame groups of a CUI bitmap source; each frame is a texture header tag. */
    public static IReadOnlyList<IReadOnlyList<uint>> ReadGroups(ReadOnlyMemory<byte> bytes)
    {
        var reader = new TagReader(bytes);
        if (!reader.Has(0, 24)) throw new InvalidDataException("Truncated CUI bitmap.");
        int descriptor = reader.Relative(16);
        // Two class sets share one layout: each frame is a texture header tag.
        (uint Group, uint Frame) classes = descriptor < 4 ? default : reader.U32(descriptor - 4) switch
        {
            0x80804A67 => (0x80804A6C, 0x80804A6F),
            0x80804A65 => (0x80804A6A, 0x80804A6E),
            _ => default
        };
        if (classes.Group == 0) throw new InvalidDataException("Invalid CUI bitmap group descriptor.");
        var groups = reader.Array(descriptor, classes.Group, 16);
        List<IReadOnlyList<uint>> result = [];
        for (int group = 0; group < groups.Count; group++)
        {
            var frames = reader.Array(groups.Data + group * 16, classes.Frame, 4);
            result.Add(Enumerable.Range(0, frames.Count).Select(frame => reader.U32(frames.Data + frame * 4)).ToArray());
        }
        return result;
    }
}
