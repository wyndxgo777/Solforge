using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks CUI property overrides: constructor dimensions become overrides, no-ops write nothing, and float pool insertion is selective. */
internal static class CuiOverrideChecks
{
    private const uint Tag = 0x80A00000;
    private static readonly CuiTypeRegistry Types = SyntheticTypes();
    private enum Slots { Missing, Empty, Nested }
    private enum Shared { None, Components, Properties }

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        byte[] defaults = Fixture(false, Slots.Missing, Shared.None);
        var original = Read(defaults);
        var rectangle = CuiLayout.Read(original).First();
        Check(rectangle.Properties.All(property => property.RecordOffset < 0), "Constructor fixture has stored coordinates.");
        Check(CuiLayout.Apply(defaults, original, rectangle, rectangle.Rectangle).SequenceEqual(defaults), "Constructor no-op wrote defaults.");
        byte[] moved = CuiLayout.Apply(defaults, original, rectangle, new(12, 0, 128, 240));
        var movedDocument = Read(moved);
        Check(CuiLayout.Read(movedDocument)[0].Rectangle == new Vector4(12, 0, 128, 240), "Constructor dimensions did not become overrides.");
        Check(movedDocument.Objects[0].Components[0].Properties.Count == 2 && movedDocument.Pools.Single().DescriptorOffset == 184,
            "Absent float pool or selective override insertion failed.");
        UnchangedSibling(original, movedDocument);
        Delta(defaults, moved);
        Reject(() => CuiPropertyEdits.Upsert(moved, original, original.Components.First(), []));

        var node = original.Nodes[0]; var component = original.Components.First();
        Check(CuiVisualProperties.Missing(node, component).Count == 8, "Missing text-property choices differ.");
        var tint = CuiVisualProperties.Add(defaults, original, node, component, CuiVisualPropertyKind.Tint, 0, new(.2f, .4f, .6f, 1));
        var tinted = Read(tint);
        Check(CuiVisualProperties.ReadTint(tinted, tinted.Nodes[0], tinted.Components.First())!.Value == new Vector4(.2f, .4f, .6f, 1),
            "Missing tint did not create a vector pool.");
        Reject(() => CuiVisualProperties.Add(defaults, original, node, component, CuiVisualPropertyKind.Alignment, 3));
        Reject(() => CuiVisualProperties.Add(defaults, original, node, component, CuiVisualPropertyKind.AutoWidth, 2));
        Reject(() => CuiPropertyEdits.Upsert(defaults, original, component, [Float(0x206, 1), Float(0x206, 2)]));

        foreach (Shared shared in Enum.GetValues<Shared>())
        {
            byte[] bytes = Fixture(true, Slots.Empty, shared);
            var source = Read(bytes);
            CuiPropertyOverride[] edits = [Float(0x206, 32), Float(0x207, 18), new(0x405, 232, BitConverter.GetBytes(0x1234u)),
                new(0x420, 152, BitConverter.GetBytes(30u)), new(0x41A, 136, new byte[] { 1 })];
            byte[] result = CuiPropertyEdits.Upsert(bytes, source, source.Objects[0].Components[0], edits);
            var updated = Read(result); var chosen = updated.Objects[0].Components[0];
            Check(chosen.Properties.Count == 5 && ReadFloat(updated, chosen, 0x206) == 32 && ReadFloat(updated, chosen, 0x207) == 18,
                "A mixed replacement/addition transaction lost a property.");
            Check(CuiVisualProperties.Read(updated, updated.Nodes[0], chosen).Single(field => field.Kind == CuiVisualPropertyKind.Text).Value == 0x1234,
                "The newly created string pool has the wrong value.");
            UnchangedSibling(source, updated);
            Check(CuiPropertyEdits.Upsert(result, updated, chosen, edits).SequenceEqual(result), "Override no-op reallocated pools.");
            Delta(bytes, result);
            byte[] onlyExisting = CuiPropertyEdits.Upsert(bytes, source, source.Objects[0].Components[0], [Float(0x206, 71)]);
            UnchangedSibling(source, Read(onlyExisting));
        }

