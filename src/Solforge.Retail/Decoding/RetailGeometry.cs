/**
 * File: Retail static meshes, static instances and terrain as untextured triangle lists. VERIFIED layouts:
 * statics component 0x80806CC9 -> wrapper 0x80806A0D (+8) -> SStaticMapData 0x808093AD (transforms +0x40, statics +0x78,
 * instance groups +0x88); SStaticMesh 0x80806D44 (+8) -> SStaticMeshData 0x80806D30 (parts +0x18, buffers +0x28, offset
 * +0x40, scale +0x4C); terrain component 0x80806C7D (+8) -> STerrain 0x80806C81, whose decoded vertices fill its bounds
 * exactly. Buffer header tags (file type 32: subtype 4 vertex, 6 index) name their data tag in the index's reference.
 */
using System.Numerics;

namespace Solforge.Retail.Decoding;

/** An untextured mesh: positions (mesh-local for statics, world for terrain) and a triangle list. */
public sealed record RetailMesh(uint Tag, string Kind, Vector3[] Positions, uint[] Indices, IReadOnlyList<string> Notes)
{
    public int Triangles => Indices.Length / 3;
}

/** One static mesh placed by an instance group: the SStaticMesh tag and its world transforms. */
public sealed record RetailStaticPlacement(uint Static, IReadOnlyList<Matrix4x4> Transforms);

/** A decoded SStaticMapData: its placements plus the instance tag. */
public sealed record RetailStaticInstances(uint Tag, IReadOnlyList<RetailStaticPlacement> Placements, int Instances, IReadOnlyList<string> Notes);

/** Decodes meshes and buffers. */
public static class RetailGeometry
{
    public const uint StaticMeshClass = 0x80806D44, StaticMeshDataClass = 0x80806D30, StaticMapDataClass = 0x808093AD, TerrainClass = 0x80806C81;
    private static readonly int[] PackedStrides = [0x08, 0x0C, 0x10, 0x1C, 0x20];

    /** The data tag of a buffer header (type 32), from the index. */
    public static uint? BufferData(RetailSource source, uint header) =>
        source.Index?.Find(header) is { Type: 32 } entry && RetailTag.Plausible(entry.Reference) ? entry.Reference : null;

    /** A vertex buffer: stride from its header (u32 size, u16 stride, u16 type) and the data bytes. */
    public static (byte[] Data, int Stride)? VertexBuffer(RetailSource source, uint header, List<string> notes)
    {
        if (source.Read(header) is not { Length: >= 8 } head) { notes.Add($"Vertex header {RetailTag.Hex(header)} has no payload."); return null; }
        if (BufferData(source, header) is not { } dataTag || source.Read(dataTag) is not { } data)
        { notes.Add($"Vertex data of {RetailTag.Hex(header)} is not in the index or dump."); return null; }
        return (data, RetailBytes.U16(head, 4));
    }

    /** An index buffer as uints (header byte 1 is the 32-bit flag). */
    public static uint[]? IndexBuffer(RetailSource source, uint header, List<string> notes)
    {
        if (source.Read(header) is not { Length: >= 16 } head) { notes.Add($"Index header {RetailTag.Hex(header)} has no payload."); return null; }
        if (BufferData(source, header) is not { } dataTag || source.Read(dataTag) is not { } data)
        { notes.Add($"Index data of {RetailTag.Hex(header)} is not in the index or dump."); return null; }
        bool wide = head[1] != 0;
        int count = data.Length / (wide ? 4 : 2);
        var result = new uint[count];
        for (int i = 0; i < count; i++) result[i] = wide ? RetailBytes.U32(data, i * 4) : RetailBytes.U16(data, i * 2);
        return result;
    }

