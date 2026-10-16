using System.Numerics;

namespace SunriseStudio.Scene;

/** A map layer: its name, definition tag and containers. This file holds the layer records and loader part. */
public sealed record MapLayerInfo(int Index, uint NameHash, string Name,
    SceneNameEvidence NameEvidence, uint DefinitionTag, int SourceOffset,
    IReadOnlyList<uint> ContainerTags);

/** A container's visibility mask and the map-global indices it covers. */
public sealed record MapContainerInfo(uint TagHash, ReadOnlyMemory<byte> Mask,
    IReadOnlyList<int> MapGlobalIndices);

/** Layer part of AssetSceneLoader: describes map layers and walks the map containers. */
public sealed partial class AssetSceneLoader
{
    private readonly record struct SceneVisitKey(uint Tag, uint Bubble, Matrix4x4 Transform, uint Entry);
    private SceneVisitKey? activeMapVisit;
    private bool hasMapLayerCatalog;
    private readonly Dictionary<SceneVisitKey, HashSet<SceneVisitKey>> mapVisitChildren = [];
    private readonly Dictionary<SceneVisitKey, MapContainerInfo> mapContainerVisits = [];
    private readonly Dictionary<SceneVisitKey, (int FirstInstance, int EndInstance,
        int FirstMarker, int EndMarker)> mapVisitContent = [];

    /** Layer ordinals and name hashes come from the map definition's typed cells array. */
    public IReadOnlyList<MapLayerInfo> DescribeMapLayers(uint mapRootTag)
    {
        if (Class(mapRootTag) != 0x808091DE)
            throw new ArgumentException("Layer metadata requires a map root.");
        var root = Read(mapRootTag);
        var definitionTag = root.U32(8);
        if (Class(definitionTag) != 0x80809962)
            throw new InvalidDataException("Map layer definition has the wrong class.");
        var definition = Read(definitionTag);
        var rows = definition.Rows(16, 0x808098C4, 24);
        if (rows.Length > 256) throw new InvalidDataException("Map layers exceed the container mask.");
        return rows.Select((row, index) =>
        {
            var hash = definition.U32(row + 8);
            var resolved = ResolveHashName(hash);
            return new MapLayerInfo(index, hash, resolved.Name, resolved.Evidence,
                definitionTag, row, []);
        }).ToArray();
    }

    /** A mission loads each bubble's state layers and every layer in that bubble's row mask. */
    public IReadOnlySet<int> GetScenarioMapIndices(uint mapRootTag, uint scenarioTag)
    {
        if (Class(mapRootTag) != 0x808091DE)
            throw new ArgumentException("Scenario layer presets require a map root.");
        var definitionTag = Read(mapRootTag).U32(8);
        if (Class(definitionTag) != 0x80809962)
            throw new InvalidDataException("Map layer definition has the wrong class.");
        var definition = Read(definitionTag);
        var cells = definition.Rows(16, 0x808098C4, 24);
        var bubbles = definition.Rows(32, 0x808098C2, 80);
        var scenario = DescribeScenario(scenarioTag);
        if (!bubbles.Select(r => definition.U32(r)).SequenceEqual(scenario.Bubbles.Select(b => b.NameHash)))
            throw new ArgumentException("Scenario bubble identities do not match the selected map.");
        var indices = new HashSet<int>();
        for (var ordinal = 0; ordinal < bubbles.Length; ++ordinal)
        {
            if (definition.U32(bubbles[ordinal] + 72) != ordinal)
                throw new InvalidDataException("Map bubble ordinal does not match its array position.");
            foreach (var state in scenario.Bubbles[ordinal].States)
            {
                var index = checked((int)state.MapBubbleIndex);
                if ((uint)index >= cells.Length || index >= 256)
                    throw new ArgumentException("Scenario layer index is absent from the selected map.");
                var owner = definition.U32(cells[index] + 16);
                if (owner == uint.MaxValue)
                {
                    var owners = Enumerable.Range(0, bubbles.Length).Where(i =>
                        (definition.U8(bubbles[i] + 40 + index / 8) & (1 << (index % 8))) != 0).ToArray();
                    if (owners.Length != 1)
                        throw new ArgumentException("Scenario layer has ambiguous map bubble ownership.");
                    owner = (uint)owners[0];
                }
                if (owner != ordinal)
                    throw new ArgumentException("Scenario layer owner does not match the selected map.");
                indices.Add(index);
            }
            // The game loads every container that meets the bubble row's mask at +40, which also holds the
            // bubble's shared and environment layers (atmosphere, time of day); 0x7FF7420E2CF0 reads it.
            if (scenario.Bubbles[ordinal].States.Count == 0) continue;
            for (var index = 0; index < cells.Length && index < 256; ++index)
                if ((definition.U8(bubbles[ordinal] + 40 + index / 8) & (1 << (index % 8))) != 0) indices.Add(index);
        }
        return indices;
    }