        byte[] nested = Fixture(true, Slots.Nested, Shared.None);
        var nestedSource = Read(nested);
        var nestedEdit = CuiPropertyEdits.Upsert(nested, nestedSource, nestedSource.Components.First(), [Float(0x206, 64)]);
        var nestedResult = Read(nestedEdit); var selected = nestedResult.Components.First();
        Check(selected.Properties.Count == 2 && !selected.Properties[0].Direct && selected.Properties[0].Path.SequenceEqual(new uint[] { 0x700, 0x206 })
            && selected.Properties[1].Direct && ReadFloat(nestedResult, selected, 0x206) == 64, "Nested path was replaced instead of adding a direct override.");
        var slots = ArrayAt(nestedEdit, 120); var bindings = ArrayAt(nestedEdit, 88);
        Check(slots.Count == 2 && Relative(nestedEdit, bindings.Data + 8) == slots.Data && Relative(nestedEdit, bindings.Data + 32) == slots.Data,
            "Root bindings did not follow relocated nonempty slots.");
        Check(ArrayAt(nestedEdit, Relative(nestedEdit, selected.Properties[0].RecordOffset)).Count == 1
            && ArrayAt(nestedEdit, Relative(nestedEdit, selected.Properties[1].RecordOffset)).Count == 0, "Relocated slot identities changed.");
        UnchangedSibling(nestedSource, nestedResult); Delta(nested, nestedEdit);
        ProjectRoundtrip(defaults, moved);
        Console.WriteLine("cui overrides=pass defaults=selective absent_pools=created shared_arrays=isolated nested_slots=preserved project_reopen=pass");
    }

    /** Runs the checks that need an installed game; takes the installed data under test. */
    public static void Installed(PackageCatalog catalog, CuiTypeRegistry types)
    {
        const uint tag = 0x80BC7154;
        byte[] bytes = catalog.Read(tag);
        var source = CuiDocument.Read(tag, 0x80804825, bytes, nativeTypes: types);
        var layout = CuiLayout.Read(source).First(row => row.Properties.Any(property => property.RecordOffset < 0));
        int axis = Enumerable.Range(0, 4).First(index => layout.Properties[index].RecordOffset < 0);
        Vector4 desired = layout.Rectangle; desired[axis] += 19;
        byte[] changed = CuiLayout.Apply(bytes, source, layout, desired);
        var updated = CuiDocument.Read(tag, 0x80804825, changed, nativeTypes: types);
        int objectIndex = source.Objects.Single(row => row.Components.Contains(layout.Component)).Index;
        int componentIndex = source.Objects[objectIndex].Components.ToList().IndexOf(layout.Component);
        var selected = updated.Objects[objectIndex].Components[componentIndex];
        Check(CuiLayout.Read(updated).Single(row => row.Component == selected).Rectangle == desired, "Installed default override did not survive decoding.");
        CompareExcept(source, updated, objectIndex, componentIndex, [layout.Properties[axis].Selector]);
        Check(CuiLayout.Apply(changed, updated, CuiLayout.Read(updated).Single(row => row.Component == selected), desired).SequenceEqual(changed),
            "Installed override no-op changed bytes.");
        Delta(bytes, changed);
        Console.WriteLine($"cui override_fixture={tag:X8} component={layout.Component.Key:X} selector={layout.Properties[axis].Selector:X} original_unchanged={catalog.ReadOriginal(tag).SequenceEqual(bytes)}");
    }

    private static CuiPropertyOverride Float(ulong selector, float value) => new(selector, 184, BitConverter.GetBytes(value));
    private static CuiDocument Read(byte[] bytes) => CuiDocument.Read(Tag, 0x80804825, bytes, nativeTypes: Types);
    private static float ReadFloat(CuiDocument document, CuiComponent component, ulong selector) =>
        BinaryPrimitives.ReadSingleLittleEndian(document.Data.Span[component.Properties.Last(property => property.Direct && property.Selector == selector).ValueOffset..]);

    private static void UnchangedSibling(CuiDocument before, CuiDocument after) => CompareExcept(before, after, 0, 0,
        after.Objects[0].Components[0].Properties.Where(property => property.Direct).Select(property => property.Selector).ToArray());

    /** Relocation may change addresses; unrelated values and selector paths must remain identical. */
    private static void CompareExcept(CuiDocument before, CuiDocument after, int changedObject, int changedComponent, ulong[] selectors)
    {
        Check(before.Objects.Count == after.Objects.Count, "An override changed object ownership.");
        for (int owner = 0; owner < before.Objects.Count; owner++)
        {
            Check(before.Objects[owner].Components.Count == after.Objects[owner].Components.Count, "An override changed component ownership.");
            for (int component = 0; component < before.Objects[owner].Components.Count; component++)
            {
                var old = before.Objects[owner].Components[component]; var next = after.Objects[owner].Components[component];
                Check(old.Key == next.Key, "An override changed a component key.");
                bool Include(CuiProperty property) => owner != changedObject || component != changedComponent || !property.Direct || !selectors.Contains(property.Selector);
                string Value(CuiProperty property) => $"{property.Direct}:{string.Join('/', property.Path)}:{property.PoolDescriptor}:{string.Join('|', property.Fields.Select(field => field.Value))}";
                Check(old.Properties.Where(Include).Select(Value).SequenceEqual(next.Properties.Where(Include).Select(Value)), "An override changed an unrelated property or slot path.");
            }
        }
    }

    private static void Delta(byte[] source, byte[] result)
    {
        var delta = BinaryDelta.Create(source, result);
        Check(delta.Operations.All(operation => operation.Kind != DeltaKind.Add) && delta.Apply(source).SequenceEqual(result), "Override delta copied original data or failed reconstruction.");
    }

    private static void ProjectRoundtrip(byte[] source, byte[] result)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cui-overrides-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary, "game", "packages"));
            PackageWriter.WriteNew(Path.Combine(temporary, "game", "packages", "w64_cui_0100_0.pkg"), 0x100,
                [new AuthoredPackageEntry(0x80804825, 8 << 9, source)]);
            using var catalog = PackageCatalog.Open(Path.Combine(temporary, "game"), cacheOptions: new() { Enabled = false });
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "CUI overrides");
            project.ApplyEdit(Tag, result, catalog);
            var reopened = StudioProject.Open(project.DirectoryPath);
            Check(reopened.Changes.Count == 1 && reopened.ReadAsset(Tag, catalog).SequenceEqual(result)
                && catalog.ReadOriginal(Tag).SequenceEqual(source), "Reopened project lost the override or changed its original.");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    /** Synthetic schema rows isolate constructor editing from the built-in table. */
    private static CuiTypeRegistry SyntheticTypes()
    {
        CuiTypeDescriptor[] rows = [new(CuiTypeRegistry.TextType, CuiTypeRegistry.LayoutBase, true),
            new(CuiTypeRegistry.FrameType, CuiTypeRegistry.LayoutBase, true), new(CuiTypeRegistry.LayoutBase, 0, true),
            new(CuiTypeRegistry.ContainerType, CuiTypeRegistry.LayoutBase, true)];
        return CuiTypeRegistry.Create(rows, new Vector2(360, 240));
    }

    private static byte[] Fixture(bool existing, Slots slots, Shared shared)
    {
        byte[] bytes = new byte[4096]; int cursor = 344;
        int Array(int descriptor, int count, int stride, uint type)
        {
            int header = (cursor + 11) & ~7; cursor = header + 16 + count * stride;
            Q(bytes, descriptor, count); Q(bytes, descriptor + 8, header - descriptor - 8);
            U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); Q(bytes, header + 8, type); return header + 16;
        }
        int names = Array(24, 2, 4, 0x80804618); U(bytes, names, 1); U(bytes, names + 4, 2);
        int nodes = Array(56, 2, 16, 0x80804622);
        for (int i = 0; i < 2; i++) { U(bytes, nodes + i * 16, (uint)CuiTypeRegistry.TextType); S(bytes, nodes + i * 16 + 10, (ushort)(i + 1)); }
        int objects = Array(72, 2, 64, 0x8080462A);
        int first = Array(objects + 32, 1, 24, 0x808046D4); Q(bytes, first, 1);
        int second = shared == Shared.Components ? first : Array(objects + 96, 1, 24, 0x808046D4);
        if (shared == Shared.Components) { Q(bytes, objects + 96, 1); Q(bytes, objects + 104, first - 16 - objects - 104); }
        else Q(bytes, second, 2);
        int slot;
        if (slots == Slots.Missing) { slot = cursor; cursor += 16; }
        else slot = Array(120, 1, 16, 0x80804943);
        if (slots == Slots.Nested)
        {
            int path = Array(slot, 1, 4, 0x80804949); U(bytes, path, 0x700);
            int bindings = Array(88, 1, 56, 0x808046D8); Q(bytes, bindings + 8, slot - bindings - 8); Q(bytes, bindings + 32, slot - bindings - 32);
        }
        if (existing)
        {
            int pool = Array(184, 1, 4, 0x80804694); BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(pool), 7);
            int property = Array(first + 8, 1, 24, 0x80804858); Q(bytes, property, slot - property); Q(bytes, property + 8, 0x206); Q(bytes, property + 16, pool - property - 16);
            if (shared == Shared.Properties) { Q(bytes, second + 8, 1); Q(bytes, second + 16, property - 16 - second - 16); }
            else if (shared != Shared.Components)
            {
                int sibling = Array(second + 8, 1, 24, 0x80804858); Q(bytes, sibling, slot - sibling); Q(bytes, sibling + 8, 0x206); Q(bytes, sibling + 16, pool - sibling - 16);
            }
        }
        System.Array.Resize(ref bytes, cursor); Q(bytes, 0, bytes.Length); return bytes;
    }
    private static void Q(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), value);
    private static int Relative(byte[] b, int p) => checked(p + (int)BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p)));
    private static (int Count, int Data) ArrayAt(byte[] b, int p) =>
        (checked((int)BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p))), Relative(b, p + 8) + 16);
    private static void U(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    private static void S(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or OverflowException) { return; } throw new InvalidOperationException("Invalid override accepted."); }
}
