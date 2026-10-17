/**
 * File: Retail map structure: SBubbleParent 0x8080891E -> SBubbleDefinition 0x80808701 -> SMapContainer 0x80808707
 * -> SMapNodeTable 0x80809883 -> nodes 0x80809885 (rotation, translation, entity, component list). VERIFIED on the
 * schism 'backyard' bubble 0x80E523F3 (24 containers, 932 nodes, every quaternion unit length, every entity resolved).
 */
using System.Numerics;

namespace Solforge.Retail.Decoding;

/** One component on a node: its class, its offset in the node table bytes and the first tag it names (when one). */
public sealed record RetailComponent(uint Class, int DataOffset, uint? DataTag)
{
    public string Kind => RetailMap.ComponentKind(Class);
}

/** One map node (placement). */
public sealed record RetailMapNode(uint Table, int Index, Quaternion Rotation, Vector3 Translation, float TranslationW, uint? Entity, ulong WorldId,
    IReadOnlyList<RetailComponent> Components)
{
    /** The most specific component kind for display (statics, terrain, light...), or "entity". */
    public string Kind => Components.Select(component => component.Kind).FirstOrDefault(kind => kind != "other") ?? "entity";
}

/** A bubble: its parent tag, name hash, definition, containers and every node of its node tables. */
public sealed record RetailBubble(uint Parent, uint NameHash, uint? Definition, IReadOnlyList<uint> Containers, IReadOnlyList<uint> Tables,
    IReadOnlyList<RetailMapNode> Nodes, IReadOnlyList<string> Notes, bool Truncated)
{
    /** Node count per component kind. */
    public IReadOnlyDictionary<string, int> KindCounts => Nodes.SelectMany(node => node.Components.Select(component => component.Kind))
        .GroupBy(kind => kind).OrderByDescending(group => group.Count()).ToDictionary(group => group.Key, group => group.Count());
}

/** Decodes bubbles and their nodes. */
public static class RetailMap
{
    public const uint BubbleParentClass = 0x8080891E, BubbleDefinitionClass = 0x80808701, ContainerClass = 0x80808707,
        NodeTableClass = 0x80809883, NodeClass = 0x80809885;
    public const uint StaticsComponent = 0x80806CC9, TerrainComponent = 0x80806C7D;
    private const int NodeStride = 0x90;

    /** Component classes by kind (names from alkahest data/map.rs; the counts were seen in the dump). */
    public static readonly IReadOnlyDictionary<uint, string> ComponentKinds = new Dictionary<uint, string>
    {
        [0x80806CC9] = "statics", [0x80806C7D] = "terrain", [0x80806A63] = "light", [0x80806C5E] = "shadowing_light",
        [0x80806955] = "decals", [0x808068E8] = "road_decals", [0x80806AA3] = "sky", [0x80806CC3] = "decorators",
        [0x80808CB5] = "respawn_point", [0x8080666D] = "audio", [0x8080666F] = "audio", [0x808068D4] = "water",
        [0x80806A71] = "sun", [0x80806CF1] = "umbra", [0x80806A40] = "ambient_occlusion", [0x80806695] = "cubemap",
    };

    public static string ComponentKind(uint componentClass) => ComponentKinds.GetValueOrDefault(componentClass, "other");

