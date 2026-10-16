using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Widget kinds that can be created. */
public enum CuiWidgetKind { Frame, Container, Text, Bitmap }
/** Result of a hierarchy edit: new hierarchy and resource bytes, the node to select, and the nodes and bindings removed. */
public sealed record CuiWidgetMutation(byte[] Hierarchy, byte[] Resource, ushort SelectedNode,
    IReadOnlySet<ushort> RemovedNodes, int RemovedBindings = 0);

/** Structural edits keep imported node ranges and all existing construction IDs unchanged. */
public sealed class CuiWidgetAuthoring
{
    private readonly CuiDocument hierarchy, resource;
    private readonly CuiComposition composition;
    private readonly int firstLocal;
    public IReadOnlyList<CuiComposedNode> LocalWidgets => composition.Nodes.Where(node => node.Id >= firstLocal && Core(node.Definition)).ToArray();
    public IReadOnlyList<CuiComposedNode> Parents => composition.Nodes.Where(node => node.Id == 0 ||
        Type(node.Definition) is CuiTypeRegistry.FrameType or CuiTypeRegistry.ContainerType).ToArray();

    public CuiWidgetAuthoring(CuiDocument hierarchy, CuiComposition composition)
    {
        if (hierarchy.ClassHash != 0x8080496A || hierarchy.Data.Length < 40
            || !composition.Resources.TryGetValue(hierarchy.RootResource, out var root))
            throw new InvalidDataException("Open an interface hierarchy before adding widgets.");
        this.hierarchy = hierarchy; this.composition = composition; resource = root;
        var r = new TagReader(hierarchy.Data);
        if (new TagReader(resource.Data).Array(24, 0x80804618, 4).Count != resource.Nodes.Count)
            throw new NotSupportedException("Widget names and definitions must have matching counts before structural editing.");
        int count = hierarchy.Hierarchy.Count;
        if (count is < 1 or > 5120 || r.U16(24) != count || r.U16(26) != count
            || hierarchy.Hierarchy.Select(node => (int)node.Id).Order().Where((id, index) => id != index).Any()
            || hierarchy.Hierarchy[0] is not { Id: 0, Parent: 0x7FFF })
            throw new NotSupportedException("This hierarchy uses a nonstandard construction range.");
        int constructed = 1;
        Count(resource, []);
        if (constructed != count) throw new InvalidDataException("Hierarchy capacity differs from its constructed widgets.");
        firstLocal = count - resource.Nodes.Count;
        if (resource.Nodes.Where((node, index) => node.Id != firstLocal + index).Any())
            throw new NotSupportedException("Local widget identities differ from construction order.");
        void Count(CuiDocument document, HashSet<uint> path)
        {
            if (!path.Add(document.TagHash)) throw new InvalidDataException("CUI imports contain a cycle.");
            foreach (var import in document.Imports)
                if (composition.Resources.TryGetValue(import.TagHash, out var child)) Count(child, path);
                else throw new InvalidDataException("An imported widget resource is missing.");
            constructed = checked(constructed + document.Nodes.Count);
            path.Remove(document.TagHash);
        }
    }

    /** Adds a widget of the given kind under parent with the given rectangle. */
    public CuiWidgetMutation Create(CuiWidgetKind kind, uint nameHash, ushort parent, Vector4 rectangle)
    {
        Capacity();
        Parent(parent); Dimensions(rectangle);
        var writer = new Writer(resource.Data); ushort id = checked((ushort)hierarchy.Hierarchy.Count);
        AppendDefinition(writer, Type(kind), nameHash, id);
        int owner = DefaultOwner(writer);
        int component = writer.AppendRows(owner + 32, 0x808046D4, 24, 1, [16]);
        writer.Q(component, id);
        byte[] bytes = writer.Finish();
        var parsed = CuiDocument.Read(resource.TagHash, resource.ClassHash, bytes, nativeTypes: resource.NativeTypes);
        var selected = parsed.Objects.Single(row => row.Offset == owner).Components.Single(row => row.Key == id);
        List<CuiPropertyOverride> values = [];
        for (int axis = 0; axis < 4; axis++) values.Add(new((ulong)(0x206 + axis), 200, BitConverter.GetBytes(rectangle[axis] / CuiLayout.ReferenceHeight)));
        values.Add(new(0x20F, 152, BitConverter.GetBytes(0u)));
        if (kind == CuiWidgetKind.Text) values.Add(new(0x405, 232, BitConverter.GetBytes(0x811C9DC5u)));
        bytes = CuiPropertyEdits.Upsert(bytes, parsed, selected, values);
        return Checked(new(AppendHierarchy(id, parent), bytes, id, new HashSet<ushort>()));
    }