    /** Explicit selections use mask intersection; a null selection keeps every container. */
    private void VisitMapContainer(uint tag, TagData data, uint bubble, Matrix4x4 transform, int depth)
    {
        var mask = data.Bytes.AsMemory(8, 32).ToArray();
        var indices = Enumerable.Range(0, 256).Where(i => (mask[i / 8] & (1 << (i % 8))) != 0).ToArray();
        var container = scene.MapContainers.FirstOrDefault(c => c.TagHash == tag);
        if (container == null)
        {
            container = new MapContainerInfo(tag, mask, indices);
            scene.MapContainers.Add(container);
        }
        mapContainerVisits[activeMapVisit!.Value] = container;
        var members = data.Rows(0x28, 0x80808BB0, 4);
        if (options.MapGlobalIndices != null && !indices.Any(options.MapGlobalIndices.Contains))
        {
            foreach (var member in members) ReadUnselectedOcclusionOwner(data.U32(member), bubble);
            return;
        }
        foreach (var member in members)
            Visit(data.U32(member), bubble, transform, depth + 1);
    }

    /** AO dependencies remain available even when their container's geometry is filtered out. */
    private void ReadUnselectedOcclusionOwner(uint tag, uint bubble)
    {
        if (Class(tag) != 0x808099D6) return;
        var data = Read(tag);
        foreach (var row in data.Rows(8, 0x808099D8, 0x90))
        {
            var field = row + 0x78;
            if (data.I64(field) == 0) continue;
            var auxiliary = data.Relative(field);
            if (!data.Has(auxiliary - 4, 0x18) || data.U32(auxiliary - 4) != 0x80806F38) continue;
            var map = data.U32(auxiliary + 0x10);
            if (Class(map) != 0x80807268)
                throw new InvalidDataException("Placed AO dependency has the wrong map class.");
            scene.OcclusionOwners.Add(new NativeOcclusionOwner(bubble, tag, row, map));
        }
    }

    /** Shared graph nodes retain every container route without duplicating geometry. */
    private void AssignMapContainers()
    {
        foreach (var (root, container) in mapContainerVisits)
        {
            var reached = new HashSet<SceneVisitKey>();
            var pending = new Stack<SceneVisitKey>();
            pending.Push(root);
            MapContainerInfo[] single = [container];
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reached.Add(node)) continue;
                if (mapVisitContent.TryGetValue(node, out var range))
                {
                    for (var i = range.FirstInstance; i < range.EndInstance; ++i)
                        scene.Instances[i].MapContainers = Add(scene.Instances[i].MapContainers);
                    for (var i = range.FirstMarker; i < range.EndMarker; ++i)
                        scene.Markers[i].MapContainers = Add(scene.Markers[i].MapContainers);
                }
                if (mapVisitChildren.TryGetValue(node, out var children))
                    foreach (var child in children) pending.Push(child);
            }
            IReadOnlyList<MapContainerInfo> Add(IReadOnlyList<MapContainerInfo> previous)
            {
                if (previous.Count == 0) return single;
                return previous.Any(c => c.TagHash == container.TagHash) ? previous
                    : previous.Append(container).ToArray();
            }
        }
    }

    /** Filtering never discards the available layer catalog. */
    private void FinishMapLayers()
    {
        if (!hasMapLayerCatalog)
            foreach (var index in scene.MapContainers.SelectMany(c => c.MapGlobalIndices).Distinct())
                if (scene.MapLayers.All(l => l.Index != index))
                    scene.MapLayers.Add(new MapLayerInfo(index, 0, $"Map layer {index}",
                        SceneNameEvidence.Unknown, 0, -1, []));
        scene.MapLayers.Sort((a, b) => a.Index.CompareTo(b.Index));
        for (var i = 0; i < scene.MapLayers.Count; ++i)
        {
            var layer = scene.MapLayers[i];
            var resolved = layer.NameHash == 0 ? default : ResolveHashName(layer.NameHash);
            scene.MapLayers[i] = layer with
            {
                Name = layer.NameHash == 0 ? layer.Name : resolved.Name,
                NameEvidence = layer.NameHash == 0 ? layer.NameEvidence : resolved.Evidence,
                ContainerTags = scene.MapContainers.Where(c => c.MapGlobalIndices.Contains(layer.Index))
                    .Select(c => c.TagHash).ToArray()
            };
        }
        ValidateMapSelection();
    }

    private void ValidateMapSelection()
    {
        if (options.MapGlobalIndices == null) return;
        foreach (var index in options.MapGlobalIndices)
            if (index < 0 || index >= 256 || scene.MapLayers.Count != 0
                && scene.MapLayers.All(l => l.Index != index))
                throw new ArgumentException($"Map has no authored layer {index}.");
    }
}