    /**
     * Reads a bubble from an SBubbleParent (or straight from an SBubbleDefinition). maxNodes caps the nodes read
     * (Truncated says so); the containers and tables are always listed.
     */
    public static RetailBubble? ReadBubble(RetailSource source, uint tag, out string? problem, int maxNodes = 200_000)
    {
        problem = null;
        if (source.Read(tag) is not { } bytes) { problem = $"{RetailTag.Hex(tag)} has no payload."; return null; }
        var notes = new List<string>();
        uint parent = tag, nameHash = 0;
        uint? definition;
        // An SBubbleParent has a WideHash at +8; an SBubbleDefinition has a container array there.
        if (RetailBytes.TryArray(bytes, 0x08, out var direct) && direct.Count > 0 && direct.Class == 0x80808703) definition = tag;
        else
        {
            nameHash = RetailBytes.U32(bytes, 0x20);
            definition = source.Wide(bytes, 0x08);
            if (definition is null) { problem = $"{RetailTag.Hex(tag)}: the bubble definition reference does not resolve (64-bit hash missing from the lookup cache?)."; return null; }
        }
        if (source.Read(definition.Value) is not { } definitionBytes) { problem = $"Bubble definition {RetailTag.Hex(definition.Value)} has no payload."; return null; }
        if (!RetailBytes.TryArray(definitionBytes, 0x08, out var containerArray))
        { problem = $"{RetailTag.Hex(definition.Value)} has no container array at +8 (not an SBubbleDefinition 0x80808701?)."; return null; }
        var containers = new List<uint>();
        var tables = new List<uint>();
        var nodes = new List<RetailMapNode>();
        bool truncated = false;
        for (int i = 0; i < containerArray.Count; i++)
        {
            if (source.Wide(definitionBytes, containerArray.Data + i * 16) is not { } container) { notes.Add($"Container {i} does not resolve."); continue; }
            containers.Add(container);
            if (source.Read(container) is not { } containerBytes) { notes.Add($"Container {RetailTag.Hex(container)} has no payload."); continue; }
            if (!RetailBytes.TryArray(containerBytes, 0x28, out var tableArray)) { notes.Add($"Container {RetailTag.Hex(container)} has no table array at +0x28."); continue; }
            for (int k = 0; k < tableArray.Count; k++)
            {
                uint table = RetailBytes.U32(containerBytes, tableArray.Data + k * 4);
                tables.Add(table);
                if (nodes.Count >= maxNodes) { truncated = true; continue; }
                if (source.Read(table) is not { } tableBytes) { notes.Add($"Node table {RetailTag.Hex(table)} has no payload."); continue; }
                ReadNodes(source, table, tableBytes, nodes, maxNodes, ref truncated);
            }
        }
        return new RetailBubble(parent, nameHash, definition, containers, tables, nodes, notes, truncated);
    }

    /** Appends the nodes of one SMapNodeTable; tables whose array holds another class are skipped. */
    public static void ReadNodes(RetailSource source, uint table, byte[] bytes, List<RetailMapNode> nodes, int maxNodes, ref bool truncated)
    {
        if (!RetailBytes.TryArray(bytes, 0x08, out var array) || array.Count == 0 || array.Class != NodeClass) return;
        for (int i = 0; i < array.Count; i++)
        {
            if (nodes.Count >= maxNodes) { truncated = true; return; }
            int at = array.Data + i * NodeStride;
            if (!RetailBytes.Has(bytes, at, NodeStride)) return;
            var rotation = new Quaternion(RetailBytes.F32(bytes, at), RetailBytes.F32(bytes, at + 4), RetailBytes.F32(bytes, at + 8), RetailBytes.F32(bytes, at + 12));
            var translation = new Vector3(RetailBytes.F32(bytes, at + 16), RetailBytes.F32(bytes, at + 20), RetailBytes.F32(bytes, at + 24));
            nodes.Add(new RetailMapNode(table, i, rotation, translation, RetailBytes.F32(bytes, at + 28), source.Wide(bytes, at + 0x28),
                RetailBytes.U64(bytes, at + 0x70), Components(bytes, at + 0x78)));
        }
    }

    /** Walks the component list from the pointer at field: class at p+off-4, data at p+off+0x10, next pointer at p+off. */
    public static IReadOnlyList<RetailComponent> Components(ReadOnlySpan<byte> bytes, int field)
    {
        var result = new List<RetailComponent>();
        int pointer = field;
        for (int guard = 0; guard < 256; guard++)
        {
            long offset = RetailBytes.I64(bytes, pointer);
            if (offset == 0 || offset == long.MaxValue) break;
            long next = pointer + offset;
            if (next < 4 || next + 0x14 > bytes.Length) break;
            int at = (int)next;
            uint componentClass = RetailBytes.U32(bytes, at - 4);
            int data = at + 0x10;
            uint first = RetailBytes.U32(bytes, data);
            uint? dataTag = componentClass == TerrainComponent ? RetailBytes.U32(bytes, data + 8) : first;
            result.Add(new RetailComponent(componentClass, data, dataTag is { } value && RetailTag.Plausible(value) ? value : null));
            pointer = at;
        }
        return result;
    }

    /** The bubble parents an activity's phases reference, de-duplicated in order. */
    public static IReadOnlyList<uint> BubblesOf(RetailActivityInfo activity) =>
        activity.Phases.SelectMany(phase => phase.BubbleParents).Distinct().ToList();
}
