using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Interchange;

/**
 * Collision as separate geometry: glTF or GLB with one node per placed shape and one mesh per
 * distinct shape, or OBJ with one object per placement. glTF nodes carry their identity in extras,
 * so an edited file can come back; OBJ is for viewing only.
 */
public static class CollisionInterchange
{
    public const string NodeExtras = "sunrise_collision", MeshExtras = "sunrise_collision_shape", AssetExtras = "sunrise_collision_export";
    // glTF is Y-up; the root node turns the game's Z-up coordinates into it.
    private static readonly float[] ZUpToYUp = [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1];
    private static readonly Matrix4x4 YUpToZUp = new(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);

    /** Writes by extension: .glb, .gltf or .obj. */
    public static void Write(string path, CollisionScene scene, PackageCatalog catalog, uint rootTag)
    {
        if (scene.IsEmpty) throw new InvalidDataException("There is no decoded collision to export.");
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".obj": WriteObj(path, scene); break;
            case ".glb" or ".gltf": WriteGltf(path, scene, Sources(scene, catalog), Materials(scene, catalog), rootTag); break;
            default: throw new NotSupportedException("Export collision as .glb, .gltf or .obj.");
        }
    }

    /** SHA-256 of every compound and shape tag the scene uses, so an import can refuse a stale file. */
    public static SortedDictionary<uint, string> Sources(CollisionScene scene, PackageCatalog catalog)
    {
        var tags = new SortedSet<uint>(scene.Pieces.Select(piece => piece.Tag));
        tags.UnionWith(scene.Placements.Where(p => p.CompoundTag != 0).Select(p => p.CompoundTag));
        var result = new SortedDictionary<uint, string>();
        foreach (uint tag in tags)
            if (catalog.TryGetEntry(tag, out _)) result[tag] = BinaryDelta.Hash(catalog.Read(tag));
        return result;
    }

    /** One readable node name per placement, such as "Mesh 80C6C7EE @80EDC2D0#12". */
    public static string NodeName(CollisionScene scene, CollisionPlacement placement)
    {
        var piece = scene.Pieces[placement.Piece];
        string shape = piece.Offset == 0 ? $"{piece.Kind} {piece.Tag:X8}" : $"{piece.Kind} {piece.Tag:X8}+{piece.Offset:X}";
        return placement.Instance >= 0 ? $"{shape} @{placement.CompoundTag:X8}#{placement.Instance}" : shape;
    }

    /** The identity a placement carries in glTF node extras and Blender custom properties. */
    public static JsonObject Identity(CollisionScene scene, CollisionPlacement placement)
    {
        var piece = scene.Pieces[placement.Piece];
        var entity = scene.Entities[placement.Entity];
        return new JsonObject
        {
            ["source"] = entity.Source.ToString(), ["entity"] = Hex(entity.Tag), ["compound"] = Hex(placement.CompoundTag),
            ["compound_offset"] = placement.CompoundOffset, ["instance"] = placement.Instance, ["shape"] = Hex(piece.Tag),
            ["shape_offset"] = piece.Offset, ["kind"] = piece.Kind, ["class"] = piece.Shape.Class,
            ["triangles"] = piece.Shape.TriangleCount, ["name"] = NodeName(scene, placement)
        };
    }

    /**
     * The material tags of each compound's owner, by compound tag in hex. A new shape names one of
     * them in its `sunrise_collision_material` property.
     */
    public static SortedDictionary<string, string[]> Materials(CollisionScene scene, PackageCatalog catalog)
    {
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var placement in scene.Placements.Where(p => p.Instance >= 0))
        {
            string key = Hex(placement.CompoundTag);
            if (result.ContainsKey(key)) continue;
            uint owner = scene.Entities[placement.Entity].Tag;
            if (!catalog.TryGetEntry(owner, out var entry) || entry.Reference != StaticCollisionReader.OwnerClass) continue;
            try { result[key] = CollisionOwnerRows.Read(catalog.Read(owner)).Materials.Select(Hex).ToArray(); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException) { }
        }
        return result;
    }

    private static void WriteObj(string path, CollisionScene scene)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("# Sunrise Studio collision. Z-up game coordinates, one object per placed shape.");
        int based = 1;
        foreach (var placement in scene.Placements)
        {
            var shape = scene.Pieces[placement.Piece].Shape;
            if (shape.TriangleCount == 0) continue;
            writer.WriteLine($"o {NodeName(scene, placement)}");
            foreach (var vertex in shape.Vertices)
            {
                var world = Vector3.Transform(vertex, placement.World);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {world.X:R} {world.Y:R} {world.Z:R}"));
            }
            bool mirrored = placement.World.GetDeterminant() < 0;
            for (int i = 0; i < shape.Indices.Length; i += 3)
            {
                int a = shape.Indices[i] + based, b = shape.Indices[i + 1] + based, c = shape.Indices[i + 2] + based;
                writer.WriteLine(mirrored ? $"f {a} {c} {b}" : $"f {a} {b} {c}");
            }
            based += shape.Vertices.Length;
        }
    }

    private static void WriteGltf(string path, CollisionScene scene, IReadOnlyDictionary<uint, string> sources,
        IReadOnlyDictionary<string, string[]> materials, uint rootTag)
    {
        using var data = new MemoryStream();
        using var binary = new BinaryWriter(data, Encoding.UTF8, true);
        var views = new JsonArray();
        var accessors = new JsonArray();
        var meshes = new JsonArray();
        var nodes = new JsonArray();
        var meshOf = new int[scene.Pieces.Count];
        foreach (var piece in scene.Pieces)
        {
            meshOf[piece.Index] = -1;
            var shape = piece.Shape;
            if (shape.TriangleCount == 0) continue;
            Align(binary);
            int positions = accessors.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = data.Position, ["byteLength"] = shape.Vertices.Length * 12, ["target"] = 34962 });
            foreach (var v in shape.Vertices) { binary.Write(v.X); binary.Write(v.Y); binary.Write(v.Z); }
            var min = shape.Vertices.Aggregate(Vector3.Min);
            var max = shape.Vertices.Aggregate(Vector3.Max);
            accessors.Add(new JsonObject
            {
                ["bufferView"] = views.Count - 1, ["componentType"] = 5126, ["count"] = shape.Vertices.Length, ["type"] = "VEC3",
                ["min"] = new JsonArray(min.X, min.Y, min.Z), ["max"] = new JsonArray(max.X, max.Y, max.Z)
            });
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = data.Position, ["byteLength"] = shape.Indices.Length * 4, ["target"] = 34963 });
            foreach (int index in shape.Indices) binary.Write((uint)index);
            accessors.Add(new JsonObject { ["bufferView"] = views.Count - 1, ["componentType"] = 5125, ["count"] = shape.Indices.Length, ["type"] = "SCALAR" });
            meshOf[piece.Index] = meshes.Count;
            meshes.Add(new JsonObject
            {
                ["name"] = piece.Offset == 0 ? $"{piece.Kind} {piece.Tag:X8}" : $"{piece.Kind} {piece.Tag:X8}+{piece.Offset:X}",
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = positions }, ["indices"] = positions + 1, ["mode"] = 4 }),
                ["extras"] = new JsonObject { [MeshExtras] = new JsonObject
                {
                    ["shape"] = Hex(piece.Tag), ["offset"] = piece.Offset, ["class"] = shape.Class, ["kind"] = piece.Kind,
                    ["vertices"] = shape.Vertices.Length, ["triangles"] = shape.TriangleCount
                } }
            });
        }
        var children = new JsonArray();
        foreach (var placement in scene.Placements)
        {
            var node = new JsonObject { ["name"] = NodeName(scene, placement) };
            WriteTransform(node, placement.World);
            if (meshOf[placement.Piece] >= 0) node["mesh"] = meshOf[placement.Piece];
            node["extras"] = new JsonObject { [NodeExtras] = Identity(scene, placement) };
            children.Add(nodes.Count + 1);
            nodes.Add(node);
        }
        // The root goes first so the placements keep their scene order after it.
        var root = new JsonObject { ["name"] = "Sunrise coordinates", ["matrix"] = new JsonArray(ZUpToYUp.Select(v => (JsonNode)v).ToArray()), ["children"] = children };
        nodes.Insert(0, root);
        var sourceNode = new JsonObject();
        foreach (var (tag, hash) in sources) sourceNode[Hex(tag)] = hash;
        var materialNode = new JsonObject();
        foreach (var (compound, tags) in materials) materialNode[compound] = new JsonArray(tags.Select(tag => (JsonNode)tag).ToArray());
        bool glb = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        byte[] payload = data.ToArray();
        var buffer = new JsonObject { ["byteLength"] = payload.Length };
        if (!glb) buffer["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(payload);
        var document = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0", ["generator"] = "Sunrise Studio collision",
                ["extras"] = new JsonObject { [AssetExtras] = new JsonObject
                {
                    ["version"] = 1, ["root"] = Hex(rootTag), ["sources"] = sourceNode, ["materials"] = materialNode
                } }
            },
            ["scene"] = 0, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = nodes, ["accessors"] = accessors, ["bufferViews"] = views, ["buffers"] = new JsonArray(buffer)
        };
        if (meshes.Count > 0) document["meshes"] = meshes;
        byte[] json = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = !glb }));
        if (glb) SunriseStudio.Formats.GltfInterchange.WriteGlb(path, json, payload);
        else File.WriteAllBytes(path, json);
    }

    /** TRS when the decomposition rebuilds the matrix to 1e-4; the matrix itself otherwise. */
    private static void WriteTransform(JsonObject node, Matrix4x4 world)
    {
        if (world.GetDeterminant() > 0 && Matrix4x4.Decompose(world, out var scale, out var rotation, out var translation))
        {
            var rebuilt = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            if (Near(rebuilt, world))
            {
                node["translation"] = new JsonArray(translation.X, translation.Y, translation.Z);
                node["rotation"] = new JsonArray(rotation.X, rotation.Y, rotation.Z, rotation.W);
                node["scale"] = new JsonArray(scale.X, scale.Y, scale.Z);
                return;
            }
        }
        node["matrix"] = new JsonArray(world.M11, world.M12, world.M13, world.M14, world.M21, world.M22, world.M23, world.M24,
            world.M31, world.M32, world.M33, world.M34, world.M41, world.M42, world.M43, world.M44);
    }

    /**
     * Reads an edited glTF or GLB back. Placements come from nodes with collision extras, in game
     * coordinates; shapes come from their meshes, in the shape's own space.
     */
    public static CollisionImportInput Read(string path)
    {
        var (json, binary) = Split(File.ReadAllBytes(path));
        var document = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The collision file is empty.");
        if (document["asset"]?["version"]?.GetValue<string>() != "2.0") throw new NotSupportedException("Only glTF 2.0 is supported.");
        var sources = new Dictionary<uint, string>();
        if (document["asset"]?["extras"]?[AssetExtras]?["sources"] is JsonObject known)
            foreach (var (tag, hash) in known) sources[ParseHex(tag)] = hash!.GetValue<string>();
        if (sources.Count == 0) throw new InvalidDataException("This file is not a Sunrise collision export. Export the collision from Studio first.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var buffers = (document["buffers"] as JsonArray ?? []).Select(item => Buffer(item!.AsObject(), binary, directory)).ToArray();
        var nodes = document["nodes"] as JsonArray ?? [];
        var meshes = document["meshes"] as JsonArray ?? [];
        var parents = new int[nodes.Count];
        Array.Fill(parents, -1);
        for (int i = 0; i < nodes.Count; i++)
            foreach (var child in nodes[i]?["children"] as JsonArray ?? [])
            {
                int index = child!.GetValue<int>();
                if (index < 0 || index >= nodes.Count || parents[index] >= 0) throw new InvalidDataException("Invalid glTF node hierarchy.");
                parents[index] = i;
            }
        Matrix4x4 World(int index)
        {
            var world = Matrix4x4.Identity;
            for (int depth = 0; index >= 0; index = parents[index], depth++)
            {
                if (depth > 256) throw new InvalidDataException("Invalid glTF node hierarchy.");
                world *= Local(nodes[index]!.AsObject());
            }
            return world * YUpToZUp;
        }
        (List<Vector3> Vertices, List<int> Indices) Mesh(int index)
        {
            var mesh = meshes[index]!.AsObject();
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            foreach (var primitive in mesh["primitives"] as JsonArray ?? [])
            {
                if (primitive!["mode"] is JsonNode mode && mode.GetValue<int>() != 4) continue;
                int start = vertices.Count;
                vertices.AddRange(Positions(document, buffers, primitive["attributes"]!["POSITION"]!.GetValue<int>()));
                if (primitive["indices"] is JsonNode accessor) indices.AddRange(Indices(document, buffers, accessor.GetValue<int>()).Select(v => start + v));
                else indices.AddRange(Enumerable.Range(start, vertices.Count - start));
            }
            return (vertices, indices);
        }
        var placements = new List<CollisionPlacementInput>();
        var shapes = new Dictionary<(uint, int), CollisionShapeInput>();
        // Copies of one node share its identity; the one that keeps the exported name is the original.
        var groups = new Dictionary<(uint, int, uint, int), List<(int Node, JsonObject Identity)>>();
        var added = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]!.AsObject();
            if (node["extras"]?[NodeExtras] is JsonObject identity)
            {
                var key = (ParseHex(identity["compound"]!.GetValue<string>()), identity["instance"]!.GetValue<int>(),
                    ParseHex(identity["shape"]!.GetValue<string>()), identity["shape_offset"]!.GetValue<int>());
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                group.Add((i, identity));
            }
            else if (node["mesh"] is not null) added.Add(i);
        }
        foreach (var ((compound, instance, shapeTag, shapeOffset), group) in groups)
        {
            string? exported = group[0].Identity["name"]?.GetValue<string>();
            int original = group.FindIndex(item => nodes[item.Node]!["name"]?.GetValue<string>() == exported);
            if (original < 0) original = 0;
            int node = group[original].Node;
            placements.Add(new(compound, instance, shapeTag, shapeOffset, World(node)));
            int? meshIndex = nodes[node]!["mesh"]?.GetValue<int>();
            if (meshIndex is int index && !shapes.ContainsKey((shapeTag, shapeOffset)))
            {
                var (vertices, indices) = Mesh(index);
                shapes[(shapeTag, shapeOffset)] = new(shapeTag, shapeOffset, vertices, indices);
            }
            for (int k = 0; k < group.Count; k++)
            {
                if (k == original) continue;
                int copy = group[k].Node;
                int? copyMesh = nodes[copy]!["mesh"]?.GetValue<int>();
                var (vertices, indices) = copyMesh is int other ? Mesh(other) : ([], []);
                // A copy with its own geometry is a new shape; with the original's, another instance.
                bool same = copyMesh == meshIndex || shapes.TryGetValue((shapeTag, shapeOffset), out var shape)
                    && shape.Indices.SequenceEqual(indices) && shape.Vertices.SequenceEqual(vertices);
                placements.Add(same
                    ? new(compound, instance, shapeTag, shapeOffset, World(copy)) { Added = true }
                    : new(compound, -1, 0, 0, World(copy))
                    {
                        Added = true,
                        NewShape = new(NodeText(nodes[copy]!, "name") ?? $"collision-{copy}", vertices, indices,
                            group[k].Identity["kind"]?.GetValue<string>() == "Convex", MaterialOf(nodes[copy]!))
                    });
            }
        }
        foreach (int node in added)
        {
            var (vertices, indices) = Mesh(nodes[node]!["mesh"]!.GetValue<int>());
            var extras = nodes[node]!["extras"];
            uint compound = NodeText(extras, "sunrise_collision_compound") is { } text ? ParseHex(text) : 0;
            placements.Add(new(compound, -1, 0, 0, World(node))
            {
                Added = true,
                NewShape = new(NodeText(nodes[node]!, "name") ?? $"collision-{node}", vertices, indices,
                    NodeText(extras, "sunrise_collision_kind") == "Convex", MaterialOf(nodes[node]!))
            });
        }
        if (placements.Count == 0) throw new InvalidDataException("No node carries Sunrise collision properties. Keep custom properties when exporting.");
        return new(placements, shapes.Values.ToArray(), sources);
    }

    /** The material tag a node names in `sunrise_collision_material`; null takes the nearest triangle's. */
    private static uint? MaterialOf(JsonNode node) =>
        NodeText(node["extras"], "sunrise_collision_material") is { Length: > 0 } text ? ParseHex(text) : null;

    private static string? NodeText(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray matrix)
        {
            float[] m = matrix.Select(v => v!.GetValue<float>()).ToArray();
            if (m.Length != 16) throw new InvalidDataException("Invalid glTF matrix.");
            return new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        float[] Values(string name, float[] fallback) => node[name] is JsonArray values ? values.Select(v => v!.GetValue<float>()).ToArray() : fallback;
        var t = Values("translation", [0, 0, 0]); var r = Values("rotation", [0, 0, 0, 1]); var s = Values("scale", [1, 1, 1]);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new(r[0], r[1], r[2], r[3])))
            * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    private static (byte[] Data, int Start, int Stride, int Count, int Type) Accessor(JsonObject document, byte[][] buffers, int index, string shape)
    {
        var accessor = document["accessors"]![index]!.AsObject();
        if (accessor["type"]!.GetValue<string>() != shape || accessor["sparse"] is not null) throw new NotSupportedException("Unsupported glTF accessor.");
        var view = document["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!.AsObject();
        byte[] bytes = buffers[view["buffer"]!.GetValue<int>()];
        int type = accessor["componentType"]!.GetValue<int>(), count = accessor["count"]!.GetValue<int>();
        int size = type switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => throw new NotSupportedException("Unsupported glTF component type.") };
        int width = shape == "VEC3" ? 3 : 1;
        int start = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        int stride = view["byteStride"]?.GetValue<int>() ?? width * size;
        if (count < 0 || start < 0 || (long)start + Math.Max(0, count - 1L) * stride + (count == 0 ? 0 : width * size) > bytes.Length)
            throw new InvalidDataException("A glTF accessor leaves its buffer.");
        return (bytes, start, stride, count, type);
    }

    private static IEnumerable<Vector3> Positions(JsonObject document, byte[][] buffers, int index)
    {
        var (bytes, start, stride, count, type) = Accessor(document, buffers, index, "VEC3");
        if (type != 5126) throw new NotSupportedException("Collision positions must be floats.");
        for (int i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(start + i * stride);
            yield return new(BinaryPrimitives.ReadSingleLittleEndian(span), BinaryPrimitives.ReadSingleLittleEndian(span[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[8..]));
        }
    }

    private static IEnumerable<int> Indices(JsonObject document, byte[][] buffers, int index)
    {
        var (bytes, start, stride, count, type) = Accessor(document, buffers, index, "SCALAR");
        for (int i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(start + i * stride);
            yield return type switch
            {
                5121 => span[0],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span)),
                _ => throw new InvalidDataException("Invalid glTF index type.")
            };
        }
    }

    private static byte[] Buffer(JsonObject buffer, byte[]? binary, string directory)
    {
        if (buffer["uri"] is not JsonNode uri) return binary ?? throw new InvalidDataException("The GLB has no binary chunk.");
        string text = uri.GetValue<string>();
        if (text.StartsWith("data:", StringComparison.Ordinal))
            return Convert.FromBase64String(text[(text.IndexOf(',') + 1)..]);
        string full = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(text)));
        if (!full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A glTF buffer leaves its folder.");
        return File.ReadAllBytes(full);
    }

    private static (byte[] Json, byte[]? Binary) Split(byte[] file)
    {
        if (file.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(file) != 0x46546C67) return (file, null);
        byte[]? json = null, binary = null;
        for (int at = 12; at + 8 <= file.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            if (length < 0 || at + 8 + length > file.Length) throw new InvalidDataException("Invalid GLB chunk.");
            if (type == 0x4E4F534A) json = file.AsSpan(at + 8, length).ToArray();
            else if (type == 0x004E4942) binary = file.AsSpan(at + 8, length).ToArray();
            at += 8 + length;
        }
        return (json ?? throw new InvalidDataException("The GLB has no JSON chunk."), binary);
    }

    private static void Align(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (MathF.Abs(a[row, column] - b[row, column]) > 1e-4f * MathF.Max(1, MathF.Abs(b[row, column]))) return false;
        return true;
    }

    /** Formats a hash as 8 hex digits. */
    public static string Hex(uint value) => value.ToString("X8", CultureInfo.InvariantCulture);

    /** Parses a hex hash with or without a 0x prefix. */
    public static uint ParseHex(string value) => uint.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
