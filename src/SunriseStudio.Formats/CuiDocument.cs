using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Imported resource: tag, first node ID and node count it contributes. */
public sealed record CuiImport(uint TagHash, ushort FirstNode, ushort NodeCount, int Offset);
/** One property of a CUI component with its record and value offsets, value class and decoded fields. */
public sealed record CuiProperty(ulong Selector, int RecordOffset, int ValueOffset, uint ValueClass, int PoolDescriptor,
    bool Direct, IReadOnlyList<AssetProperty> Fields)
{
    public IReadOnlyList<uint> Path { get; init; } = [];
}
/** A component of a CUI object, keyed by its component key. */
public sealed record CuiComponent(ulong Key, int Offset, IReadOnlyList<CuiProperty> Properties);
/** A CUI object and its components; IsDefaultProfile marks the default-profile variant. */
public sealed record CuiObject(int Index, int Offset, IReadOnlyList<CuiComponent> Components)
{
    public bool IsDefaultProfile { get; init; }
}
/** A CUI node with ID, hashed and resolved names, and node type hashes. */
public sealed record CuiNode(ushort Id, uint NameHash, uint TypeHash, uint AuxiliaryTypeHash,
    string Name, int Offset);
/** Parent link of one hierarchy node. */
public sealed record CuiHierarchyNode(ushort Id, ushort Parent, int Offset);
/** Hierarchy rules shared by CUI readers and editors. */
public static class CuiHierarchy
{
    /** Native linking removes non-root nodes whose parent is unreachable. */
    public static IReadOnlySet<int> ActiveNodes(IEnumerable<CuiHierarchyNode> rows)
    {
        var hierarchy = rows.ToArray();
        HashSet<int> active = [0];
        bool extended;
        do
        {
            extended = false;
            foreach (var row in hierarchy)
                if (active.Contains(row.Parent)) extended |= active.Add(row.Id);
        } while (extended);
        return active;
    }
}
/** Descriptor and data location of a typed value pool in a CUI tag. */
public sealed record CuiValuePool(int DescriptorOffset, int DataOffset, int Count, int Stride,
    uint ClassHash, string Name);

/** Static properties retain their owning record and pooled value address. */
public sealed class CuiDocument
{
    public uint TagHash { get; init; }
    public uint ClassHash { get; init; }
    public uint RootResource { get; init; }
    public string SourceSha256 { get; init; } = "";
    public ReadOnlyMemory<byte> Data { get; init; }
    public CuiTypeRegistry NativeTypes { get; init; } = CuiTypeRegistry.Core;
    public IReadOnlyList<CuiNode> Nodes { get; init; } = [];
    public IReadOnlyList<CuiHierarchyNode> Hierarchy { get; init; } = [];
    public IReadOnlyList<CuiImport> Imports { get; init; } = [];
    public IReadOnlyList<CuiObject> Objects { get; init; } = [];
    public IReadOnlyList<CuiValuePool> Pools { get; init; } = [];
    public IEnumerable<CuiComponent> Components => Objects.SelectMany(value => value.Components);
    public IEnumerable<CuiProperty> Properties => Components.SelectMany(value => value.Properties);

    /** True for the CUI widget (0x80804825) and hierarchy (0x8080496A) classes. */
    public static bool Supports(uint classHash) => classHash is 0x80804825 or 0x8080496A;

