using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Geometry part of world scene export: builds the shared geometry of one mesh part. */
public static partial class WorldSceneExport
{
    private static WorldGeometry Geometry(PackageCatalog catalog, MeshData mesh, int id, WorldBinaryWriter writer,
        GameMaterialReader materials, Dictionary<string, WorldGeometry> shared, List<BundleNotice> notices)
    {
        NativeSurfaceBasis? basis = null; BundleVertexMotion? motion = null;
        try
        {
            if (mesh.Native is { } native && NativeMeshBasis.IsSupported(catalog, native)) basis = NativeMeshBasis.Read(catalog, mesh);
        }
        catch (Exception error) when (Recoverable(error)) { notices.Add(new("Normals", "Diagnostic", error.Message, mesh.TagHash)); }
        try { motion = NativeVertexMotion.Read(catalog, mesh, materials) ?? NativeVertexInputs.Motion(catalog, mesh, materials, null); }
        catch (Exception error) when (Recoverable(error)) { notices.Add(new("Vertex motion", "Unavailable", error.Message, mesh.TagHash)); }
        if (basis is null && !notices.Any(notice => notice.Surface == "Normals" && notice.TagHash == mesh.TagHash))
            notices.Add(new("Normals", "Diagnostic", "Tangent frame not decoded; preview normals used.", mesh.TagHash));
        var colors = NativeVertexInputs.Colors(mesh);
        var zw = NativeVertexInputs.TexCoordZw(mesh);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(new[] { mesh.Vertices.Length, mesh.Indices.Length, basis is null ? 0 : 1,
            motion is null ? 0 : 1, colors is null ? 0 : 1, zw is null ? 0 : 1 }.AsSpan()));
        foreach (float[] row in (colors ?? []).Concat(zw ?? [])) hash.AppendData(MemoryMarshal.AsBytes(row.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan())); hash.AppendData(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
        if (basis is not null)
        {
            hash.AppendData(MemoryMarshal.AsBytes(basis.Normals.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(basis.Tangents.AsSpan()));
        }
        if (motion is not null)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { motion.Profile, motion.Constants, motion.Rates }));
            foreach (float[] color in motion.Colors) hash.AppendData(MemoryMarshal.AsBytes(color.AsSpan()));
        }
        string key = Convert.ToHexString(hash.GetHashAndReset());
        if (shared.TryGetValue(key, out var existing)) return existing;
        int count = mesh.Vertices.Length;
        var positions = writer.Floats(count, 3, (row, lane) => mesh.Vertices[row].Position[lane]);
        var normals = writer.Floats(count, 3, (row, lane) => (basis?.Normals[row] ?? mesh.Vertices[row].Normal)[lane]);
        var uvs = writer.Floats(count, 2, (row, lane) => mesh.Vertices[row].TexCoord[lane]);
        var indices = writer.Indices(mesh.Indices, count);
        var tangents = basis is null ? null : writer.Floats(count, 4, (row, lane) => basis.Tangents[row][lane]);
        WorldMotion? movement = motion is null ? null : new(motion.Profile,
            writer.Floats(count, 4, (row, lane) => motion.Colors[row][lane]), motion.Constants, motion.Rates);
        var result = new WorldGeometry(id, mesh.TagHash, mesh.PartIndex, mesh.LogicalPartIndex, positions, normals,
            uvs, indices, tangents, basis is null ? "Diagnostic" : NativeMeshBasis.StaticProfile, movement)
        {
            Colors = colors is null ? null : writer.Bytes(count, 4, (row, lane) => colors[row][lane]),
            TexCoordsZw = zw is null ? null : writer.Floats(count, 2, (row, lane) => zw[row][lane])
        };
        shared.Add(key, result);
        return result;
    }
}
