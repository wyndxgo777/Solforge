using System.Globalization;
using System.Numerics;
using Solforge.Retail;
using Solforge.Retail.Decoding;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** What a retail scene object is: its kind, the retail tag behind it and a short description for the Retail inspector. */
public sealed record RetailPick(string Kind, uint? Tag, string Label, IReadOnlyList<(string Name, string Value)> Fields);

/**
 * StudioWorkspace part: retail (build 245048) bubbles opened read-only in the same viewport as 86657 assets. The document
 * has tag 0 and no bytes, so nothing that edits, reverts or re-reads 86657 tags can reach retail data, and the 86657
 * world and asset paths are untouched; RetailPicks maps each scene object back to the retail tags it came from.
 */
public sealed partial class StudioWorkspace
{
    /** StudioDocument.Kind of a retail view. */
    public const string RetailKind = "retail";
    /** True while the open document is a retail view. */
    public bool RetailDocumentOpen => Document is { Kind: RetailKind };
    /** The decoded retail scene of the open retail view; null otherwise. */
    public RetailScene? RetailView { get; private set; }
    /** Scene object (SceneMarker or MeshInstance) to its retail description, for the open retail view. */
    public IReadOnlyDictionary<object, RetailPick> RetailPicks { get; private set; } = new Dictionary<object, RetailPick>();

    /**
     * Opens a retail bubble in the viewport on the worker (closing an open 86657 map first, since a world's scene takes the
     * viewport). Markers for every node, untextured terrain and static meshes; nothing is written anywhere.
     */
    public void OpenRetailBubble(RetailSource source, uint bubble, RetailSceneOptions options)
    {
        RequireIdle();
        if (World is not null) CloseWorld();
        BeginWork($"Opening retail bubble 0x{bubble:X8}", () =>
        {
            var token = OperationToken;
            var retail = RetailSceneBuilder.Build(source, bubble, options, out string? problem, ReportProgress, token)
                ?? throw new InvalidDataException(problem ?? $"Retail bubble 0x{bubble:X8} did not decode.");
            ReportProgress("Building the retail view");
            var (scene, picks) = RetailSceneConverter.ToScene(retail, token);
            var document = new StudioDocument(new PackageEntry(0, RetailMap.BubbleParentClass, 16, 0, 0,
                    "retail " + RetailOptions.Build, 0, 0),
                "Retail: " + retail.Name, [], RetailSceneConverter.Inspection(retail, scene), scene, null, "")
            { Kind = RetailKind };
            return () =>
            {
                Document = document;
                RetailView = retail;
                RetailPicks = picks;
                AdoptNative(null, scene);
                NativeError = "";
                SceneRevision++; DocumentRevision++;
                Status = $"Retail {retail.Name}: {scene.Markers.Count:N0} markers, {scene.Instances.Count:N0} mesh instances";
            };
        });
    }
}

/** RetailScene to SceneDocument: markers by component kind, meshes with computed normals and no materials. */
public static class RetailSceneConverter
{
    /** Marker colours by component kind (RGBA). */
    private static Vector4 ColorOf(string kind) => kind switch
    {
        "statics" => new(0.75f, 0.75f, 0.78f, 1), "terrain" => new(0.45f, 0.7f, 0.35f, 1),
        "light" or "shadowing_light" or "sun" => new(1f, 0.85f, 0.3f, 1), "respawn_point" => new(0.3f, 0.6f, 1f, 1),
        "decals" or "road_decals" => new(0.8f, 0.5f, 0.9f, 1), "audio" => new(0.3f, 0.9f, 0.9f, 1),
        "water" => new(0.2f, 0.45f, 0.95f, 1), _ => new(1f, 0.55f, 0.25f, 1)
    };

    private static SceneLayer LayerOf(string kind) => kind switch
    {
        "light" or "shadowing_light" or "sun" => SceneLayer.Lights,
        "respawn_point" => SceneLayer.Spawns,
        "decals" or "road_decals" or "audio" or "decorators" or "sky" or "cubemap" or "umbra" or "ambient_occlusion" or "water" => SceneLayer.Effects,
        _ => SceneLayer.Objects
    };