    /** Appends a primitive range as triangles: 3 = list, 5 = strip (restart 0xFFFF / 0xFFFFFFFF, degenerates dropped). */
    public static void AppendTriangles(List<uint> output, uint[] indices, int start, int count, int primitive, uint baseVertex = 0)
    {
        int end = (int)Math.Min((long)start + count, indices.Length);
        if (start < 0 || start >= end) return;
        if (primitive == 3)
        {
            for (int i = start; i + 2 < end; i += 3) { output.Add(indices[i] + baseVertex); output.Add(indices[i + 1] + baseVertex); output.Add(indices[i + 2] + baseVertex); }
            return;
        }
        int run = 0;
        for (int i = start; i + 2 < end; i++)
        {
            uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a is 0xFFFF or 0xFFFFFFFF) { run = 0; continue; }
            if (b is 0xFFFF or 0xFFFFFFFF || c is 0xFFFF or 0xFFFFFFFF) { run++; continue; }
            bool flip = (run & 1) == 1;
            run++;
            if (a == b || b == c || a == c) continue;
            if (flip) (a, b) = (b, a);
            output.Add(a + baseVertex); output.Add(b + baseVertex); output.Add(c + baseVertex);
        }
    }

    /** Highest-detail LOD categories (alkahest LodCategory.is_highest_detail: Lod_0_0..Lod_0_3 and Lod_Detail). */
    public static bool HighestDetail(int lodCategory) => lodCategory is <= 3 or 10;

    /**
     * The parts to draw, as alkahest's static renderer picks them: parts named by a mesh group (u16 part, u8 render stage,
     * stride 6) whose stage is GenerateGbuffer (0) and whose LOD is highest detail; without such groups, every
     * highest-detail part; without those, the parts of the lowest LOD present.
     */
    public static HashSet<int> SelectParts(ReadOnlySpan<byte> data, RetailArray parts)
    {
        var result = new HashSet<int>();
        if (RetailBytes.TryArray(data, 0x08, out var groups))
            for (int g = 0; g < groups.Count; g++)
            {
                int part = RetailBytes.U16(data, groups.Data + g * 6);
                if (RetailBytes.U8(data, groups.Data + g * 6 + 2) == 0 && part < parts.Count && HighestDetail(RetailBytes.U8(data, parts.Data + part * 12 + 10)))
                    result.Add(part);
            }
        if (result.Count > 0) return result;
        for (int i = 0; i < parts.Count; i++) if (HighestDetail(RetailBytes.U8(data, parts.Data + i * 12 + 10))) result.Add(i);
        if (result.Count > 0) return result;
        int best = int.MaxValue;
        for (int i = 0; i < parts.Count; i++) best = Math.Min(best, RetailBytes.U8(data, parts.Data + i * 12 + 10));
        for (int i = 0; i < parts.Count; i++) if (RetailBytes.U8(data, parts.Data + i * 12 + 10) == best) result.Add(i);
        return result;
    }

    /** Reads an SStaticMesh's opaque GenerateGbuffer parts at the highest detail (SelectParts) in mesh-local space. */
    public static RetailMesh? ReadStaticMesh(RetailSource source, uint staticTag, out string? problem)
    {
        problem = null;
        var notes = new List<string>();
        if (source.Read(staticTag) is not { } mesh) { problem = $"Static {RetailTag.Hex(staticTag)} has no payload."; return null; }
        uint dataTag = RetailBytes.U32(mesh, 0x08);
        if (source.Read(dataTag) is not { } data) { problem = $"Static mesh data {RetailTag.Hex(dataTag)} has no payload."; return null; }
        if (!RetailBytes.TryArray(data, 0x18, out var parts) || !RetailBytes.TryArray(data, 0x28, out var buffers) || buffers.Count == 0)
        { problem = $"{RetailTag.Hex(dataTag)} has no parts/buffers arrays (not an SStaticMeshData 0x80806D30?)."; return null; }
        var offset = new Vector3(RetailBytes.F32(data, 0x40), RetailBytes.F32(data, 0x44), RetailBytes.F32(data, 0x48));
        float scale = RetailBytes.F32(data, 0x4C);
        var selected = SelectParts(data, parts);
        var positions = new List<Vector3>();
        var triangles = new List<uint>();
        var loaded = new Dictionary<int, (uint Base, uint[] Indices)>();
        for (int i = 0; i < parts.Count; i++)
        {
            int part = parts.Data + i * 12;
            int primitive = RetailBytes.U8(data, part + 11), bufferIndex = RetailBytes.U8(data, part + 8);
            if (!selected.Contains(i)) continue;
            if (primitive is not (3 or 5)) { notes.Add($"Part {i}: primitive type {primitive} skipped."); continue; }
            if (bufferIndex >= buffers.Count) { notes.Add($"Part {i}: buffer {bufferIndex} out of range."); continue; }
            if (!loaded.TryGetValue(bufferIndex, out var buffer))
            {
                int at = buffers.Data + bufferIndex * 16;
                uint indexHeader = RetailBytes.U32(data, at), vertexHeader = RetailBytes.U32(data, at + 4);
                var indices = IndexBuffer(source, indexHeader, notes);
                var vertices = VertexBuffer(source, vertexHeader, notes);
                if (indices is null || vertices is not { } vertexBuffer) { loaded[bufferIndex] = (0u, System.Array.Empty<uint>()); continue; }
                var (vertexData, stride) = vertexBuffer;
                if (!PackedStrides.Contains(stride))
                {
                    notes.Add($"Vertex stride 0x{stride:X} is not a known packed int16 layout; buffer {bufferIndex} skipped.");
                    loaded[bufferIndex] = (0u, System.Array.Empty<uint>());
                    continue;
                }
                uint first = (uint)positions.Count;
                for (int v = 0; v + stride <= vertexData.Length; v += stride)
                    positions.Add(new Vector3(RetailBytes.I16(vertexData, v), RetailBytes.I16(vertexData, v + 2), RetailBytes.I16(vertexData, v + 4)) / 32767f * scale + offset);
                loaded[bufferIndex] = buffer = (first, indices);
            }
            if (buffer.Indices.Length == 0) continue;
            AppendTriangles(triangles, buffer.Indices, (int)RetailBytes.U32(data, part), (int)RetailBytes.U32(data, part + 4), primitive, buffer.Base);
        }
        DropOutOfRange(triangles, positions.Count, notes);
        if (triangles.Count == 0) { problem = $"Static {RetailTag.Hex(staticTag)} produced no triangles."; if (notes.Count > 0) problem += " " + string.Join(" ", notes.Take(3)); return null; }
        return new RetailMesh(staticTag, "static", positions.ToArray(), triangles.ToArray(), notes);
    }

    /** Reads an SStaticMapData (or its 0x80806A0D wrapper) into static placements with world transforms. */
    public static RetailStaticInstances? ReadStaticInstances(RetailSource source, uint tag, out string? problem)
    {
        problem = null;
        if (source.Read(tag) is not { } bytes) { problem = $"{RetailTag.Hex(tag)} has no payload."; return null; }
        if (bytes.Length <= 0x30 && RetailTag.Plausible(RetailBytes.U32(bytes, 0x08)))
        {
            tag = RetailBytes.U32(bytes, 0x08); // the 0x80806A0D wrapper names the instances tag at +8
            if (source.Read(tag) is not { } inner) { problem = $"Static instances {RetailTag.Hex(tag)} have no payload."; return null; }
            bytes = inner;
        }
        if (!RetailBytes.TryArray(bytes, 0x40, out var transforms) || !RetailBytes.TryArray(bytes, 0x78, out var statics)
            || !RetailBytes.TryArray(bytes, 0x88, out var groups))
        { problem = $"{RetailTag.Hex(tag)} does not decode as SStaticMapData 0x808093AD (transforms +0x40, statics +0x78, groups +0x88)."; return null; }
        var notes = new List<string>();
        var placements = new List<RetailStaticPlacement>();
        int instances = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            int at = groups.Data + g * 8;
            int count = RetailBytes.U16(bytes, at), start = RetailBytes.U16(bytes, at + 2), staticIndex = RetailBytes.U16(bytes, at + 4);
            if (staticIndex >= statics.Count) { notes.Add($"Group {g}: static index {staticIndex} out of range."); continue; }
            var matrices = new List<Matrix4x4>(count);
            for (int k = 0; k < count && start + k < transforms.Count; k++)
                matrices.Add(Transform(bytes, transforms.Data + (start + k) * 0x40));
            instances += matrices.Count;
            placements.Add(new RetailStaticPlacement(RetailBytes.U32(bytes, statics.Data + staticIndex * 4), matrices));
        }
        return new RetailStaticInstances(tag, placements, instances, notes);
    }

    /** A static instance transform (quat xyzw, translation at +0x10, uniform scale at +0x1C) as a row-vector matrix. */
    public static Matrix4x4 Transform(ReadOnlySpan<byte> bytes, int at)
    {
        var rotation = new Quaternion(RetailBytes.F32(bytes, at), RetailBytes.F32(bytes, at + 4), RetailBytes.F32(bytes, at + 8), RetailBytes.F32(bytes, at + 12));
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        var translation = new Vector3(RetailBytes.F32(bytes, at + 16), RetailBytes.F32(bytes, at + 20), RetailBytes.F32(bytes, at + 24));
        float scale = RetailBytes.F32(bytes, at + 28);
        if (!float.IsFinite(scale) || scale <= 0) scale = 1;
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
    }

    /** Reads an STerrain's highest-detail parts (detail level 0) in world space. */
    public static RetailMesh? ReadTerrain(RetailSource source, uint terrainTag, out string? problem)
    {
        problem = null;
        var notes = new List<string>();
        if (source.Read(terrainTag) is not { Length: >= 0x88 } bytes) { problem = $"Terrain {RetailTag.Hex(terrainTag)} has no payload."; return null; }
        var origin = new Vector4(RetailBytes.F32(bytes, 0x30), RetailBytes.F32(bytes, 0x34), RetailBytes.F32(bytes, 0x38), RetailBytes.F32(bytes, 0x3C));
        if (!RetailBytes.TryArray(bytes, 0x78, out var parts)) { problem = $"{RetailTag.Hex(terrainTag)} has no mesh parts at +0x78."; return null; }
        var indices = IndexBuffer(source, RetailBytes.U32(bytes, 0x68), notes);
        var vertices = VertexBuffer(source, RetailBytes.U32(bytes, 0x60), notes);
        if (indices is null || vertices is not { } vertexBuffer) { problem = string.Join(" ", notes); return null; }
        var (data, stride) = vertexBuffer;
        if (stride != 8) { problem = $"Terrain vertex stride {stride} (expected 8)."; return null; }
        var positions = new Vector3[data.Length / 8];
        for (int i = 0; i < positions.Length; i++)
        {
            float x = origin.X + RetailBytes.I16(data, i * 8), y = origin.Y + RetailBytes.I16(data, i * 8 + 2);
            float z = origin.Z + RetailBytes.I16(data, i * 8 + 4), w = origin.W + RetailBytes.I16(data, i * 8 + 6);
            positions[i] = new Vector3(x / 64f, y / 64f, (w * 65536f + z) * 0.000122070313f);
        }
        var triangles = new List<uint>();
        for (int i = 0; i < parts.Count; i++)
        {
            int at = parts.Data + i * 12;
            if (RetailBytes.U8(bytes, at + 11) != 0) continue; // detail level 0 is the full mesh
            AppendTriangles(triangles, indices, (int)RetailBytes.U32(bytes, at + 4), RetailBytes.U16(bytes, at + 8), 5);
        }
        DropOutOfRange(triangles, positions.Length, notes);
        if (triangles.Count == 0) { problem = $"Terrain {RetailTag.Hex(terrainTag)} produced no triangles."; return null; }
        return new RetailMesh(terrainTag, "terrain", positions, triangles.ToArray(), notes);
    }

    /** The SStaticMapData bounds at +0xA0 (min vec4, max vec4); the decoded instances fall inside them on 0x80E3F655. */
    public static (Vector3 Min, Vector3 Max) StaticBounds(ReadOnlySpan<byte> bytes) =>
        (new Vector3(RetailBytes.F32(bytes, 0xA0), RetailBytes.F32(bytes, 0xA4), RetailBytes.F32(bytes, 0xA8)),
         new Vector3(RetailBytes.F32(bytes, 0xB0), RetailBytes.F32(bytes, 0xB4), RetailBytes.F32(bytes, 0xB8)));

    /** The terrain AABB at +0x10 (min) and +0x20 (max). */
    public static (Vector3 Min, Vector3 Max) TerrainBounds(ReadOnlySpan<byte> bytes) =>
        (new Vector3(RetailBytes.F32(bytes, 0x10), RetailBytes.F32(bytes, 0x14), RetailBytes.F32(bytes, 0x18)),
         new Vector3(RetailBytes.F32(bytes, 0x20), RetailBytes.F32(bytes, 0x24), RetailBytes.F32(bytes, 0x28)));

    private static void DropOutOfRange(List<uint> triangles, int vertexCount, List<string> notes)
    {
        int dropped = 0;
        for (int i = triangles.Count - 3; i >= 0; i -= 3)
            if (triangles[i] >= vertexCount || triangles[i + 1] >= vertexCount || triangles[i + 2] >= vertexCount)
            { triangles.RemoveRange(i, 3); dropped++; }
        if (dropped > 0) notes.Add($"{dropped} triangles referenced vertices past the buffer and were dropped.");
    }
}
