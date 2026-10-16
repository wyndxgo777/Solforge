using System.Numerics;

namespace SunriseStudio.Scene;

/** Builder part of SceneHierarchy: assembles the ownership tree from a scene. */
public sealed partial class SceneHierarchy
{
    private readonly record struct PlacementKey(object? Identity, uint Tag, int Row, int Kind,
        uint Bubble, uint Mission, int State, Matrix4x4 Transform);

    /** Multiple container routes stay explicit instead of assigning an arbitrary single owner. */
    private void BuildNodes(WorldDocument world)
    {
        string prefix = $"scene:{world.Map.Tag:X8}";
        Root = Add(prefix, world.Name, SceneHierarchyKind.Root, null);
        foreach (var bubble in world.Scene.Bubbles.GroupBy(x => world.CanonicalBubble(x.TagHash)))
            Add($"{prefix}:bubble:{bubble.Key:X8}", bubble.First().Name, SceneHierarchyKind.Bubble, Root);
        var authored = new Dictionary<AuthoredSlotIdentity, List<(WorldObject Row, SceneHierarchyNode Node)>>();
        var placements = new Dictionary<PlacementKey, List<WorldObject>>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        string Unique(string key)
        {
            int ordinal = occurrences.GetValueOrDefault(key);
            occurrences[key] = ordinal + 1;
            return ordinal == 0 ? key : $"{key}#{ordinal}";
        }
        SceneHierarchyNode Scope(WorldObject row)
        {
            string bubbleId = $"{prefix}:bubble:{row.BubbleHash:X8}";
            var bubble = Add(bubbleId, row.BubbleName, SceneHierarchyKind.Bubble, Root);
            var scope = row.MissionTag == 0
                ? Add($"{bubbleId}:map", "Map", SceneHierarchyKind.Map, bubble)
                : Add($"{bubbleId}:mission:{row.MissionTag:X8}",
                    world.MissionScene?.Name ?? $"Mission {row.MissionTag:X8}",
                    SceneHierarchyKind.Mission, bubble);
            if (row.StateOrdinal >= 0)
                scope = Add($"{scope.Id}:state:{row.StateOrdinal}",
                    row.StateName.Length == 0 ? $"State {row.StateOrdinal}" : row.StateName,
                    SceneHierarchyKind.State, scope);
            return scope;
        }
        SceneHierarchyNode ContainerParent(WorldObject row, IReadOnlyList<MapContainerInfo> containers)
        {
            var parent = Scope(row);
            uint[] tags = containers.Select(x => x.TagHash).Distinct().Order().ToArray();
            if (tags.Length == 0) return parent;
            string key = string.Join("-", tags.Select(x => x.ToString("X8")));
            var names = containers.SelectMany(x => x.MapGlobalIndices).Distinct().Order()
                .Select(i => world.Scene.MapLayers.FirstOrDefault(x => x.Index == i)?.Name)
                .OfType<string>().Distinct().ToArray();
            string label = tags.Length > 1 ? "Shared containers"
                : names.Length == 0 ? $"Container {tags[0]:X8}" : string.Join(" / ", names);
            var node = Add($"{parent.Id}:containers:{key}", label,
                tags.Length == 1 ? SceneHierarchyKind.Container : SceneHierarchyKind.SharedContainers, parent);
            node.ContainerTags = tags;
            return node;
        }
        SceneHierarchyNode? AuthoredParent(IEnumerable<WorldObject> rows)
        {
            SceneHierarchyNode? parent = null;
            foreach (var row in rows)
            {
                var slot = row.Source switch
                {
                    SceneMarker marker => marker.Slot,
                    AuthoredSquadInfo squad => squad.Slot,
                    _ => null
                };
                if (slot is null || !authored.TryGetValue(slot, out var owners)) continue;
                var matches = owners.Where(owner => owner.Row.MissionTag == row.MissionTag
                    && owner.Row.BubbleHash == row.BubbleHash && owner.Row.StateOrdinal == row.StateOrdinal
                    && (owner.Row.Owners.Count == 0 || owner.Row.Owners.Any(state =>
                        state.MissionTag == row.MissionTag && state.BubbleHash == row.BubbleHash
                        && state.Ordinal == row.StateOrdinal))).Select(x => x.Node).Distinct().ToArray();
                if (matches.Length != 1 || parent is not null && !ReferenceEquals(parent, matches[0])) return null;
                parent = matches[0];
            }
            return parent;
        }
        foreach (var row in world.Objects.Where(x => x.Kind == WorldObjectKind.State))
            Associate(Scope(row), row);
        foreach (var row in world.Objects.Where(x => x.Source is AuthoredObjectInfo))
        {
            var node = Add($"{prefix}:{row.Id}", row.Name, SceneHierarchyKind.AuthoredObject, Scope(row));
            Associate(node, row);
            foreach (var slot in ((AuthoredObjectInfo)row.Source).Slots)
            {
                if (!authored.TryGetValue(slot.Identity, out var owners)) authored.Add(slot.Identity, owners = []);
                owners.Add((row, node));
            }
        }
        foreach (var row in world.Objects.Where(x => x.Source is MeshInstance or SceneMarker))
        {
            var key = Key(row);
            if (!placements.TryGetValue(key, out var members)) placements.Add(key, members = []);
            members.Add(row);
        }
        foreach (var (key, rows) in placements)
        {
            var representative = rows.FirstOrDefault(x => x.Source is MeshInstance) ?? rows[0];
            var containers = rows.SelectMany(row => row.Source switch
            {
                MeshInstance mesh => mesh.MapContainers,
                SceneMarker marker => marker.MapContainers,
                _ => Array.Empty<MapContainerInfo>()
            }).GroupBy(x => x.TagHash).Select(x => x.First()).ToArray();
            string identity = key.Identity is SceneObjectPlacement placed
                ? $"placed:{placed.SourceTag:X8}:{placed.SourceIndex}"
                : key.Tag != 0 ? $"source:{key.Kind}:{key.Tag:X8}:{key.Row}"
                : $"row:{representative.Id}";
            string id = Unique($"{prefix}:placement:{key.Bubble:X8}:{key.Mission:X8}:{key.State}:{identity}");
            var parent = AuthoredParent(rows) ?? ContainerParent(representative, containers);
            var node = Add(id, representative.Name,
                rows.Any(x => x.Source is MeshInstance) ? SceneHierarchyKind.Placement : SceneHierarchyKind.Marker, parent);
            node.Representative = representative;
            node.ContainerTags = containers.Select(x => x.TagHash).Distinct().Order().ToArray();
            foreach (var row in rows) Associate(node, row);
        }
        foreach (var row in world.Objects.Where(x => x.Source is WorldLight or AuthoredSquadInfo))
        {
            var ownerMarker = (row.Source as WorldLight)?.Owner.OwnerMarker;
            var parent = ownerMarker is not null && bySource.TryGetValue(ownerMarker, out var placed)
                ? placed : AuthoredParent([row]) ?? Scope(row);
            var node = Add($"{prefix}:{row.Id}", row.Name,
                row.Source is WorldLight ? SceneHierarchyKind.Light : SceneHierarchyKind.Squad, parent);
            Associate(node, row);
        }
        Finish();
    }