    private static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    /** The scene and its retail picks. */
    public static (SceneDocument Scene, Dictionary<object, RetailPick> Picks) ToScene(RetailScene retail, CancellationToken cancel)
    {
        var scene = new SceneDocument { Name = "Retail " + retail.Name, TagHash = 0 };
        scene.Bubbles.Add(new BubbleInfo(retail.Bubble, retail.Bubble, retail.Name));
        var picks = new Dictionary<object, RetailPick>(ReferenceEqualityComparer.Instance);
        foreach (var marker in retail.Markers)
        {
            string name = $"{marker.Kind} {Hex(marker.Table)}[{marker.NodeIndex}]";
            var item = new SceneMarker
            {
                Name = name, Layer = LayerOf(marker.Kind), Position = marker.Position, Rotation = marker.Rotation, Extents = marker.HalfExtents,
                Color = ColorOf(marker.Kind), BubbleHash = retail.Bubble, Category = "Retail " + marker.Kind, Semantic = marker.Kind,
                Tags = ["retail", .. marker.Kinds]
            };
            scene.Markers.Add(item);
            picks[item] = new RetailPick(marker.Kind, marker.Entity, name,
            [
                ("Node table", Hex(marker.Table)), ("Node", marker.NodeIndex.ToString(CultureInfo.InvariantCulture)),
                ("Entity", marker.Entity is { } entity ? Hex(entity) : "none"), ("Components", string.Join(", ", marker.Kinds)),
                ("World id", "0x" + marker.WorldId.ToString("X16", CultureInfo.InvariantCulture)),
                ("Position", $"{marker.Position.X:0.##}, {marker.Position.Y:0.##}, {marker.Position.Z:0.##}")
            ]);
        }
        foreach (var mesh in retail.Meshes)
        {
            cancel.ThrowIfCancellationRequested();
            if (mesh.Mesh.Positions.Length == 0 || mesh.Mesh.Indices.Length < 3) continue;
            bool terrain = mesh.Mesh.Kind == "terrain";
            var data = ToMesh(mesh.Mesh, terrain);
            string label = $"Retail {mesh.Mesh.Kind} {Hex(mesh.Source)}";
            var pick = new RetailPick(mesh.Mesh.Kind, mesh.Source, label,
            [
                ("Tag", Hex(mesh.Source)), ("Vertices", mesh.Mesh.Positions.Length.ToString("N0", CultureInfo.InvariantCulture)),
                ("Triangles", mesh.Mesh.Triangles.ToString("N0", CultureInfo.InvariantCulture)),
                ("Instances", mesh.Transforms.Count.ToString("N0", CultureInfo.InvariantCulture)), ("Notes", string.Join(" ", mesh.Mesh.Notes.Take(3)))
            ]);
            foreach (var transform in mesh.Transforms)
            {
                var instance = new MeshInstance
                {
                    Mesh = data, Transform = transform, Name = label, Layer = terrain ? SceneLayer.Terrain : SceneLayer.Geometry,
                    BubbleHash = retail.Bubble, Category = terrain ? "Retail terrain" : "Retail static", Tags = ["retail", mesh.Mesh.Kind]
                };
                scene.Instances.Add(instance);
                picks[instance] = pick;
            }
        }
        scene.Diagnostics.AddRange(retail.Notes);
        return (scene, picks);
    }

    /** Vertices with smooth normals from the triangles; colour by kind; tag 0 so no 86657 material or texture is looked up. */
    private static MeshData ToMesh(RetailMesh mesh, bool terrain)
    {
        var normals = new Vector3[mesh.Positions.Length];
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            if (a >= normals.Length || b >= normals.Length || c >= normals.Length) continue;
            var face = Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += face; normals[b] += face; normals[c] += face;
        }
        var color = terrain ? new Vector4(0.45f, 0.6f, 0.38f, 1) : new Vector4(0.72f, 0.72f, 0.75f, 1);
        var vertices = new SceneVertex[mesh.Positions.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var normal = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
            vertices[i] = new SceneVertex(mesh.Positions[i], normal, Vector2.Zero, color);
        }
        uint count = (uint)vertices.Length;
        uint[] indices = mesh.Indices.All(index => index < count) ? mesh.Indices
            : mesh.Indices.Chunk(3).Where(tri => tri.Length == 3 && tri.All(index => index < count)).SelectMany(tri => tri).ToArray();
        return new MeshData { TagHash = 0, Vertices = vertices, Indices = indices, MaterialTag = 0 };
    }

    /** The asset inspector's view of a retail scene: what was decoded, what renders and what does not. */
    public static AssetInspection Inspection(RetailScene retail, SceneDocument scene)
    {
        static AssetProperty P(string name, string value) => new(name, value);
        var summary = new List<AssetProperty>
        {
            P("Build", RetailOptions.Build + " (retail, read-only)"), P("Bubble", Hex(retail.Bubble)), P("Name", retail.Name),
            P("Markers", scene.Markers.Count.ToString("N0", CultureInfo.InvariantCulture)),
            P("Mesh instances", scene.Instances.Count.ToString("N0", CultureInfo.InvariantCulture)),
            P("Distinct meshes", retail.Meshes.Count.ToString("N0", CultureInfo.InvariantCulture)),
            P("Triangles (distinct)", retail.Meshes.Sum(mesh => (long)mesh.Mesh.Triangles).ToString("N0", CultureInfo.InvariantCulture))
        };
        var stats = retail.Stats.Select(pair => P(pair.Key, pair.Value.ToString("N0", CultureInfo.InvariantCulture))).ToList();
        var renders = new List<AssetProperty>
        {
            P("Drawn", "Node markers by component kind; terrain and static mesh triangles (untextured, computed normals)"),
            P("Not drawn", "Textures, materials, entity models, dynamic objects, decals, water, sky, lighting, collision"),
            P("Verified", "Node transforms, static instance transforms (inside the map's bounds), terrain winding"),
            P("Editing", "None: retail views are read-only; port with the retail_* tools")
        };
        return new AssetInspection("Retail bubble", "Retail " + retail.Name,
            [new AssetNode("Retail view", summary), new AssetNode("Decode statistics", stats), new AssetNode("What renders", renders)],
            [], retail.Notes);
    }
}
