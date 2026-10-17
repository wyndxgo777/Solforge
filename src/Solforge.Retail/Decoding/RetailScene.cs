/**
 * File: A retail bubble as viewport-ready data: one marker per map node (placement) and untextured static/terrain meshes
 * with their instance transforms. Plain System.Numerics types so it needs no renderer; the editor's Retail workspace
 * turns it into a SceneDocument. What renders: node markers, static meshes (opaque GenerateGbuffer parts, highest
 * detail) and terrain (detail level 0). What does not: textures, materials, entity models, decorators, water, sky,
 * decals and lights (markers only).
 */
using System.Numerics;

namespace Solforge.Retail.Decoding;

/** A marker for one map node. Entity is the node's entity tag; Kinds lists its component kinds. */
public sealed record RetailSceneMarker(Vector3 Position, Quaternion Rotation, Vector3 HalfExtents, string Kind, IReadOnlyList<string> Kinds,
    uint? Entity, uint Table, int NodeIndex, ulong WorldId);

/** A mesh and every world transform it is drawn with (terrain has the identity once). */
public sealed record RetailSceneMesh(RetailMesh Mesh, IReadOnlyList<Matrix4x4> Transforms, uint Source);

/** Limits for one scene build. */
public sealed record RetailSceneOptions
{
    public bool Statics { get; init; } = true;
    public bool Terrain { get; init; } = true;
    public int MaxNodes { get; init; } = 100_000;
    /** Distinct static meshes decoded; the rest are counted in Notes. */
    public int MaxStaticMeshes { get; init; } = 6_000;
    public int MaxInstances { get; init; } = 250_000;
}

/** A decoded bubble ready for a viewport. */
public sealed record RetailScene(uint Bubble, string Name, IReadOnlyList<RetailSceneMarker> Markers, IReadOnlyList<RetailSceneMesh> Meshes,
    IReadOnlyList<string> Notes, IReadOnlyDictionary<string, int> Stats);

/** Builds RetailScene from a bubble. */
public static class RetailSceneBuilder
{
    /** Decodes bubble (an SBubbleParent or SBubbleDefinition) with geometry per options; progress gets short status lines. */
    public static RetailScene? Build(RetailSource source, uint bubble, RetailSceneOptions options, out string? problem,
        Action<string>? progress = null, CancellationToken cancel = default)
    {
        progress?.Invoke("Reading bubble " + RetailTag.Hex(bubble));
        var map = RetailMap.ReadBubble(source, bubble, out problem, options.MaxNodes);
        if (map is null) return null;
        var notes = new List<string>(map.Notes);
        if (map.Truncated) notes.Add($"Only the first {options.MaxNodes} nodes were read.");
        var markers = new List<RetailSceneMarker>(map.Nodes.Count);
        foreach (var node in map.Nodes)
        {
            float size = node.Kind switch { "statics" or "terrain" => 2f, "light" or "shadowing_light" => 0.5f, _ => 0.75f };
            markers.Add(new RetailSceneMarker(node.Translation, Quaternion.Normalize(node.Rotation.LengthSquared() > 1e-6f ? node.Rotation : Quaternion.Identity),
                new Vector3(size), node.Kind, node.Components.Select(component => component.Kind).Distinct().ToList(), node.Entity, node.Table, node.Index, node.WorldId));
        }
        var meshes = new List<RetailSceneMesh>();
        var stats = new Dictionary<string, int>
        {
            ["containers"] = map.Containers.Count, ["node_tables"] = map.Tables.Count, ["nodes"] = map.Nodes.Count,
        };
        foreach (var (kind, count) in map.KindCounts) stats["components_" + kind] = count;
        var componentTags = map.Nodes.SelectMany(node => node.Components).Where(component => component.DataTag is not null).ToList();
        if (options.Terrain)
        {
            var terrains = componentTags.Where(component => component.Class == RetailMap.TerrainComponent).Select(component => component.DataTag!.Value).Distinct().ToList();
            int done = 0;
            foreach (uint terrain in terrains)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke($"Terrain {++done}/{terrains.Count}");
                if (RetailGeometry.ReadTerrain(source, terrain, out string? why) is { } mesh) meshes.Add(new RetailSceneMesh(mesh, [Matrix4x4.Identity], terrain));
                else notes.Add(why ?? $"Terrain {RetailTag.Hex(terrain)} failed.");
            }
            stats["terrain_meshes"] = meshes.Count;
        }
        if (options.Statics)
        {
            var wrappers = componentTags.Where(component => component.Class == RetailMap.StaticsComponent).Select(component => component.DataTag!.Value).Distinct().ToList();
            var transforms = new Dictionary<uint, List<Matrix4x4>>();
            int instances = 0, skippedInstances = 0, wrapperIndex = 0;
            foreach (uint wrapper in wrappers)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke($"Static instances {++wrapperIndex}/{wrappers.Count}");
                if (RetailGeometry.ReadStaticInstances(source, wrapper, out string? why) is not { } set) { notes.Add(why ?? $"Statics {RetailTag.Hex(wrapper)} failed."); continue; }
                notes.AddRange(set.Notes.Take(5));
                foreach (var placement in set.Placements)
                {
                    if (instances + placement.Transforms.Count > options.MaxInstances) { skippedInstances += placement.Transforms.Count; continue; }
                    if (!transforms.TryGetValue(placement.Static, out var list)) transforms[placement.Static] = list = [];
                    list.AddRange(placement.Transforms);
                    instances += placement.Transforms.Count;
                }
            }
            int decoded = 0, failed = 0, skippedMeshes = 0, meshIndex = 0;
            foreach (var (staticTag, list) in transforms.OrderByDescending(pair => pair.Value.Count))
            {
                cancel.ThrowIfCancellationRequested();
                if (++meshIndex % 50 == 0) progress?.Invoke($"Static meshes {meshIndex}/{transforms.Count}");
                if (decoded >= options.MaxStaticMeshes) { skippedMeshes++; continue; }
                if (RetailGeometry.ReadStaticMesh(source, staticTag, out string? why) is { } mesh) { meshes.Add(new RetailSceneMesh(mesh, list, staticTag)); decoded++; }
                else { failed++; if (failed <= 10) notes.Add(why ?? $"Static {RetailTag.Hex(staticTag)} failed."); }
            }
            stats["static_instance_sets"] = wrappers.Count; stats["static_meshes"] = decoded; stats["static_instances"] = instances;
            if (failed > 0) { stats["static_meshes_failed"] = failed; notes.Add($"{failed} static meshes did not decode (first 10 listed)."); }
            if (skippedMeshes > 0) notes.Add($"{skippedMeshes} static meshes over the {options.MaxStaticMeshes} cap were skipped.");
            if (skippedInstances > 0) notes.Add($"{skippedInstances} static instances over the {options.MaxInstances} cap were skipped.");
        }
        string name = map.NameHash != 0 ? $"Retail bubble {RetailTag.Hex(bubble)} (name 0x{map.NameHash:X8})" : $"Retail bubble {RetailTag.Hex(bubble)}";
        return new RetailScene(bubble, name, markers, meshes, notes, stats);
    }
}