    /** Duplicates copy stored profile properties while leaving runtime binding programs separate. */
    public CuiWidgetMutation Duplicate(ushort sourceNode, uint nameHash, ushort parent)
    {
        Capacity();
        var source = Local(sourceNode); Parent(parent);
        var writer = new Writer(resource.Data); ushort id = checked((ushort)hierarchy.Hierarchy.Count);
        AppendDefinition(writer, Type(source.Definition), nameHash, id);
        foreach (var owner in resource.Objects)
        {
            var originals = owner.Components.Where(component => component.Key == sourceNode).ToArray();
            if (originals.Length == 0) continue;
            int added = writer.AppendRows(owner.Offset + 32, 0x808046D4, 24, originals.Length, [16]);
            for (int i = 0; i < originals.Length; i++)
            {
                int target = added + i * 24;
                writer.Copy(originals[i].Offset, target, 24, [16]); writer.Q(target, id);
            }
        }
        if (!resource.Objects.Any(owner => owner.IsDefaultProfile && owner.Components.Any(component => component.Key == sourceNode)))
        {
            int owner = DefaultOwner(writer);
            int component = writer.AppendRows(owner + 32, 0x808046D4, 24, 1, [16]); writer.Q(component, id);
        }
        return Checked(new(AppendHierarchy(id, parent), writer.Finish(), id, new HashSet<ushort>()));
    }