    /** Hierarchy IDs are stored values; their array positions are unrelated. */
    public static CuiDocument Read(uint tagHash, uint classHash, ReadOnlyMemory<byte> bytes,
        Func<uint, string?>? resolveHash = null, CuiTypeRegistry? nativeTypes = null)
    {
        var r = new TagReader(bytes);
        if (classHash == 0x8080496A)
        {
            if (!r.Has(0, 40)) throw new InvalidDataException("Truncated CUI hierarchy.");
            var rows = r.Array(8, 0x80804616, 4);
            List<CuiHierarchyNode> hierarchy = [];
            HashSet<ushort> ids = [];
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * 4;
                ushort id = r.U16(p + 2);
                if (!ids.Add(id)) throw new InvalidDataException("Duplicate CUI hierarchy node.");
                hierarchy.Add(new(id, r.U16(p), p));
            }
            foreach (var node in hierarchy)
                if (node.Parent != 0x7FFF && !ids.Contains(node.Parent))
                    throw new InvalidDataException("CUI hierarchy parent is missing.");
            var parents = hierarchy.ToDictionary(node => node.Id, node => node.Parent);
            HashSet<ushort> resolved = [];
            foreach (var node in hierarchy)
            {
                HashSet<ushort> path = [];
                ushort current = node.Id;
                while (current != 0x7FFF && !resolved.Contains(current))
                {
                    if (!path.Add(current)) throw new InvalidDataException("CUI hierarchy contains a cycle.");
                    current = parents[current];
                }
                resolved.UnionWith(path);
            }
            return new() { TagHash = tagHash, ClassHash = classHash, RootResource = r.U32(28), Hierarchy = hierarchy,
                NativeTypes = nativeTypes ?? CuiTypeRegistry.Core, Data = bytes,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)) };
        }
        if (classHash != 0x80804825) throw new NotSupportedException("This asset is not a CUI resource.");
        if (!r.Has(0, 344)) throw new InvalidDataException("Truncated CUI resource.");
        List<CuiImport> imports = [];
        var imported = r.Array(8, 0x80804607, 12);
        for (int i = 0; i < imported.Count; i++)
        {
            int p = imported.Data + i * 12;
            imports.Add(new(r.U32(p), r.U16(p + 4), r.U16(p + 6), p));
        }
        var names = r.Array(24, 0x80804618, 4);
        var types = r.Array(56, 0x80804622, 16);
        List<CuiNode> nodes = [];
        for (int i = 0; i < types.Count; i++)
        {
            int p = types.Data + i * 16;
            ushort id = r.U16(p + 10);
            uint name = i < names.Count ? r.U32(names.Data + i * 4) : 0;
            nodes.Add(new(id, name, r.U32(p), r.U32(p + 4), resolveHash?.Invoke(name) ?? $"Node {id}", p));
        }
        List<CuiValuePool> pools = [];
        foreach (var schema in PoolSchemas)
        {
            var array = r.Array(schema.Offset, schema.Class, schema.Stride);
            if (array.Count > 0) pools.Add(new(schema.Offset, array.Data, array.Count,
                schema.Stride, schema.Class, schema.Name));
        }
        var objects = r.Array(72, 0x8080462A, 64);
        List<CuiObject> objectRows = [];
        for (int i = 0; i < objects.Count; i++)
        {
            int p = objects.Data + i * 64;
            var components = r.Array(p + 32, 0x808046D4, 24);
            List<CuiComponent> componentRows = [];
            for (int j = 0; j < components.Count; j++)
            {
                int c = components.Data + j * 24;
                var references = r.Array(c + 8, 0x80804858, 24);
                List<CuiProperty> properties = [];
                for (int k = 0; k < references.Count; k++)
                {
                    int e = references.Data + k * 24;
                    int slot = r.Relative(e), value = r.Relative(e + 16);
                    var pool = pools.FirstOrDefault(pool => value >= pool.DataOffset
                        && value < pool.DataOffset + pool.Count * pool.Stride
                        && (value - pool.DataOffset) % pool.Stride == 0);
                    if (pool is null) throw new InvalidDataException("CUI property leaves its typed value pool.");
                    var path = r.Array(slot, 0x80804949, 4);
                    ulong selector = r.U64(e + 8);
                    properties.Add(new(selector, e, value, pool.ClassHash, pool.DescriptorOffset, path.Count == 0,
                        Fields(r, pool, value, selector))
                    { Path = [.. Enumerable.Range(0, path.Count).Select(index => r.U32(path.Data + index * 4)), (uint)selector] });
                }
                componentRows.Add(new(r.U64(c), c, properties));
            }
            objectRows.Add(new(i, p, componentRows)
            { IsDefaultProfile = r.U64(p) == 0x701CFA3E7F9B89AF && r.U64(p + 8) == 0xECBABAE340FB9D24
                && r.U64(p + 16) == 0xAD52C0B9 && r.U64(p + 24) == 0xB93A719D });
        }
        return new() { TagHash = tagHash, ClassHash = classHash, Data = bytes,
            NativeTypes = nativeTypes ?? CuiTypeRegistry.Core,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)), Imports = imports, Nodes = nodes,
            Objects = objectRows, Pools = pools };
    }

    private static readonly (int Offset, uint Class, int Stride, string Name)[] PoolSchemas =
    [
        (136, 0x8080468D, 1, "Bytes"), (152, 0x80804696, 4, "Integers"),
        (168, 0x80804695, 8, "Pairs"), (184, 0x80804694, 4, "Floats A"),
        (200, 0x80804694, 4, "Floats B"), (216, 0x80804693, 16, "Vectors"),
        (232, 0x80804692, 4, "String hashes"), (264, 0x80804690, 4, "Hashes"),
        (280, 0x8080468B, 2, "Shorts"), (312, 0x8080468F, 4, "Words"),
        (328, 0x80804688, 16, "Records")
    ];

    /** Class and element stride of a value pool, from its descriptor offset; throws for unknown pools. */
    internal static (uint Class, int Stride) PoolSchema(int descriptor)
    {
        var schema = PoolSchemas.FirstOrDefault(value => value.Offset == descriptor);
        return schema.Class == 0 ? throw new InvalidDataException("Unknown CUI value pool.") : (schema.Class, schema.Stride);
    }

    /** Display name for a property selector; layout selectors 0x206-0x209 read as X, Y, Width, Height. */
    public static string PropertyName(ulong selector, bool layout = false) => layout ? selector switch
    {
        0x206 => "X", 0x207 => "Y", 0x208 => "Width", 0x209 => "Height",
        _ => $"Property {selector:X}"
    } : $"Property {selector:X}";

    /** Unknown record classes stay read-only; only known scalar storage becomes editable. */
    private static IReadOnlyList<AssetProperty> Fields(TagReader r, CuiValuePool pool, int p, ulong selector)
    {
        string name = PropertyName(selector);
        return pool.ClassHash switch
        {
            0x8080468D => [r.Byte(name, p, true)],
            0x8080468B => [r.UShort(name, p, true)],
            0x80804694 => [r.Float(name, p, true)],
            0x80804693 => Enumerable.Range(0, 4).Select(i => r.Float($"{name} {"XYZW"[i]}", p + i * 4, true)).ToArray(),
            0x80804696 or 0x80804692 or 0x80804690 or 0x8080468F => [r.UInt(name, p, true, true)],
            _ => [new(name, $"{pool.Name} {p:X}", p)]
        };
    }
}

