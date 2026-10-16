namespace SunriseStudio.Formats;

/** CUI views expose stored graph structure without running bindings or guessing layout. */
public sealed class CuiInspector : IAssetInspector
{
    public string Id => "sunrise.cui";
    public string DisplayName => "CUI";
    /** True for CUI widget resources (0x80804825) and hierarchies (0x8080496A). */
    public bool CanInspect(AssetInspectionContext context) =>
        context.ClassHash is 0x80804825 or 0x8080496A;

    /** Inspects a hierarchy as a program of nodes and bindings, and a resource as a widget definition. */
    public AssetInspection Inspect(AssetInspectionContext context) => context.ClassHash == 0x8080496A
        ? Program(context) : Resource(context);

    /** Program edges use node indices; they do not encode pixel coordinates. */
    private static AssetInspection Program(AssetInspectionContext context)
    {
        var r = new TagReader(context.Data);
        var edges = r.Array(8, 0x80804616, 4);
        List<AssetNode> rows = [];
        Dictionary<ushort, AssetGraphNode> nodes = [];
        List<AssetGraphEdge> links = [];
        var parsed = CuiDocument.Read(context.TagHash, context.ClassHash, context.Data);
        var active = CuiHierarchy.ActiveNodes(parsed.Hierarchy);
        for (int i = 0; i < edges.Count; i++)
        {
            int p = edges.Data + i * 4;
            rows.Add(new($"Edge {i}", [r.UShort("Parent", p), r.UShort("Child", p + 2)]));
            ushort parent = r.U16(p), child = r.U16(p + 2);
            if (!active.Contains(child)) continue;
            nodes.TryAdd(child, new($"node:{child}", $"Node {child}", p + 2));
            if (parent == 0x7FFF) continue;
            nodes.TryAdd(parent, new($"node:{parent}", $"Node {parent}", p));
            links.Add(new($"node:{parent}", $"node:{child}", "Child"));
        }
        var header = new AssetNode("Program", [r.UShort("Node count A", 24),
            r.UShort("Node count B", 26), r.UInt("Root resource", 28, hex: true)]);
        uint tag = r.U32(28);
        return new("CUI program", context.Name, [header, new("Hierarchy", [], rows)],
            [new(28, tag, context.ResolveName?.Invoke(tag) ?? $"{tag:X8}")], [])
        { Graph = new(nodes.Values.ToArray(), links) };
    }

    /** Resource arrays are accepted only with the expected element class and complete rows. */
    private static AssetInspection Resource(AssetInspectionContext context)
    {
        var r = new TagReader(context.Data);
        List<AssetNode> sections = [];
        List<AssetReference> references = [];
        Dictionary<string, AssetGraphNode> graphNodes = [];
        List<AssetGraphEdge> graphEdges = [];
        var imports = r.Array(8, 0x80804607, 12);
        List<AssetNode> importRows = [];
        for (int i = 0; i < imports.Count; i++)
        {
            int p = imports.Data + i * 12;
            uint tag = r.U32(p);
            string name = context.ResolveName?.Invoke(tag) ?? $"{tag:X8}";
            references.Add(new(p, tag, name));
            importRows.Add(new(name, [r.UInt("Resource", p, hex: true),
                r.UShort("First node", p + 4), r.UShort("Node count", p + 6),
                r.UShort("Mirror first", p + 8), r.UShort("Mirror count", p + 10)]));
        }
        sections.Add(new("Imports", [], importRows));
        var names = r.Array(24, 0x80804618, 4);
        var types = r.Array(56, 0x80804622, 16);
        var texts = r.Array(232, 0x80804692, 4);
        List<AssetNode> nodes = [];
        for (int i = 0; i < Math.Max(types.Count, names.Count); i++)
        {
            List<AssetProperty> properties = [];
            if (i < names.Count) properties.Add(r.UInt("Name hash", names.Data + i * 4, hex: true));
            string label = $"Node {i}";
            if (i < types.Count)
            {
                int p = types.Data + i * 16;
                label = $"Node {r.U16(p + 10)}";
                properties.AddRange([r.UInt("Type", p, hex: true),
                    r.UInt("Auxiliary type", p + 4, hex: true),
                    r.UShort("Node index", p + 10), r.UInt("Raw flags", p + 12, true, true)]);
            }
            nodes.Add(new(label, properties));
            int nodeIndex = i < types.Count ? r.U16(types.Data + i * 16 + 10) : i;
            string nodeId = $"node:{nodeIndex}";
            graphNodes.TryAdd(nodeId, new(nodeId, label, i < types.Count ? types.Data + i * 16 : -1));
        }
        sections.Add(new("Nodes", [], nodes));
        sections.Add(new("String values", Enumerable.Range(0, texts.Count)
            .Select(i => r.UInt($"String {i}", texts.Data + i * 4, true, true)).ToArray()));
        var bindings = r.Array(88, 0x808046D8, 56);
        List<AssetNode> bindingRows = [];
        for (int i = 0; i < bindings.Count; i++)
        {
            int p = bindings.Data + i * 56;
            string Endpoint(int field)
            {
                ushort first = r.U16(field), last = r.U16(field + 2);
                string id = first == last ? $"node:{first}" : $"range:{first}-{last}";
                graphNodes.TryAdd(id, new(id, first == last ? $"Node {first}" : $"Nodes {first}-{last}", field));
                return id;
            }
            graphEdges.Add(new(Endpoint(p), Endpoint(p + 24), $"{r.U32(p + 16):X} -> {r.U32(p + 40):X}"));
            bindingRows.Add(new($"Binding {i}", [r.UShort("Source first", p),
                r.UShort("Source last", p + 2), r.UInt("Source property", p + 16, hex: true),
                r.UShort("Target first", p + 24), r.UShort("Target last", p + 26),
                r.UInt("Target property", p + 40, hex: true)]));
        }
        sections.Add(new("Bindings", [], bindingRows));
        var document = CuiDocument.Read(context.TagHash, context.ClassHash, context.Data);
        foreach (var item in document.Objects)
        {
            List<AssetNode> operations = [];
            for (int componentIndex = 0; componentIndex < item.Components.Count; componentIndex++)
            {
                var component = item.Components[componentIndex];
                foreach (var property in component.Properties)
                    operations.Add(new($"Node {component.Key} / {CuiDocument.PropertyName(property.Selector)}",
                        [new("Selector", $"0x{property.Selector:X}", property.RecordOffset + 8),
                            new("Path", property.Direct ? "Direct" : "Nested", property.RecordOffset),
                            .. property.Fields.Select(field => field with { CuiOwner = new(item.Index, componentIndex, property.RecordOffset) })]));
            }
            sections.Add(new($"Property set {item.Index + 1}", [], operations));
        }
        return new("CUI resource", context.Name, sections, references, [])
        { Graph = new(graphNodes.Values.ToArray(), graphEdges) };
    }
}