    /** Moves a node under a new parent; throws if the parent is one of its descendants. */
    public CuiWidgetMutation Reparent(ushort node, ushort parent)
    {
        _ = Local(node); Parent(parent);
        var descendants = Descendants(node);
        if (descendants.Contains(parent)) throw new InvalidDataException("A widget cannot be parented under its own children.");
        byte[] data = hierarchy.Data.ToArray();
        var row = hierarchy.Hierarchy.Single(row => row.Id == node);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(row.Offset), parent);
        return Checked(new(data, resource.Data.ToArray(), node, new HashSet<ushort>()));
    }

    /** An unreachable parent destroys the node during native linking without renumbering other nodes. */
    public CuiWidgetMutation Remove(ushort node)
    {
        _ = Local(node);
        var removed = Descendants(node);
        if (removed.Any(id => id < firstLocal)) throw new NotSupportedException("Remove imported children in their owning interface.");
        var reader = new TagReader(resource.Data);
        foreach (var owner in resource.Objects)
        {
            var tracks = reader.Array(owner.Offset + 48, 0x808046F3, 24);
            for (int track = 0; track < tracks.Count; track++)
            {
                var targets = reader.Array(tracks.Data + track * 24 + 8, 0x808046F5, 32);
                for (int target = 0; target < targets.Count; target++)
                    if (removed.Contains(reader.U16(targets.Data + target * 32)))
                        throw new NotSupportedException("This widget has authored animation targets. Remove those dependencies before deleting it.");
            }
        }
        byte[] data = hierarchy.Data.ToArray();
        foreach (var row in hierarchy.Hierarchy.Where(row => removed.Contains(row.Id)))
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(row.Offset), 0x7FFF);
        var writer = new Writer(resource.Data);
        var bindings = reader.Array(88, 0x808046D8, 56);
        var retained = Enumerable.Range(0, bindings.Count).Select(index => bindings.Data + index * 56)
            .Where(at => !removed.Contains(reader.U16(at + 2)) && !removed.Contains(reader.U16(at + 26))).ToArray();
        int destination = writer.ReplaceRows(88, 0x808046D8, 56, retained.Length);
        for (int i = 0; i < retained.Length; i++) writer.Copy(retained[i], destination + i * 56, 56, [8, 32, 48]);
        return Checked(new(data, writer.Finish(), hierarchy.Hierarchy.Single(row => row.Id == node).Parent, removed,
            bindings.Count - retained.Length));
    }

    /** Every binding that read before the edit reads the same after it, unless its widget was removed. */
    private CuiWidgetMutation Checked(CuiWidgetMutation mutation)
    {
        var expected = CuiBindings.Read(resource.Data).Where(row => row.Error.Length == 0
            && !mutation.RemovedNodes.Contains(row.Source.Node) && !mutation.RemovedNodes.Contains(row.Target.Node)).Select(Signature);
        var actual = CuiBindings.Read(mutation.Resource).Where(row => row.Error.Length == 0).Select(Signature);
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException("This edit would break a data binding.");
        return mutation;

        static string Signature(CuiBinding row) => $"{row.Source.Node}:{row.Source.PathKey}>{row.Target.Node}:{row.Target.PathKey}"
            + $":{row.Transform.Kind}:{row.Transform.Comparison}:{row.Transform.Operand}";
    }

    /** Throws unless both tags still match the hashes of the bytes this editor was built from. */
    public void RequireUnchanged(ReadOnlySpan<byte> currentHierarchy, ReadOnlySpan<byte> currentResource)
    {
        if (Convert.ToHexString(SHA256.HashData(currentHierarchy)) != hierarchy.SourceSha256
            || Convert.ToHexString(SHA256.HashData(currentResource)) != resource.SourceSha256)
            throw new InvalidDataException("The interface changed. Reload it before editing.");
    }

    private HashSet<ushort> Descendants(ushort node)
    {
        HashSet<ushort> result = [node];
        bool changed;
        do
        {
            changed = false;
            foreach (var row in hierarchy.Hierarchy)
                if (result.Contains(row.Parent)) changed |= result.Add(row.Id);
        } while (changed);
        return result;
    }

    private CuiComposedNode Local(ushort node) => LocalWidgets.SingleOrDefault(row => row.Id == node)
        ?? throw new InvalidDataException("Choose a local core widget.");
    private void Parent(ushort parent)
    {
        if (!Parents.Any(row => row.Id == parent)) throw new InvalidDataException("Choose the screen, a frame, or a container as parent.");
    }
    private void Capacity()
    {
        if (hierarchy.Hierarchy.Count >= 5120) throw new NotSupportedException("The native interface has reached its widget limit.");
    }
    private static bool Core(CuiNode node) => Type(node) is CuiTypeRegistry.FrameType or CuiTypeRegistry.ContainerType
        or CuiTypeRegistry.TextType or CuiTypeRegistry.BitmapType;
    private static ulong Type(CuiNode node) => ((ulong)node.AuxiliaryTypeHash << 32) | node.TypeHash;
    private static ulong Type(CuiWidgetKind kind) => kind switch
    {
        CuiWidgetKind.Frame => CuiTypeRegistry.FrameType, CuiWidgetKind.Container => CuiTypeRegistry.ContainerType,
        CuiWidgetKind.Text => CuiTypeRegistry.TextType, CuiWidgetKind.Bitmap => CuiTypeRegistry.BitmapType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static void Dimensions(Vector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W) || value.Z < 0 || value.W < 0)
            throw new InvalidDataException("Enter finite coordinates and nonnegative dimensions.");
    }
    private static void AppendDefinition(Writer writer, ulong type, uint name, ushort id)
    {
        int names = writer.AppendRows(24, 0x80804618, 4, 1, []); writer.U(names, name);
        int definition = writer.AppendRows(56, 0x80804622, 16, 1, []); writer.Q(definition, unchecked((long)type)); writer.S(definition + 10, id);
    }
    private int DefaultOwner(Writer writer)
    {
        var existing = resource.Objects.FirstOrDefault(row => row.IsDefaultProfile);
        if (existing is not null) return existing.Offset;
        int owner = writer.AppendRows(72, 0x8080462A, 64, 1, [40, 56]);
        writer.Q(owner, 0x701CFA3E7F9B89AF); writer.Q(owner + 8, unchecked((long)0xECBABAE340FB9D24));
        writer.Q(owner + 16, 0xAD52C0B9); writer.Q(owner + 24, 0xB93A719D); return owner;
    }
    private byte[] AppendHierarchy(ushort id, ushort parent)
    {
        var writer = new Writer(hierarchy.Data);
        int row = writer.AppendRows(8, 0x80804616, 4, 1, []); writer.S(row, parent); writer.S(row + 2, id);
        writer.S(24, checked((ushort)(id + 1))); writer.S(26, checked((ushort)(id + 1))); return writer.Finish();
    }

    /** Appended arrays retain relative targets for every copied pointer field. */
    private sealed class Writer(ReadOnlyMemory<byte> source)
    {
        private byte[] bytes = source.ToArray();
        /** Grows the array at descriptor by extra rows, copying old rows; returns the offset where the new rows start. */
        public int AppendRows(int descriptor, uint type, int stride, int extra, int[] pointers)
        {
            var old = new TagReader(bytes).Array(descriptor, type, stride);
            int data = ReplaceRows(descriptor, type, stride, checked(old.Count + extra));
            for (int i = 0; i < old.Count; i++) Copy(old.Data + i * stride, data + i * stride, stride, pointers);
            return data + old.Count * stride;
        }
        /** Points the array descriptor at a fresh block of count rows (new header appended to the buffer) and returns its data offset; 0 when count is 0. */
        public int ReplaceRows(int descriptor, uint type, int stride, int count)
        {
            if (count == 0) { Q(descriptor, 0); Q(descriptor + 8, 0); return 0; }
            int header = checked((bytes.Length + 11) & ~7), data = checked(header + 16);
            Array.Resize(ref bytes, checked(data + count * stride)); U(header - 4, 0x80809FBD); Q(header, count); Q(header + 8, type);
            Q(descriptor, count); Q(descriptor + 8, header - descriptor - 8); return data;
        }
        /** Copies a block inside the buffer and rewrites the relative pointers at the given offsets so they still reach their targets. */
        public void Copy(int sourceOffset, int destination, int length, int[] pointers)
        {
            var reader = new TagReader(bytes); bytes.AsSpan(sourceOffset, length).CopyTo(bytes.AsSpan(destination));
            foreach (int pointer in pointers)
            {
                int at = sourceOffset + pointer, target = reader.Relative(at);
                Q(destination + pointer, target < 0 ? 0 : target - destination - pointer);
            }
        }
        /** Writes a little-endian 64-bit value. */
        public void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
        /** Writes a little-endian 32-bit value. */
        public void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
        /** Writes a little-endian 16-bit value. */
        public void S(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
        /** Stores the total length at offset 0 and returns the buffer. */
        public byte[] Finish() { Q(0, bytes.Length); return bytes; }
    }
}
