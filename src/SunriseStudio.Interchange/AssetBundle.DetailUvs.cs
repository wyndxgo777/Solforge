using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Detail-UV derivation of AssetBundle: reads the TEXCOORD2 stream of entity parts to rebuild the detail UV map. */
public static partial class AssetBundle
{
    /**
     * UV map 1 of an entity part: UV0 times the TEXCOORD2 stream, as its material's vertex shader writes TEXCOORD3.zw.
     * @return Null when the vertex shader does not write the detail UVs or the stream is missing.
     */
    private static float[][]? DetailTexCoords(PackageCatalog catalog, MeshData mesh, GameMaterialReader reader)
    {
        if (mesh.Native is not { Kind: NativeMeshKind.Entity, InputLayoutVerified: true } native
            || mesh.SourceVertexIndices.Length != mesh.Vertices.Length) return null;
        var element = native.InputElements.FirstOrDefault(value => value is { Semantic: "TEXCOORD", SemanticIndex: 2, PerInstance: false });
        if (element is null || element.Slot >= native.VertexStreams.Length
            || element.Format is not (NativeVertexFormat.R16G16Float or NativeVertexFormat.R32G32Float)) return null;
        try
        {
            if (!catalog.TryGetEntry(mesh.MaterialTag, out var entry) || entry.Reference != 0x808071E8
                || reader.Read(mesh.MaterialTag).VertexShader is not { } shader
                || !ShaderVaryings.WritesDetailUv(shader.Disassembly))
                return null;
        }
        catch (Exception error) when (Recoverable(error) || error is IOException) { return null; }
        var stream = native.VertexStreams[element.Slot];
        var uvs = new float[mesh.Vertices.Length][];
        for (int vertex = 0; vertex < uvs.Length; vertex++)
        {
            int offset = checked((int)mesh.SourceVertexIndices[vertex] * stream.Stride + element.Offset);
            if (offset + 8 > stream.Data.Length) return null;
            var span = stream.Data.AsSpan(offset);
            Vector2 scale = element.Format == NativeVertexFormat.R16G16Float
                ? new((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(span)), (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(span[2..])))
                : new(BitConverter.ToSingle(span), BitConverter.ToSingle(span[4..]));
            var uv = mesh.Vertices[vertex].TexCoord * scale;
            uvs[vertex] = [uv.X, uv.Y];
        }
        return uvs;
    }
}
