using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks native tangent-basis handling: declarations, index order, position transform and authored tangent arrays. */
internal static class NativeBasisChecks
{
    /** Exported basis vectors must match the authored shader inputs, not generated face normals. */
    internal static void Check(PackageCatalog catalog, AssetBundleExportResult bundle)
    {
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(bundle.Manifest.RootTag);
        int checkedVertices = 0, differences = 0;
        foreach (var info in bundle.Manifest.Meshes)
        {
            var geometry = JsonSerializer.Deserialize<BundleGeometry>(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(bundle.ManifestPath)!, info.Path)))!;
            var source = scene.Instances.First(value => value.Mesh.TagHash == info.ModelTag
                && value.Mesh.PartIndex == info.PartIndex && value.Mesh.LodLevel == info.Lod
                && value.Mesh.MaterialTag == info.MaterialTag && value.ModelSourceConfigTag == info.ConfigTag).Mesh;
            if (geometry.Basis != NativeMeshBasis.StaticProfile) continue;
            Require(source.Native is { NativeDeclarationId: 123, RenderStage: 0 }, "Native basis has no matching declaration.");
            var native = source.Native!;
            Require(geometry.Tangents?.Length == source.Vertices.Length, "Authored tangent array is incomplete.");
            Require(geometry.Indices.SequenceEqual(source.Indices), "Native index ordering changed.");
            for (int i = 0; i < source.Vertices.Length; i++)
            {
                int vertex = checked((int)source.SourceVertexIndices[i]);
                var p = native.VertexStreams[0]; var a = native.VertexStreams[1];
                Vector3 expectedPosition = Three(p.Data, vertex * p.Stride) * native.PositionScale.X
                    + new Vector3(native.PositionOffset.X, native.PositionOffset.Y, native.PositionOffset.Z);
                Vector3 rawNormal = Three(a.Data, vertex * a.Stride + 4);
                float divisor = rawNormal.Length(), sign = MathF.Sign(native.PositionScale.X);
                Vector3 expectedNormal = rawNormal / divisor * sign;
                Vector3 expectedTangent = Three(a.Data, vertex * a.Stride + 12) / divisor * sign;
                float expectedW = One(a.Data, vertex * a.Stride + 18);
                Vector2 expectedUv = new(One(a.Data, vertex * a.Stride) * native.UvTransform.X + native.UvTransform.Z,
                    One(a.Data, vertex * a.Stride + 2) * native.UvTransform.X + native.UvTransform.W);
                Require(Vector3.Distance(V3(geometry.Positions[i]), expectedPosition) < .000002f, "Native position transform changed.");
                Require(Vector2.Distance(new(geometry.TexCoords[i][0], geometry.TexCoords[i][1]), expectedUv) < .000001f, "Native UV transform changed.");
                Require(Vector3.Distance(V3(geometry.Normals[i]), expectedNormal) < .000001f, "Export still uses generated normals.");
                Require(Vector3.Distance(V3(geometry.Tangents![i]), expectedTangent) < .000001f
                    && geometry.Tangents[i][3] == expectedW, "Exported tangent or handedness differs from the shader input.");
                if (Vector3.Dot(expectedNormal, source.Vertices[i].Normal) < .996f) differences++;
                checkedVertices++;
            }
            var unchanged = new BundleMeshEdit(geometry.Id, geometry.Positions, geometry.Indices,
                geometry.Joints, geometry.Weights, geometry.Normals, geometry.TexCoords);
            Require(BundleMeshCodec.Apply(catalog, source, geometry, unchanged).Count == 0,
                "Authored-basis no-op reimport changed native bytes.");
        }
        if (bundle.Manifest.RootTag == 0x80FEBE71)
            Require(checkedVertices == 1160 && differences > 300, "The target's authored-normal regression fixture is incomplete.");
        Console.WriteLine(checkedVertices == 0 ? "native_basis vertices=0 status=unsupported_profile"
            : $"native_basis vertices={checkedVertices} generated_normal_differences={differences} indices=preserved positions=preserved uv=preserved normals=authored tangents=authored noop=preserved");
    }

    /** A mesh with zero-tangent vertices exports with decoded normals instead of failing the bundle. */
    internal static void Refused(PackageCatalog catalog, string output)
    {
        // Altar of Flame rock: part 1 has two used vertices with tangent (0,0,0).
        const uint model = 0x814021FE;
        var bundle = AssetBundle.Export(catalog, model, output, new(IncludeAnimations: false));
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(bundle.Manifest.RootTag);
        int refused = 0;
        foreach (var info in bundle.Manifest.Meshes)
        {
            var source = scene.Instances.Select(value => value.Mesh).First(mesh => mesh.TagHash == info.ModelTag
                && mesh.PartIndex == info.PartIndex && mesh.LodLevel == info.Lod && mesh.MaterialTag == info.MaterialTag);
            if (source.Native is not { } native || !NativeMeshBasis.IsSupported(catalog, native)) continue;
            try { NativeMeshBasis.Read(catalog, source); continue; }
            catch (InvalidDataException) { refused++; }
            var geometry = JsonSerializer.Deserialize<BundleGeometry>(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(bundle.ManifestPath)!, info.Path)))!;
            Require(geometry.Basis == "Diagnostic" && geometry.Tangents is null, "Refused basis still claims authored tangents.");
            Require(geometry.Normals.Select(V3).Zip(source.Vertices).All(pair => Vector3.Distance(pair.First, pair.Second.Normal) < .000001f),
                "Refused basis did not fall back to decoded normals.");
            Require(bundle.Manifest.Notices.Any(notice => notice.Surface == "Normals" && notice.TagHash == info.ModelTag),
                "Refused basis has no Normals notice.");
            var unchanged = new BundleMeshEdit(geometry.Id, geometry.Positions, geometry.Indices,
                geometry.Joints, geometry.Weights, geometry.Normals, geometry.TexCoords);
            Require(BundleMeshCodec.Apply(catalog, source, geometry, unchanged).Count == 0,
                "Refused-basis no-op reimport changed native bytes.");
        }
        Require(refused > 0, "Refused-basis fixture no longer has a refused part.");
        Check(catalog, bundle);
        Console.WriteLine($"native_basis refused_parts={refused} export=pass normals=decoded noop=preserved");
    }

    private static Vector3 Three(byte[] bytes, int offset) => new(One(bytes, offset), One(bytes, offset + 2), One(bytes, offset + 4));
    private static float One(byte[] bytes, int offset) => Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) / 32767f);
    private static Vector3 V3(float[] value) => new(value[0], value[1], value[2]);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
