using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks CUI visual edits (text, style, size, RGBA tint): channel order is kept and a no-op leaves native bytes unchanged. */
internal static class CuiVisualChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        const uint first = 0x80A00000;
        byte[] bytes = Resource(first + 1);
        var source = CuiDocument.Read(first, 0x80804825, bytes);
        var components = source.Components.ToArray();
        var text = CuiVisualProperties.Read(source, source.Nodes[0], components[0]);
        Check(text.Count == 3 && text.Single(field => field.Kind == CuiVisualPropertyKind.FontSize).Value == 24, "Text fields missing.");
        var textEdit = CuiVisualProperties.Apply(bytes, source, source.Nodes[0], components[0], new Dictionary<CuiVisualPropertyKind, uint>
            { [CuiVisualPropertyKind.Text] = 77, [CuiVisualPropertyKind.Style] = 88, [CuiVisualPropertyKind.FontSize] = 30 });
        Verify(source, textEdit, components[0].Properties.Where(property => property.Selector is 0x405 or 0x40C or 0x420).ToArray());
        var changed = CuiDocument.Read(first, 0x80804825, textEdit);
        var changedFields = CuiVisualProperties.Read(changed, changed.Nodes[0], changed.Components.First());
        Check(changedFields.Select(field => field.Value).SequenceEqual(new uint[] { 77, 88, 30 }), "Text/style/size edits differ.");
        var tint = CuiVisualProperties.ReadTint(source, source.Nodes[0], components[0])!;
        var recolored = CuiVisualProperties.Apply(bytes, source, source.Nodes[0], components[0],
            new Dictionary<CuiVisualPropertyKind, uint>(), new(.2f, .4f, .6f, .8f));
        Verify(source, recolored, [tint.Property]);
        var recoloredDocument = CuiDocument.Read(first, 0x80804825, recolored);
        Check(CuiVisualProperties.ReadTint(recoloredDocument, recoloredDocument.Nodes[0], recoloredDocument.Components.First())!.Value
            == new System.Numerics.Vector4(.2f, .4f, .6f, .8f), "RGBA tint edit changed channel order.");
        Check(CuiVisualProperties.Apply(bytes, source, source.Nodes[0], components[0], text.ToDictionary(field => field.Kind, field => field.Value))
            .SequenceEqual(bytes), "Visual no-op changed native bytes.");
        Reject(() => CuiVisualProperties.Apply(bytes, source, source.Nodes[0], components[0], new Dictionary<CuiVisualPropertyKind, uint>
            { [CuiVisualPropertyKind.Group] = 1 }));
        Reject(() => CuiVisualProperties.Apply(bytes, changed, changed.Nodes[0], changed.Components.First(), new Dictionary<CuiVisualPropertyKind, uint>()));
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cui-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, "packages"));
        try
        {
            List<AuthoredPackageEntry> entries = [new(0x80804825, 8 << 9, bytes), new(0x80804A69, 8 << 9, Bitmap(first + 2))];
            for (uint i = 0; i < 5; i++)
            {
                var header = new byte[40]; U(header, 0, 4); U(header, 4, 28); S(header, 12, 0xCAFE);
                foreach (int offset in new[] { 14, 16, 18, 20 }) S(header, offset, 1);
                header[23] = 1; U(header, 36, uint.MaxValue);
                entries.Add(new(first + 7 + i, (32 << 9) | (1 << 6), header));
            }
            for (byte i = 0; i < 5; i++) entries.Add(new(uint.MaxValue, (40 << 9) | (1 << 6), [(byte)(i * 40), 10, 20, 255]));
            var one = Strings(first + 13, "Alpha local text");
            var two = Strings(first + 15, "Beta local text");
            entries.Add(new(0x80809A88, 8 << 9, one.Container)); entries.Add(new(0x80809A8A, 8 << 9, one.Data));
            entries.Add(new(0x80809A88, 8 << 9, two.Container)); entries.Add(new(0x80809A8A, 8 << 9, two.Data));
            PackageWriter.WriteNew(Path.Combine(temporary, "packages", "w64_cui_0100_0.pkg"), 0x100, entries);
            using var catalog = PackageCatalog.Open(temporary, cacheOptions: new() { Enabled = false });
            var localized = LocalizedStrings.Load(catalog);
            Check(localized.Search("local").Count == 0 && localized.Search("alpha", containerTag: first + 12).Single().Text == "Alpha local text"
                && localized.Search("local", containerTag: first + 14).Single().Text == "Beta local text", "Text search lost bank-local distinctions.");
            var replacement = Strings(first + 13, "Project alpha");
            const uint addedBank = first + 0x2000;
            var added = Strings(addedBank + 1, "Added gamma");
            catalog.SetOverlayEntries([
                new(new(addedBank, 0x80809A88, 8, 0, added.Container.Length, "w64_added_0101_0", 0x101, 0) { TypeInfo = 8 << 9 }),
                new(new(addedBank + 1, 0x80809A8A, 8, 0, added.Data.Length, "w64_added_0101_0", 0x101, 1) { TypeInfo = 8 << 9 })]);
            catalog.ReadOverride = tag => tag == first + 13 ? replacement.Data
                : tag == addedBank ? added.Container : tag == addedBank + 1 ? added.Data : null;
            var selectedBanks = LocalizedStrings.LoadBanks(catalog, [first + 12, addedBank, addedBank, 0, uint.MaxValue]);
            Check(selectedBanks.ContainerCount == 2 && selectedBanks.Get(first + 12, 10) == "Project alpha"
                && selectedBanks.Get(addedBank, 10) == "Added gamma" && selectedBanks.Get(10) is null,
                "Selected banks missed an overlay or combined different bank meanings.");
            Check(localized.Get(first + 12, 10) == "Alpha local text", "Fresh selected-bank read mutated an earlier snapshot.");
            catalog.SetOverlayEntries([]); catalog.ReadOverride = null;
            var restoredBanks = LocalizedStrings.LoadBanks(catalog, [first + 12, addedBank]);
            Check(restoredBanks.Get(first + 12, 10) == "Alpha local text" && !restoredBanks.IsContainer(addedBank)
                && restoredBanks.FailedContainerCount == 1, "Closing the overlay retained added or replaced strings.");
            var preview = CuiPreviewData.Read(catalog, first, 0x80804825, bytes, key => key == 10 ? "Fixture text" : null)!;
            var bitmap = preview.LocalContent[components[2].Offset];
            Check(bitmap.Group == 1 && bitmap.Frame == 1 && bitmap.TextureTag == first + 5
                && preview.Textures[bitmap.TextureTag].Subresources[0].Data[0] == 120, "Authored nonzero group/frame rendered the wrong pixels.");
            var edit = CuiVisualProperties.Apply(bytes, source, source.Nodes[2], components[2], new Dictionary<CuiVisualPropertyKind, uint>
                { [CuiVisualPropertyKind.Group] = 0, [CuiVisualPropertyKind.Frame] = 1 });
            Verify(source, edit, [components[2].Properties.Single(property => property.Selector == 0x601)]);
            var editedPreview = CuiPreviewData.Read(catalog, first, 0x80804825, edit)!;
            var updated = editedPreview.LocalContent[components[2].Offset];
            Check(updated.Group == 0 && updated.Frame == 1 && updated.TextureTag == first + 3
                && editedPreview.Textures[updated.TextureTag].Subresources[0].Data[0] == 40, "Edited frame did not decode its own texture.");
            Check(editedPreview.LocalContent[components[3].Offset].TextureTag == bitmap.TextureTag, "Group edit changed an aliased sibling.");
            var bad = Bitmap(first + 2); Q(bad, 16, long.MaxValue); Reject(() => CuiBitmap.ReadGroups(bad));
            Reject(() => CuiBitmap.ReadTexture(Bitmap(first + 2), 1, 3));
        }
        finally { Directory.Delete(temporary, true); }
        Console.WriteLine("cui visual=pass text_style_size=isolated bitmap_group_frame=nonzero decoded_pixels=matched");
    }

    private static (byte[] Container, byte[] Data) Strings(uint dataTag, string text)
    {
        var container = new byte[80]; U(container, 24, dataTag); A(container, 8, 48, 1, 0x80800070); U(container, 64, 10);
        var data = new byte[288]; A(data, 8, 112, 1, 0x80809A90); A(data, 72, 176, 1, 0x80809A8E);
        Q(data, 136, 224 - 136); U(data, 144, 0x811C9DC5); S(data, 148, (ushort)Encoding.UTF8.GetByteCount(text));
        Q(data, 192, 128 - 192); Q(data, 200, 1); Encoding.UTF8.GetBytes(text).CopyTo(data, 224);
        return (container, data);
    }

    /** Runs the checks that need an installed game; takes the installed data under test. */
    public static bool Installed(PackageCatalog catalog, CuiDocument document)
    {
        foreach (var component in document.Components)
        {
            var node = document.Nodes.FirstOrDefault(node => (ulong)node.Id == component.Key);
            if (node is null || node.TypeHash != CuiTypeRegistry.BitmapType || node.AuxiliaryTypeHash != 0) continue;
            var fields = CuiVisualProperties.Read(document, node, component);
            if (!fields.Any(field => field.Kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame && field.Value > 0)) continue;
            var preview = CuiPreviewData.Read(catalog, document.TagHash, document.ClassHash, document.Data)!;
            if (preview.LocalContent.GetValueOrDefault(component.Offset) is not { TextureTag: not 0, Error.Length: 0 } content) continue;
            var values = fields.Where(field => field.Kind is CuiVisualPropertyKind.Group or CuiVisualPropertyKind.Frame)
                .ToDictionary(field => field.Kind, _ => 0u);
            byte[] edited = CuiVisualProperties.Apply(document.Data.Span, document, node, component, values);
            var changes = fields.Where(field => values.ContainsKey(field.Kind) && field.Value != 0).Select(field => field.Property).ToArray();
            Verify(document, edited, changes);
            var result = CuiPreviewData.Read(catalog, document.TagHash, document.ClassHash, edited)!.LocalContent[component.Offset];
            Check(result.Group == 0 && result.Frame == 0 && result.TextureTag == content.BitmapFrames[0][0], "Installed bitmap edit did not select its first frame.");
            Console.WriteLine($"cui bitmap_fixture={document.TagHash:X8} component={component.Key:X} group={content.Group} frame={content.Frame} texture={content.TextureTag:X8} edited_texture={result.TextureTag:X8}");
            return true;
        }
        return false;
    }

    private static void Verify(CuiDocument before, byte[] bytes, IReadOnlyList<CuiProperty> changed)
    {
        var after = CuiDocument.Read(before.TagHash, before.ClassHash, bytes);
        var old = before.Properties.ToDictionary(property => property.RecordOffset);
        foreach (var property in after.Properties.Where(property => changed.All(change => change.RecordOffset != property.RecordOffset)))
            Check(property.Fields.Select(field => field.Value).SequenceEqual(old[property.RecordOffset].Fields.Select(field => field.Value)),
                "A visual edit changed an unrelated property.");
        var delta = BinaryDelta.Create(before.Data.Span, bytes);
        Check(delta.Apply(before.Data.Span).SequenceEqual(bytes) && delta.Operations.All(operation => operation.Kind != DeltaKind.Add),
            "Visual edits copied package data into an added delta range.");
    }

    private static byte[] Resource(uint bitmap)
    {
        var b = new byte[4096]; int cursor = 344;
        int Allocate(int count) { int p = cursor; cursor += count; return p; }
        int Array(int field, int count, int stride, uint type)
        {
            int header = (cursor + 11) & ~7; cursor = header + 16 + count * stride;
            A(b, field, header, count, type); return header + 16;
        }
        int empty = Allocate(16);
        int names = Array(24, 4, 4, 0x80804618);
        int nodes = Array(56, 4, 16, 0x80804622);
        for (int i = 0; i < 4; i++)
        { U(b, names + i * 4, (uint)i + 1); U(b, nodes + i * 16, i < 2 ? (uint)CuiTypeRegistry.TextType : (uint)CuiTypeRegistry.BitmapType); S(b, nodes + i * 16 + 10, (ushort)(i + 1)); }
        int objects = Array(72, 1, 64, 0x8080462A);
        int components = Array(objects + 32, 4, 24, 0x808046D4);
        int floats = Array(184, 3, 4, 0x80804694); F(b, floats + 4, 100); F(b, floats + 8, 50);
        int strings = Array(232, 2, 4, 0x80804692); U(b, strings, 10); U(b, strings + 4, 20);
        int integers = Array(152, 1, 4, 0x80804696); U(b, integers, 1);
        int shorts = Array(280, 1, 2, 0x8080468B); S(b, shorts, 24);
        int hashes = Array(264, 1, 4, 0x80804690); U(b, hashes, bitmap);
        int tint = Array(216, 1, 16, 0x80804693); F(b, tint, 1); F(b, tint + 4, .25f); F(b, tint + 8, .125f); F(b, tint + 12, .75f);
        for (int i = 0; i < 4; i++)
        {
            int component = components + i * 24; Q(b, component, i + 1);
            int properties = Array(component + 8, i < 2 ? 8 : 7, 24, 0x80804858);
            ulong[] selectors = i < 2 ? [0x206, 0x207, 0x208, 0x209, 0x405, 0x40C, 0x420, 0x414]
                : [0x206, 0x207, 0x208, 0x209, 0x600, 0x601, 0x602];
            int[] values = i < 2 ? [floats, floats, floats + 4, floats + 8, strings, strings + 4, shorts, tint]
                : [floats, floats, floats + 4, floats + 8, hashes, integers, integers];
            for (int j = 0; j < selectors.Length; j++)
            { int p = properties + j * 24; Q(b, p, empty - p); Q(b, p + 8, (long)selectors[j]); Q(b, p + 16, values[j] - p - 16); }
        }
        System.Array.Resize(ref b, cursor); Q(b, 0, b.Length); return b;
    }

    private static byte[] Bitmap(uint texture)
    {
        var b = new byte[176]; Q(b, 0, b.Length); Q(b, 16, 32 - 16); U(b, 28, 0x80804A67);
        A(b, 32, 56, 2, 0x80804A6C); A(b, 72, 112, 2, 0x80804A6F); A(b, 88, 144, 3, 0x80804A6F);
        U(b, 128, texture); U(b, 132, texture + 1);
        U(b, 160, texture + 2); U(b, 164, texture + 3); U(b, 168, texture + 4); return b;
    }
    private static void A(byte[] b, int p, int h, int count, uint type)
    { Q(b, p, count); Q(b, p + 8, h - p - 8); U(b, h - 4, 0x80809FBD); Q(b, h, count); U(b, h + 8, type); }
    private static void U(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    private static void S(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
    private static void Q(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), value);
    private static void F(byte[] b, int p, float value) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or OverflowException) { return; } throw new InvalidOperationException("Malformed visual edit accepted."); }
}
