using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** Decoded per-vertex normals and tangents of a mesh. */
public sealed record NativeSurfaceBasis(Vector3[] Normals, Vector4[] Tangents);

/** Authored tangent frames are decoded only when their input declaration and shader agree. */
public static class NativeMeshBasis
{
    public const string StaticProfile = "Native static123";
    private const string StaticShader = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";

    /** This shader consumes NORMAL.xyz and TANGENT.xyzw directly from declaration 123. */
    public static bool IsSupported(PackageCatalog catalog, NativeMeshData native)
    {
        if (native.Kind != NativeMeshKind.Static || !native.InputLayoutVerified
            || native.NativeDeclarationId != 123 || native.RenderStage != 0
            || native.VertexStreams.Length != 2 || native.VertexStreams[0].Stride != 8
            || native.VertexStreams[1].Stride != 20 || native.VertexStreams.Any(value => value.Type != 0)
            || !native.InputElements.SequenceEqual(new NativeInputElement[]
            {
                new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
                new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
                new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
                new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm)
            }) || !catalog.ContainsTag(native.VertexShaderDataTag)) return false;
        return Convert.ToHexString(SHA256.HashData(catalog.Read(native.VertexShaderDataTag))) == StaticShader;
    }

    /** Native bitangents equal cross(N,T)*W; tangent length uses the normal's length. */
    public static NativeSurfaceBasis Read(PackageCatalog catalog, MeshData mesh)
    {
        if (mesh.Native is not { } native || !IsSupported(catalog, native))
            throw new NotSupportedException("Authored normals need the verified static declaration-123 shader profile.");
        if (mesh.SourceVertexIndices.Length != mesh.Vertices.Length || !float.IsFinite(native.PositionScale.X)
            || native.PositionScale.X == 0)
            throw new InvalidDataException("Native tangent-frame vertex identities or model scale are invalid.");
        var stream = native.VertexStreams[1];
        if (!catalog.Read(stream.DataTag).AsSpan().SequenceEqual(stream.Data))
            throw new InvalidDataException("Native tangent-frame source changed after decoding.");
        float sign = MathF.Sign(native.PositionScale.X);
        var normals = new Vector3[mesh.Vertices.Length]; var tangents = new Vector4[mesh.Vertices.Length];
        for (int i = 0; i < normals.Length; i++)
        {
            int p = checked((int)mesh.SourceVertexIndices[i] * stream.Stride);
            if (p < 0 || p > stream.Data.Length - 20) throw new InvalidDataException("Native tangent frame leaves its stream.");
            Vector3 normal = Read3(stream.Data, p + 4), tangent = Read3(stream.Data, p + 12);
            float length = normal.Length(), handedness = Read1(stream.Data, p + 18);
            if (length < 1e-8f || tangent.LengthSquared() < 1e-12f || Math.Abs(handedness) < .9999f)
                throw new InvalidDataException("Native tangent frame has no finite direction or handedness.");
            normals[i] = normal * (sign / length);
            tangents[i] = new(tangent * (sign / length), handedness);
        }
        return new(normals, tangents);
    }

    private static float Read1(byte[] bytes, int offset) =>
        Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f);
    private static Vector3 Read3(byte[] bytes, int offset) =>
        new(Read1(bytes, offset), Read1(bytes, offset + 2), Read1(bytes, offset + 4));
}