    /** Reference identities group decoded parts; fallback sources also need their full transform. */
    private static PlacementKey Key(WorldObject row)
    {
        if (row.Source is SceneMarker marker)
            return new(marker.ObjectPlacement ?? (object)marker, 0, 0, 0,
                marker.BubbleHash, row.MissionTag, row.StateOrdinal, default);
        var mesh = (MeshInstance)row.Source;
        if (mesh.ObjectPlacement is { } placement)
            return new(placement, 0, 0, 0, mesh.BubbleHash, row.MissionTag, row.StateOrdinal, default);
        var key = new PlacementKey(null, 0, 0, 0, mesh.BubbleHash, row.MissionTag,
            row.StateOrdinal, mesh.Transform);
        if (mesh.PlacementSourceTag != 0 && mesh.PlacementSourceOffset >= 0)
            return key with { Kind = 1, Tag = mesh.PlacementSourceTag, Row = mesh.PlacementSourceOffset };
        if (mesh.InstanceSourceTag != 0 && mesh.InstanceSourceIndex >= 0)
            return key with { Kind = 2, Tag = mesh.InstanceSourceTag, Row = mesh.InstanceSourceIndex };
        if (mesh.ObjectClassTag != 0) return key with { Kind = 3, Tag = mesh.ObjectClassTag };
        if (mesh.Mesh.TagHash != 0) return key with { Kind = 4, Tag = mesh.Mesh.TagHash,
            Row = mesh.Layer == SceneLayer.Volumes ? mesh.Mesh.PartIndex : 0 };
        return key with { Identity = mesh };
    }
}
