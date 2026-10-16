using System.Numerics;

namespace SunriseStudio.Scene;

/** Terrain part of AssetSceneLoader: decodes terrain meshes. */
public sealed partial class AssetSceneLoader
{
    /** Terrain stores XY in 1/64 units and a signed two-word height in 1/8192 units. */
    private MeshData[] TerrainMesh(uint tag)
    {
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        var data = Read(tag);
        var parts = data.Rows(0x80, 0x80807152, 12);
        var groups = data.Rows(0x58, 0x80807154, 0x60);
        var raw = Buffer(data.U32(0x68), 4, out var header);
        if (header.U16(4) != 8 || header.U32(0) != raw.Length)
            throw new InvalidDataException("Terrain position buffer is not signed short4.");
        var offset = data.Q4(0x30);
        var allVertices = new SceneVertex[raw.Length / 8];
        for (var i = 0; i < allVertices.Length; ++i)
        {
            var position = new Vector3((raw.I16(i * 8) + offset.X) / 64,
                (raw.I16(i * 8 + 2) + offset.Y) / 64,
                (raw.I16(i * 8 + 4) + offset.Z + 65536 * (raw.I16(i * 8 + 6) + offset.W)) / 8192);
            allVertices[i] = new SceneVertex(position, Vector3.UnitZ, Vector2.Zero, Vector4.One);
        }
        var result = new List<MeshData>();
        foreach (var part in parts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (data.U8(part + 11) != 0) continue;
            if (data.U8(part + 10) >= groups.Length) throw new InvalidDataException("Terrain part group is out of range.");
            var group = groups[data.U8(part + 10)];
            var indices = IndexData(data.U32(0x70), data.U32(part + 4), data.U16(part + 8), 5, allVertices.Length);
            var vertices = allVertices;
            var sourceIndices = Compact(ref vertices, indices);
            GenerateNormals(vertices, indices);
            var decoded = new MeshData
            {
                TagHash = tag,
                Vertices = vertices,
                Indices = indices,
                MaterialTag = data.U32(part),
                PartIndex = part,
                SourceVertexIndices = sourceIndices,
                // The generate_gbuffer terrain draw binds declaration 60 and draws parts as strips.
                Native = NativeMesh(NativeMeshKind.Terrain, data, data.U32(part), data.U32(0x68), data.U32(0x6C),
                    data.U32(0x70), data.U32(part + 4), data.U16(part + 8), 5, declarationId: 60, renderStage: 0,
                    terrainGroup: (Vector(data, group + 0x20), data.U32(group + 0x50)))
            };
            ApplyNativeTexcoords(decoded);
            result.Add(decoded);
        }
        return meshes[tag] = result.ToArray();
    }
}
