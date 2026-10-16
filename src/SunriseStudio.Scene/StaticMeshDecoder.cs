using System.Numerics;

namespace SunriseStudio.Scene;

/** Static-mesh part of AssetSceneLoader: decodes static meshes with their LOD, vertices and texcoords. */
public sealed partial class AssetSceneLoader
{
    /** The preview reads the pre-Beyond-Light static schema and validates every buffer range. */
    private MeshData[] StaticMesh(uint tag)
    {
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        var header = Read(tag);
        var resourceTag = header.U32(8);
        if (Class(resourceTag) != 0x80807194) throw new InvalidDataException("Static resource class differs from build 86657.");
        var data = Read(resourceTag);
        var materials = header.Rows(0x10, 0x80800014, 4);
        var assignments = data.Rows(8, 0x8080719B, 8);
        var parts = data.Rows(0x18, 0x8080719A, 12);
        var buffers = data.Rows(0x28, 0x80807199, 16);
        var drawable = Enumerable.Range(0, Math.Min(assignments.Length, materials.Length))
            .Any(i => DrawnStages.Contains(data.U16(assignments[i] + 2)));
        var minimumStage = assignments.Length == 0 ? 0 : assignments.Min(r => data.U16(r + 2));
        var partStages = new Dictionary<int, List<(int Stage, uint Material, int Layout)>>();
        var assigned = assignments.Select(r => (int)data.U16(r)).ToHashSet();
        for (var i = 0; i < assignments.Length && i < materials.Length; ++i)
        {
            int part = data.U16(assignments[i]), stage = data.U16(assignments[i] + 2);
            // Shadow casters (stage 3) load beside the colour stages; the native shadow pass draws them.
            if (drawable ? !DrawnStages.Contains(stage) && stage != ShadowStage : stage != minimumStage) continue;
            if (!partStages.TryGetValue(part, out var list)) partStages[part] = list = [];
            if (list.All(entry => entry.Stage != stage))
                list.Add((stage, header.U32(materials[i]), data.I16(assignments[i] + 4)));
        }
        var result = new List<MeshData>();
        // Header +0x38 to +0x40 size the three switch distances; +0x50 is the bounding sphere.
        var lodSizes = new Vector3(header.F32(0x38), header.F32(0x3C), header.F32(0x40));
        var lodSphere = new Vector4(header.V3(0x50), header.F32(0x5C));
        for (var i = 0; i < parts.Length; ++i)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = parts[i];
            var levels = StaticLod.Levels(data.U8(row + 10));
            var lodLevel = levels == 0 ? -1 : BitOperations.TrailingZeroCount(levels);
            if (lodLevel < 0 || lodLevel > 0 && !IncludeLowerLods) continue;
            // A part assigned only to undrawn stages is a shadow or depth caster.
            if (!partStages.ContainsKey(i) && (lodLevel == 0 && partStages.Count != 0 || assigned.Contains(i))) continue;
            var bufferIndex = data.U16(row + 8);
            if (bufferIndex >= buffers.Length) throw new InvalidDataException("Mesh part buffer index is out of range.");
            var bufferRow = buffers[bufferIndex];
            var vertexTag = data.U32(bufferRow + 4);
            var vertices = VertexPositions(vertexTag, header);
            var indices = IndexData(data.U32(bufferRow), data.U32(row), data.U32(row + 4), data.U8(row + 11), vertices.Length);
            var extraTag = data.U32(bufferRow + 8);
            if (catalog.TryGetEntry(extraTag, out _)) VertexTexcoords(extraTag, vertices, header);
            var sourceIndices = Compact(ref vertices, indices);
            GenerateNormals(vertices, indices);
            var entries = partStages.GetValueOrDefault(i) ?? [(minimumStage, 0u, -1)];
            for (var e = 0; e < entries.Count; ++e)
            {
                var (stage, material, layout) = entries[e];
                // A shadow entry after a colour entry shares its vertices; casters read no texture coordinates.
                bool shared = e != 0 && stage == ShadowStage;
                var decoded = new MeshData
                {
                    TagHash = tag,
                    // Texture coordinates follow each stage's own input layout.
                    Vertices = e == 0 || shared ? vertices : (SceneVertex[])vertices.Clone(),
                    Indices = indices,
                    MaterialTag = material,
                    PartIndex = i,
                    LodLevel = lodLevel,
                    StaticLod = new StaticLodPart(lodSizes, lodSphere, levels),
                    SourceVertexIndices = sourceIndices,
                    Native = NativeMesh(NativeMeshKind.Static, header, material,
                        vertexTag, extraTag, data.U32(bufferRow), data.U32(row), data.U32(row + 4),
                        data.U8(row + 11), data.U32(0x38), data.U32(bufferRow + 12),
                        declarationId: layout, renderStage: stage)
                };
                if (!shared) ApplyNativeTexcoords(decoded);
                result.Add(decoded);
            }
        }
        return meshes[tag] = result.ToArray();
    }

    // Colour stages the viewer draws: generate_gbuffer, decals, investment_decals, decals_additive, transparents.
    private static readonly HashSet<int> DrawnStages = [0, 1, 2, 6, 7];
    // shadow_generate.
    private const int ShadowStage = 3;

    /** Entity part category to its first detail level: 0 to 3 and 10 are 0; 4 is 1; 7 and 8 are 2; 9 is 3. */
    private static int LodLevel(byte category) => category switch
    {
        0 or 1 or 2 or 3 or 10 => 0,
        4 => 1,
        7 or 8 => 2,
        9 => 3,
        _ => -1
    };

    /** Parts retain only referenced vertices so unused LOD data cannot widen scene bounds. */
    private static uint[] Compact(ref SceneVertex[] vertices, uint[] indices)
    {
        var source = vertices;
        var remap = new Dictionary<uint, uint>();
        var compact = new List<SceneVertex>();
        var originals = new List<uint>();
        for (var i = 0; i < indices.Length; ++i)
        {
            var index = indices[i];
            if (!remap.TryGetValue(index, out var mapped))
            {
                mapped = (uint)compact.Count;
                remap.Add(index, mapped);
                compact.Add(source[index]);
                originals.Add(index);
            }
            indices[i] = mapped;
        }
        vertices = compact.ToArray();
        return originals.ToArray();
    }

    /** Buffer headers reference payload entries through their package reference field. */
    private TagData Buffer(uint tag, byte subtype, out TagData header)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != subtype)
            throw new InvalidDataException($"Tag 0x{tag:X8} is not a buffer header of subtype {subtype}.");
        header = Read(tag);
        if (!catalog.TryGetEntry(entry.Reference, out var payload) || payload.Type != 40 || payload.SubType != subtype)
            throw new InvalidDataException("Buffer payload has the wrong package type.");
        return Read(entry.Reference);
    }

    /** Static positions are signed normalized shorts followed by the authored model transform. */
    private SceneVertex[] VertexPositions(uint tag, TagData mesh)
    {
        var raw = Buffer(tag, 4, out var header);
        var size = header.U32(0);
        var stride = header.U16(4);
        if (stride is not (8 or 12 or 16 or 20 or 24 or 28 or 32))
            throw new InvalidDataException($"Position stride {stride} is not supported.");
        if (size > raw.Length || size % stride != 0 || header.U32(8) != 0xDEADBEEF)
            throw new InvalidDataException("Vertex header size or marker is invalid.");
        var vertices = new SceneVertex[size / stride];
        var shift = mesh.V3(0x60);
        var scale = mesh.F32(0x6C);
        if (!Finite(shift) || !float.IsFinite(scale)) throw new InvalidDataException("Mesh transform is not finite.");
        for (var i = 0; i < vertices.Length; ++i)
        {
            var o = i * stride;
            var p = new Vector3(Snorm(raw.I16(o)), Snorm(raw.I16(o + 2)), Snorm(raw.I16(o + 4)));
            vertices[i] = new SceneVertex(p * scale + shift, Vector3.UnitZ, Vector2.Zero, Vector4.One);
        }
        return vertices;
    }

    private static float Snorm(short v) => Math.Max(v / 32767f, -1f);

    /** Unknown attribute layouts keep zero texture coordinates in the diagnostic material. */
    private void VertexTexcoords(uint tag, SceneVertex[] vertices, TagData mesh)
    {
        var raw = Buffer(tag, 4, out var header);
        var stride = header.U16(4);
        if (stride is not (4 or 12 or 16 or 20 or 24) || raw.Length / stride < vertices.Length) return;
        var scale = new Vector2(mesh.F32(0x70), mesh.F32(0x74));
        var shift = new Vector2(mesh.F32(0x78), mesh.F32(0x7C));
        for (var i = 0; i < vertices.Length; ++i)
            vertices[i].TexCoord = new Vector2(Snorm(raw.I16(i * stride)), Snorm(raw.I16(i * stride + 2))) * scale + shift;
    }

    /** Strip restart indices reset both winding and the two-index history. */
    private uint[] IndexData(uint tag, uint first, uint count, byte primitive, int vertexCount)
    {
        var data = Buffer(tag, 6, out var header);
        var wide = header.U8(1) != 0;
        var width = wide ? 4 : 2;
        if (header.U64(8) > (ulong)data.Length || ((ulong)first + count) * (uint)width > (ulong)data.Length)
            throw new InvalidDataException("Index range leaves its buffer.");
        if (primitive is not (3 or 5)) throw new InvalidDataException($"Primitive type {primitive} is not supported.");
        var result = new List<uint>();
        var restart = wide ? uint.MaxValue : ushort.MaxValue;
        uint a = 0, b = 0;
        var held = 0;
        for (uint i = 0; i < count; ++i)
        {
            var offset = checked((int)((first + i) * width));
            var current = wide ? data.U32(offset) : data.U16(offset);
            if (primitive == 5 && current == restart) { held = 0; continue; }
            if (current >= vertexCount) throw new InvalidDataException("Index references an absent vertex.");
            if (primitive == 3) { result.Add(current); continue; }
            if (held >= 2 && a != b && b != current && a != current)
            {
                result.Add((held & 1) == 0 ? a : b);
                result.Add((held & 1) == 0 ? b : a);
                result.Add(current);
            }
            a = b; b = current; held++;
        }
        if (result.Count % 3 != 0) throw new InvalidDataException("Triangle list has an incomplete final triangle.");
        return result.ToArray();
    }

    /** Generated normals provide a diagnostic view without claiming shader-accurate attributes. */
    private static void GenerateNormals(SceneVertex[] vertices, uint[] indices)
    {
        for (var i = 0; i < vertices.Length; ++i) vertices[i].Normal = Vector3.Zero;
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
            vertices[a].Normal += normal; vertices[b].Normal += normal; vertices[c].Normal += normal;
        }
        for (var i = 0; i < vertices.Length; ++i)
            vertices[i].Normal = vertices[i].Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(vertices[i].Normal) : Vector3.UnitZ;
    }
}
