using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks CUI widget creation, duplication and name-table mutation: native identity, parent-local layout and typed properties must be preserved without altering the source. */
internal static class CuiWidgetChecks
{
    private const uint RootTag = 0x80A00000, ProgramTag = RootTag + 1;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        byte[] resource = Resource(), program = Hierarchy();
        var shortNames = resource.ToArray();
        int namesHeader = checked(32 + (int)BinaryPrimitives.ReadInt64LittleEndian(shortNames.AsSpan(32)));
        Q(shortNames, 24, 1); Q(shortNames, namesHeader, 1);
        var mismatchedNames = Open(program, shortNames);
        Reject(() => new CuiWidgetAuthoring(mismatchedNames.Program, mismatchedNames.Scene)
            .Create(CuiWidgetKind.Frame, 99, 0, new(0, 0, 80, 20)));
        Check(mismatchedNames.Scene.Resources[RootTag].Nodes[1].NameHash == 0
            && shortNames.AsSpan().SequenceEqual(mismatchedNames.Scene.Resources[RootTag].Data.Span),
            "Rejected name-table mutation changed its source.");
        var initial = Open(program, resource);
        var author = new CuiWidgetAuthoring(initial.Program, initial.Scene);
        var created = author.Create(CuiWidgetKind.Container, 31, 1, new(50, 40, 200, 150));
        var added = Open(created.Hierarchy, created.Resource);
        Check(created.SelectedNode == 3 && added.Scene.Nodes.Count == 4
            && added.Scene.Nodes.Single(node => node.Id == 3).Rectangle == new Vector4(60, 60, 200, 150), "Created container has wrong native identity or parent-local layout.");
        SameOriginal(initial.Scene, added.Scene);
        var copied = new CuiWidgetAuthoring(added.Program, added.Scene).Duplicate(2, 41, 3);
        var duplicate = Open(copied.Hierarchy, copied.Resource);
        Check(duplicate.Scene.Nodes.Single(node => node.Id == 4).ParentId == 3
            && duplicate.Scene.Nodes.Single(node => node.Id == 4).Properties.Select(row => row.Property.Selector)
                .SequenceEqual(duplicate.Scene.Nodes.Single(node => node.Id == 2).Properties.Select(row => row.Property.Selector)),
            "Duplicate did not preserve typed properties and parent ownership.");
        var root = duplicate.Scene.Resources[RootTag];
        var rectangle = CuiLayout.Read(root).Single(row => row.Component.Key == 4);
        var edited = CuiLayout.Apply(copied.Resource, root, rectangle, rectangle.Rectangle with { X = 29 });
        var detached = Open(copied.Hierarchy, edited);
        Check(detached.Scene.Nodes.Single(node => node.Id == 2).StoredRectangle!.Value.X == 5
            && detached.Scene.Nodes.Single(node => node.Id == 4).StoredRectangle!.Value.X == 29, "Duplicate property edit changed its source.");
        var reparented = new CuiWidgetAuthoring(detached.Program, detached.Scene).Reparent(4, 0);
        var moved = Open(reparented.Hierarchy, reparented.Resource);
        Check(moved.Scene.Nodes.Single(node => node.Id == 4).Rectangle!.Value.X == 29, "Reparenting changed local coordinates.");
        var retainTransform = new CuiWidgetAuthoring(moved.Program, moved.Scene).Remove(4);
        Check(BindingTransforms(reparented.Resource).SequenceEqual(BindingTransforms(retainTransform.Resource)),
            "Removing a widget changed a retained binding transform.");
        Reject(() => new CuiWidgetAuthoring(moved.Program, moved.Scene).Reparent(1, 3));
        Reject(() => author.Reparent(0, 1));
        var removed = new CuiWidgetAuthoring(moved.Program, moved.Scene).Remove(1);
        var deleted = Open(removed.Hierarchy, removed.Resource);
        Check(removed.RemovedNodes.SetEquals(new ushort[] { 1, 2, 3 }) && removed.RemovedBindings == 1
            && deleted.Scene.Nodes.Select(node => node.Id).SequenceEqual(new[] { 0, 4 }), "Removal retained an active descendant or linked binding.");
        Check(deleted.Program.Hierarchy.Count == moved.Program.Hierarchy.Count && deleted.Scene.Resources[RootTag].Nodes.Count == root.Nodes.Count,
            "Removal renumbered native construction slots.");
        var graph = new CuiInspector().Inspect(new(ProgramTag, 0x8080496A, removed.Hierarchy));
        Check(graph.Graph!.Nodes.Count == 2 && graph.Graph.Edges.Count == 1,
            "Inspection exports retained a removed node or sentinel parent as an active graph node.");
        var again = new CuiWidgetAuthoring(deleted.Program, deleted.Scene).Create(CuiWidgetKind.Text, 51, 0, new(7, 8, 90, 30));
        Check(again.SelectedNode == 5, "New widget reused a deleted native ID.");
        var bitmap = new CuiWidgetAuthoring(Open(again.Hierarchy, again.Resource).Program,
            Open(again.Hierarchy, again.Resource).Scene).Create(CuiWidgetKind.Bitmap, 61, 0, new(0, 0, 64, 64));
        Check(Open(bitmap.Hierarchy, bitmap.Resource).Scene.Nodes.Any(node => node.Id == 6 && node.Definition.TypeHash == CuiTypeRegistry.BitmapType),
            "Bitmap creation did not select its native constructor.");
        author.RequireUnchanged(program, resource);
        Reject(() => author.RequireUnchanged(created.Hierarchy, resource));
        foreach (var (before, after) in new[] { (program, bitmap.Hierarchy), (resource, bitmap.Resource) })
        {
            var delta = BinaryDelta.Create(before, after);
            Check(delta.Operations.All(row => row.Kind != DeltaKind.Add) && delta.Apply(before).SequenceEqual(after), "Structural changes failed the delta roundtrip.");
        }
        var animated = Open(program, Resource(true));
        Reject(() => new CuiWidgetAuthoring(animated.Program, animated.Scene).Remove(2));
        Console.WriteLine("cui_widgets create=pass duplicate=isolated reparent=pass cycle=rejected removal=stable_ids bindings=removed animation_dependency=rejected delta=pass");
    }

    /** Runs the checks that need an installed game; takes the installed data under test. */
    public static void Installed(PackageCatalog catalog, CuiTypeRegistry types)
    {
        const uint tag = 0x80BC6F5A;
        byte[] program = catalog.Read(tag);
        var hierarchy = CuiDocument.Read(tag, 0x8080496A, program, nativeTypes: types);
        byte[] original = catalog.Read(hierarchy.RootResource);
        CuiComposition Compose(CuiDocument parsed, byte[] root) => CuiComposition.Read(parsed,
            id => CuiDocument.Read(id, 0x80804825, id == parsed.RootResource ? root : catalog.Read(id), nativeTypes: types));
        var baseline = Compose(hierarchy, original);
        var author = new CuiWidgetAuthoring(hierarchy, baseline);
        var source = author.LocalWidgets.First(node => node.Definition.TypeHash == CuiTypeRegistry.TextType);
        var added = author.Create(CuiWidgetKind.Frame, 123, 0, new(90, 80, 800, 500));
        var current = CuiDocument.Read(tag, 0x8080496A, added.Hierarchy, nativeTypes: types);
        var scene = Compose(current, added.Resource);
        SameOriginal(baseline, scene);
        var duplicate = new CuiWidgetAuthoring(current, scene).Duplicate((ushort)source.Id, 456, added.SelectedNode);
        current = CuiDocument.Read(tag, 0x8080496A, duplicate.Hierarchy, nativeTypes: types);
        scene = Compose(current, duplicate.Resource);
        var removed = new CuiWidgetAuthoring(current, scene).Remove(added.SelectedNode);
        current = CuiDocument.Read(tag, 0x8080496A, removed.Hierarchy, nativeTypes: types);
        var final = Compose(current, removed.Resource);
        Check(final.Nodes.Count == baseline.Nodes.Count && !final.Nodes.Any(node => node.Id >= baseline.Nodes.Count),
            "Installed removed widgets remained active in composition.");
        SameOriginal(baseline, final);
        Check(BindingTransforms(original).SequenceEqual(BindingTransforms(removed.Resource)), "Installed retained binding transform was not rebased.");
        foreach (var (before, after) in new[] { (program, removed.Hierarchy), (original, removed.Resource) })
            Check(BinaryDelta.Create(before, after).Apply(before).SequenceEqual(after), "Installed widget edit failed delta reconstruction.");
        Check(catalog.ReadOriginal(tag).SequenceEqual(program) && catalog.ReadOriginal(hierarchy.RootResource).SequenceEqual(original),
            "Installed widget edit changed package originals.");
        Console.WriteLine($"cui_widgets hierarchy={tag:X8} root={hierarchy.RootResource:X8} local_copy={source.Id} appended={added.SelectedNode},{duplicate.SelectedNode} original_nodes={baseline.Nodes.Count} active_after_remove={final.Nodes.Count} source=unchanged");
    }

    private static (CuiDocument Program, CuiComposition Scene) Open(byte[] program, byte[] resource)
    {
        var parsed = CuiDocument.Read(ProgramTag, 0x8080496A, program);
        return (parsed, CuiComposition.Read(parsed, tag => CuiDocument.Read(tag, 0x80804825, resource)));
    }
    private static void SameOriginal(CuiComposition before, CuiComposition after)
    {
        foreach (var node in before.Nodes)
        {
            var current = after.Nodes.Single(row => row.Id == node.Id);
            Check(node.ParentId == current.ParentId && node.StoredRectangle == current.StoredRectangle
                && node.Definition.TypeHash == current.Definition.TypeHash
                && node.Definition.AuxiliaryTypeHash == current.Definition.AuxiliaryTypeHash
                && node.Definition.NameHash == current.Definition.NameHash, "Structural insertion changed an existing widget.");
        }
    }
    private static byte[] Hierarchy()
    {
        var bytes = new byte[76]; Q(bytes, 0, bytes.Length); A(bytes, 8, 48, 3, 0x80804616); S(bytes, 24, 3); S(bytes, 26, 3); U(bytes, 28, RootTag);
        S(bytes, 64, 0x7FFF); S(bytes, 66, 0); S(bytes, 68, 0); S(bytes, 70, 1); S(bytes, 72, 1); S(bytes, 74, 2); return bytes;
    }
    private static byte[] Resource(bool animation = false)
    {
        var bytes = new byte[4096]; int cursor = 344;
        int Array(int field, int count, int stride, uint type)
        { int header = (cursor + 11) & ~7; cursor = header + 16 + count * stride; A(bytes, field, header, count, type); return header + 16; }
        int names = Array(24, 2, 4, 0x80804618); U(bytes, names, 11); U(bytes, names + 4, 21);
        int nodes = Array(56, 2, 16, 0x80804622); U(bytes, nodes, (uint)CuiTypeRegistry.FrameType); S(bytes, nodes + 10, 1);
        U(bytes, nodes + 16, (uint)CuiTypeRegistry.TextType); S(bytes, nodes + 26, 2);
        int owner = Array(72, 1, 64, 0x8080462A);
        Q(bytes, owner, unchecked((long)0x701CFA3E7F9B89AF)); Q(bytes, owner + 8, unchecked((long)0xECBABAE340FB9D24));
        Q(bytes, owner + 16, 0xAD52C0B9); Q(bytes, owner + 24, 0xB93A719D);
        int components = Array(owner + 32, 2, 24, 0x808046D4), empty = Array(120, 1, 16, 0x80804943);
        int floats = Array(184, 8, 4, 0x80804694);
        float[] values = [10, 20, 400, 300, 5, 6, 100, 20];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(floats + i * 4), values[i]);
        for (int widget = 0; widget < 2; widget++)
        {
            int at = components + widget * 24; Q(bytes, at, widget + 1);
            int properties = Array(at + 8, 4, 24, 0x80804858);
            for (int axis = 0; axis < 4; axis++)
            { int row = properties + axis * 24; Q(bytes, row, empty - row); Q(bytes, row + 8, 0x206 + axis); Q(bytes, row + 16, floats + widget * 16 + axis * 4 - row - 16); }
        }
        int link = Array(88, 1, 56, 0x808046D8); S(bytes, link, 2); S(bytes, link + 2, 2); S(bytes, link + 24, 1); S(bytes, link + 26, 1);
        Q(bytes, link + 8, empty - link - 8); Q(bytes, link + 32, empty - link - 32);
        int transform = (cursor + 7) & ~3; cursor = transform + 4;
        U(bytes, transform - 4, 0x808046E0); Q(bytes, link + 48, transform - link - 48);
        if (animation)
        {
            int track = Array(owner + 48, 1, 24, 0x808046F3); U(bytes, track, 1);
            int target = Array(track + 8, 1, 32, 0x808046F5); S(bytes, target, 2);
        }
        System.Array.Resize(ref bytes, cursor); Q(bytes, 0, bytes.Length); return bytes;
    }
    private static void A(byte[] b, int p, int h, int count, uint type)
    { Q(b, p, count); Q(b, p + 8, h - p - 8); U(b, h - 4, 0x80809FBD); Q(b, h, count); Q(b, h + 8, type); }
    private static void Q(byte[] b, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), value);
    private static void U(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value);
    private static void S(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static string[] BindingTransforms(byte[] bytes)
    {
        int count = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(88)));
        int data = checked(96 + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(96)) + 16);
        List<string> result = [];
        for (int index = 0; index < count; index++)
        {
            int pointer = data + index * 56 + 48;
            long relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pointer));
            if (relative == 0) { result.Add("none"); continue; }
            int target = checked(pointer + (int)relative);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(target - 4));
            int size = type == 0x808046E0 ? 4 : type == 0x808046DC ? 8 : throw new InvalidDataException("Unexpected fixture transform class.");
            result.Add($"{type:X8}:{Convert.ToHexString(bytes.AsSpan(target, size))}");
        }
        return result.ToArray();
    }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new InvalidOperationException("Invalid structural mutation was accepted."); }
}
