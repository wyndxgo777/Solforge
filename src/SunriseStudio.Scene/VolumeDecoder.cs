using System.Numerics;

namespace SunriseStudio.Scene;

/** Volume part of AssetSceneLoader: builds trigger and class volumes as scene geometry. */
public sealed partial class AssetSceneLoader
{
    private readonly HashSet<(uint Tag, uint Bubble)> volumeConfigs = [];

    /** Trigger roots are reached through the exact placed-class construction rows. */
    private void ReadClassVolumes(uint classTag, uint bubble, string label)
    {
        if (Class(classTag) != 0x80809C0F) return;
        var definition = Read(classTag);
        foreach (var build in definition.Rows(0x10, 0x80809C04, 12))
            ReadConfigVolumes(definition.U32(build), bubble, label);
    }

    /** Config component rows name the trigger root through a signed relative header link. */
    private void ReadConfigVolumes(uint configTag, uint bubble, string label)
    {
        if (Class(configTag) != 0x80809C36 || !volumeConfigs.Add((configTag, bubble))) return;
        var config = Read(configTag);
        foreach (var component in config.Rows(0x30, 0x808091A4, 16))
        {
            if (config.U32(component + 8) != 0x808099C8) continue;
            var header = checked(component + config.I32(component));
            if (!config.Has(header, 16) || config.U32(header) != configTag || config.U32(header + 4) != 0x808099C9)
                throw new InvalidDataException("Trigger component header does not match its config.");
            var root = checked((int)config.U64(header + 8));
            foreach (var row in config.Rows(root + 0x38, 0x808099D0, 0x120))
            {
                var box = TriggerBox(config, row);
                var name = $"{label} trigger {config.U16(row + 0x12)}";
                var rotation = config.Q4(row + 0x30);
                var position = config.V3(row + 0x40);
                if (position.LengthSquared() > 1e-10f || MathF.Abs(rotation.W - 1) > 1e-5f
                    || new Vector3(rotation.X, rotation.Y, rotation.Z).LengthSquared() > 1e-10f)
                {
                    scene.UnsupportedCount++;
                    AddDiagnostic($"Trigger {name} has an unsupported non-identity placement.");
                    continue;
                }
                var objectPlacement = new SceneObjectPlacement(configTag, row, Matrix4x4.Identity);
                scene.Markers.Add(new SceneMarker
                {
                    ObjectPlacement = objectPlacement,
                    Name = name,
                    SourceTag = configTag,
                    SourceOffset = row,
                    Slot = new AuthoredSlotIdentity(config.U32(row + 0x0C), config.U8(row + 0x10), config.U16(row + 0x12)),
                    Semantic = "trigger volume",
                    TagHash = configTag,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Volumes,
                    Position = box.Position,
                    Extents = box.Extents,
                    Color = new Vector4(0.8f, 0.3f, 1, 0.7f)
                });
                var points = config.Rows(row + 0xD0, 0x80800094, 16);
                var triangles = config.Rows(row + 0xE0, 0x80809B92, 3);
                var vertices = points.Select(p => new SceneVertex(config.V3(p), Vector3.UnitZ, Vector2.Zero, new Vector4(0.8f, 0.3f, 1, 0.35f))).ToArray();
                var indices = triangles.SelectMany(p => new uint[] { config.U8(p), config.U8(p + 1), config.U8(p + 2) }).ToArray();
                if (indices.Any(i => i >= vertices.Length)) throw new InvalidDataException("Trigger triangle references an absent vertex.");
                if (indices.Length == 0) continue;
                var extrusion = config.F32(row + 0xF0);
                if (!float.IsFinite(extrusion) || extrusion < 0) throw new InvalidDataException("Trigger extrusion is invalid.");
                Extrude(ref vertices, ref indices, extrusion);
                GenerateNormals(vertices, indices);
                scene.Instances.Add(new MeshInstance
                {
                    ObjectPlacement = objectPlacement,
                    Name = name,
                    BubbleHash = bubble,
                    Layer = SceneLayer.Volumes,
                    Mesh = new MeshData { TagHash = configTag, Vertices = vertices, Indices = indices, PartIndex = row }
                });
            }
        }
    }

    /**
     * Trigger bounds are an ordered min at +0xB0 and max at +0xC0.
     * @return The marker's center and half size.
     */
    internal static (Vector3 Position, Vector3 Extents) TriggerBox(TagData config, int row)
    {
        var min = config.V3(row + 0xB0);
        var max = config.V3(row + 0xC0);
        if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidDataException("Trigger bounds are invalid.");
        return ((min + max) * 0.5f, (max - min) * 0.5f);
    }

    /** Each boundary edge joins the authored base to its Z-extruded cap. */
    private static void Extrude(ref SceneVertex[] vertices, ref uint[] indices, float height)
    {
        if (height == 0) return;
        var count = (uint)vertices.Length;
        var source = vertices;
        vertices = new SceneVertex[count * 2];
        source.CopyTo(vertices, 0);
        source.CopyTo(vertices, (int)count);
        for (var i = (int)count; i < vertices.Length; ++i) vertices[i].Position.Z += height;
        var edges = new Dictionary<(uint Low, uint High), (uint A, uint B, int Count)>();
        var output = new List<uint>(indices.Length * 2);
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            output.AddRange([c, b, a, a + count, b + count, c + count]);
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                var previous = edges.GetValueOrDefault(key);
                edges[key] = (edge.Item1, edge.Item2, previous.Count + 1);
            }
        }
        foreach (var edge in edges.Values.Where(e => e.Count == 1))
            output.AddRange([edge.A, edge.B, edge.B + count, edge.A, edge.B + count, edge.A + count]);
        indices = output.ToArray();
    }
}