/** A layout node's rectangle (X, Y, width, height) and the properties that store it. */
public sealed record CuiLayoutRectangle(CuiNode Node, CuiComponent Component, Vector4 Rectangle,
    IReadOnlyList<CuiProperty> Properties);

/** Layout editing uses the four stored float properties without evaluating native bindings. */
public static class CuiLayout
{
    public const float ReferenceHeight = 1080;

    /** Collects the layout rectangle of every node that has the four layout properties. */
    public static IReadOnlyList<CuiLayoutRectangle> Read(CuiDocument document)
    {
        List<CuiLayoutRectangle> result = [];
        var nodes = document.Nodes.GroupBy(node => node.Id).ToDictionary(group => (ulong)group.Key, group => group.First());
        foreach (var component in document.Components)
        {
            if (!nodes.TryGetValue(component.Key, out var node) || !document.NativeTypes.HasLayout(node.TypeHash, node.AuxiliaryTypeHash)) continue;
            bool defaults = document.NativeTypes.HasVerifiedDefaults(node.TypeHash, node.AuxiliaryTypeHash);
            bool container = document.NativeTypes.IsContainer(node.TypeHash, node.AuxiliaryTypeHash);
            List<CuiProperty> fields = [];
            for (int axis = 0; axis < 4; axis++)
            {
                ulong selector = (ulong)(0x206 + axis);
                var property = component.Properties.LastOrDefault(p => p.Direct && p.ValueClass == 0x80804694
                    && (p.Selector == selector || container && axis >= 2 && p.Selector == (ulong)(0x600 + axis - 2)));
                if (property is not null) fields.Add(property);
                else if (defaults)
                {
                    var size = document.NativeTypes.DefaultSize!.Value;
                    float value = axis == 2 ? size.X : axis == 3 ? size.Y : 0;
                    fields.Add(new(selector, -1, -1, 0x80804694, 184, true,
                        [new(CuiDocument.PropertyName(selector, true), value.ToString("R", CultureInfo.InvariantCulture))]));
                }
            }
            var properties = fields.ToArray();
            if (properties.Length != 4) continue;
            var values = properties.Select(p => float.Parse(p.Fields[0].Value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Any(value => !float.IsFinite(value) || Math.Abs(value) > 10000)) continue;
            for (int i = 0; i < values.Length; i++)
                if (properties[i].PoolDescriptor == 200) values[i] = MathF.Round(values[i] * ReferenceHeight);
            result.Add(new(node, component, new(values[0], values[1], values[2], values[3]), properties));
        }
        return result;
    }

    /** Shared constants are detached inside their typed pool so sibling widgets retain their values. */
    public static byte[] Apply(ReadOnlySpan<byte> bytes, CuiDocument document, CuiLayoutRectangle rectangle, Vector4 pixels)
    {
        if (document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        float[] values = [pixels.X, pixels.Y, pixels.Z, pixels.W];
        float[] previous = [rectangle.Rectangle.X, rectangle.Rectangle.Y, rectangle.Rectangle.Z, rectangle.Rectangle.W];
        if (!Read(document).Any(current => current.Component == rectangle.Component && current.Node == rectangle.Node
            && current.Rectangle == rectangle.Rectangle))
            throw new InvalidDataException("The selected layout does not belong to this resource.");
        var all = document.Properties.ToArray();
        List<CuiPropertyOverride> replacements = [];
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || i >= 2 && values[i] < 0)
                throw new InvalidDataException("Enter finite coordinates and nonnegative dimensions.");
            if (values[i] == previous[i]) continue;
            var property = rectangle.Properties[i];
            if (property.RecordOffset >= 0 && !all.Contains(property) || property.ValueClass != 0x80804694)
                throw new InvalidDataException("The layout field does not belong to this resource.");
            float value = property.PoolDescriptor == 200 ? values[i] / ReferenceHeight : values[i];
            replacements.Add(new(property.Selector, property.PoolDescriptor, BitConverter.GetBytes(value)));
        }
        return CuiPropertyEdits.Upsert(bytes, document, rectangle.Component, replacements);
    }
}
