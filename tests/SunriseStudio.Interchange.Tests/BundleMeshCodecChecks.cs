using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks bundle mesh edits against the native vertex codecs: unchanged geometry makes no edit and UV/position edits touch only their own lanes. */
internal static class BundleMeshCodecChecks
{
    /** Every supported UV encoding preserves other lanes and merges with position edits. */
    internal static void Synthetic(string root)
    {
        foreach (var format in new[] { NativeVertexFormat.R16G16SNorm, NativeVertexFormat.R16G16B16A16SNorm,
            NativeVertexFormat.R16G16Float, NativeVertexFormat.R32G32Float, NativeVertexFormat.R32G32B32A32Float })
        {
            string folder = Path.Combine(root, "codec-" + format);
            var mesh = Fixture(folder, format);
            using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
            var baseline = Geometry(mesh);
            var unchanged = Edit(baseline);
            Check(BundleMeshCodec.Apply(catalog, mesh, baseline, unchanged).Count == 0,
                "Unchanged geometry produced native edits.");
            float[][] uv = Copy(baseline.TexCoords); uv[0] = [4, 6];
            float[][] positions = Copy(baseline.Positions); positions[0] = [.5f, 0, 0];
            var edits = BundleMeshCodec.Apply(catalog, mesh, baseline,
                unchanged with { Positions = positions, TexCoords = uv });
            var native = mesh.Native!;
            Check(edits.Count == 2 && Read16(edits[native.VertexStreams[0].DataTag], 0) == 16384,
                "Position edit was omitted from a combined geometry change.");
            byte[] attributes = edits[native.VertexStreams[1].DataTag];
            float x = Decode(attributes, 4, format), y = Decode(attributes, IsFloat(format) ? 8 : 6, format);
            Check(Math.Abs(x - .5f) < .00004 && Math.Abs(y - .5f) < .00004,
                "UV encoding used the static Y transform instead of X.");
            int size = IsFloat(format) ? 8 : 4;
            for (int i = 0; i < attributes.Length; i++)
                if (i < 4 || i >= 4 + size)
                    Check(attributes[i] == native.VertexStreams[1].Data[i], "UV edit changed another attribute lane.");
            Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                unchanged with { Indices = [2, 1, 0] }));
            Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                unchanged with { Positions = [[float.NaN, 0, 0], [1, 0, 0], [0, 1, 0]] }));
            float[][] normals = Copy(baseline.Normals); normals[0] = [1, 0, 0];
            Reject<NotSupportedException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                unchanged with { Normals = normals }));
            var joints = Enumerable.Range(0, 3).Select(_ => new[] { 0, 1, 0, 0 }).ToArray();
            var weights = Enumerable.Range(0, 3).Select(_ => new[] { .75f, .25f, 0, 0 }).ToArray();
            var skinned = baseline with { Joints = joints, Weights = weights };
            Check(BundleMeshCodec.Apply(catalog, mesh, skinned,
                unchanged with { Joints = joints, Weights = weights }).Count == 0,
                "Unchanged skin arrays were rejected.");
            int[][] changedJoints = joints.Select(row => (int[])row.Clone()).ToArray(); changedJoints[0][0] = 2;
            Reject<NotSupportedException>(() => BundleMeshCodec.Apply(catalog, mesh, skinned,
                unchanged with { Joints = changedJoints, Weights = weights }));
            if (format is NativeVertexFormat.R16G16SNorm or NativeVertexFormat.R16G16B16A16SNorm)
            {
                uv[0] = [30, 6];
                Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                    unchanged with { TexCoords = uv }));
            }
            if (format == NativeVertexFormat.R16G16Float)
            {
                uv[0] = [float.MaxValue, 6];
                Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                    unchanged with { TexCoords = uv }));
            }
            var stale = (byte[])native.VertexStreams[1].Data.Clone(); stale[^1] ^= 1;
            catalog.ReadOverride = tag => tag == native.VertexStreams[1].DataTag ? stale : null;
            uv[0] = [4, 6];
            Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
                unchanged with { TexCoords = uv }));
        }
        SharedStream(root);
        SharedVertices(root);
        Console.WriteLine("bundle_mesh uv_formats=5 static_y_scale=x shared_payload=merged topology=checked skin=preserved unsupported_normals=rejected stale_source=rejected");
    }

    /** A real accepted shader proves normal encoding while the native normal W lane stays intact. */
    internal static void Owned(PackageCatalog catalog)
    {
        var scene = new AssetSceneLoader(catalog).LoadMesh(0x80B9EEE1);
        var mesh = scene.Instances.Select(x => x.Mesh).First(x => x.Native is
            { Kind: NativeMeshKind.Static, NativeDeclarationId: 123, RenderStage: 0 });
        var baseline = Geometry(mesh);
        var native = mesh.Native!; var stream = native.VertexStreams[1];
        var basis = NativeMeshBasis.Read(catalog, mesh);
        var authored = baseline with { Basis = NativeMeshBasis.StaticProfile,
            Normals = basis.Normals.Select(value => new[] { value.X, value.Y, value.Z }).ToArray(),
            Tangents = basis.Tangents.Select(value => new[] { value.X, value.Y, value.Z, value.W }).ToArray() };
        Check(BundleMeshCodec.Apply(catalog, mesh, authored, Edit(authored)).Count == 0
            && BundleMeshCodec.Apply(catalog, mesh, authored, Edit(authored) with { Normals = null }).Count == 0,
            "Unchanged authored normals or tangents produced native edits.");
        var badTangents = Copy(authored.Tangents!); badTangents[0][3] *= -1;
        Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh,
            authored with { Tangents = badTangents }, Edit(authored)));
        Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh,
            authored with { Tangents = null }, Edit(authored)));
        Reject<NotSupportedException>(() => BundleMeshCodec.Apply(catalog, mesh,
            authored with { Basis = "Unknown" }, Edit(authored)));
        foreach (var geometry in new[] { baseline, authored })
            CheckNormalEdit(catalog, mesh, geometry);
        Check(catalog.Read(stream.DataTag).AsSpan().SequenceEqual(stream.Data), "Mesh codec mutated installed bytes.");
        Console.WriteLine($"bundle_mesh owned_profile=verified model={mesh.TagHash:X8} authored_noop=preserved diagnostic_legacy=accepted normal_w=preserved tangent_w=preserved invalid_basis=rejected");
    }

    /** A normal edit must preserve handedness and every unrelated native vertex. */
    private static void CheckNormalEdit(PackageCatalog catalog, MeshData mesh, BundleGeometry baseline)
    {
        var stream = mesh.Native!.VertexStreams[1];
        float[][] normals = Copy(baseline.Normals);
        Vector3 wanted = Math.Abs(baseline.Normals[0][0]) < .5f ? Vector3.UnitX : Vector3.UnitZ;
        normals[0] = [wanted.X, wanted.Y, wanted.Z];
        var edits = BundleMeshCodec.Apply(catalog, mesh, baseline, Edit(baseline) with { Normals = normals });
        Check(edits.Count == 1 && edits.ContainsKey(stream.DataTag), "Owned normal edit changed unexpected native assets.");
        byte[] bytes = edits[stream.DataTag];
        int first = checked((int)mesh.SourceVertexIndices[0] * stream.Stride);
        var actual = new Vector3(Read16(bytes, first + 4), Read16(bytes, first + 6), Read16(bytes, first + 8)) / 32767f;
        Check(Vector3.Distance(actual, wanted) < .0001f, "Owned normal did not encode to the requested native vector.");
        foreach (uint vertex in mesh.SourceVertexIndices)
        {
            int p = checked((int)vertex * stream.Stride);
            Check(Read16(bytes, p + 10) == Read16(stream.Data, p + 10), "Normal edit overwrote unknown W.");
            Check(Read16(bytes, p + 18) == Read16(stream.Data, p + 18), "Normal edit changed native tangent handedness.");
            if (vertex != mesh.SourceVertexIndices[0])
            {
                Check(bytes.AsSpan(p, stream.Stride).SequenceEqual(stream.Data.AsSpan(p, stream.Stride)),
                    "Normal edit changed an unrelated native vertex.");
                continue;
            }
            var n = new Vector3(Read16(bytes, p + 4), Read16(bytes, p + 6), Read16(bytes, p + 8)) / 32767f;
            var t = new Vector3(Read16(bytes, p + 12), Read16(bytes, p + 14), Read16(bytes, p + 16)) / 32767f;
            Check(Math.Abs(Vector3.Dot(n, t)) < .001f, "Recomputed native tangent is not orthogonal to its normal.");
        }
    }

    private static void SharedStream(string root)
    {
        string folder = Path.Combine(root, "codec-shared");
        var mesh = Fixture(folder, NativeVertexFormat.R32G32Float, true);
        using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
        var baseline = Geometry(mesh);
        var positions = Copy(baseline.Positions); positions[0] = [.5f, 0, 0];
        var uv = Copy(baseline.TexCoords); uv[0] = [4, 7];
        var edits = BundleMeshCodec.Apply(catalog, mesh, baseline, Edit(baseline) with { Positions = positions, TexCoords = uv });
        byte[] bytes = edits.Values.Single();
        Check(BinaryPrimitives.ReadSingleLittleEndian(bytes) == .5f
            && BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(12)) == .5f
            && BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(16)) == .5f,
            "UV edit discarded a position edit in the same native stream.");
    }

    private static void SharedVertices(string root)
    {
        string folder = Path.Combine(root, "codec-aliased-vertices");
        var source = Fixture(folder, NativeVertexFormat.R16G16SNorm);
        var vertices = (SceneVertex[])source.Vertices.Clone(); vertices[1] = vertices[0];
        var mesh = new MeshData { Vertices = vertices, Indices = source.Indices,
            SourceVertexIndices = [0, 0, 2], Native = source.Native };
        using var catalog = PackageCatalog.Open(folder, cacheOptions: new() { Enabled = false });
        var baseline = Geometry(mesh);
        var uv = Copy(baseline.TexCoords); uv[0] = [4, 6];
        Reject<InvalidDataException>(() => BundleMeshCodec.Apply(catalog, mesh, baseline,
            Edit(baseline) with { TexCoords = uv }));
    }

    private static MeshData Fixture(string folder, NativeVertexFormat format, bool shared = false)
    {
        const uint first = 0x80A00000;
        int positionStride = shared ? 48 : 8, attributeStride = IsFloat(format) ? 20 : 12;
        byte[] positions = new byte[positionStride * 3], attributes = Enumerable.Repeat((byte)0x5A, attributeStride * 3).ToArray();
        Vector3[] points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        for (int vertex = 0; vertex < 3; vertex++)
        {
            for (int axis = 0; axis < 3; axis++)
                if (shared) BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(vertex * positionStride + axis * 4), points[vertex][axis]);
                else BinaryPrimitives.WriteInt16LittleEndian(positions.AsSpan(vertex * positionStride + axis * 2), (short)(points[vertex][axis] * 32767));
            if (!shared) BinaryPrimitives.WriteInt16LittleEndian(positions.AsSpan(vertex * positionStride + 6), 0x1234);
            attributes.AsSpan(vertex * attributeStride + 4, IsFloat(format) ? 8 : 4).Clear();
        }
        Directory.CreateDirectory(Path.Combine(folder, "packages"));
        PackageWriter.WriteNew(Path.Combine(folder, "packages", "w64_codec_0100_0.pkg"), 0x100,
            [new(uint.MaxValue, (40 << 9) | (4 << 6), positions),
             new(uint.MaxValue, (40 << 9) | (4 << 6), attributes)]);
        var streams = shared ? new[] { new NativeVertexStream(0, first, positionStride, 0, positions) }
            : new[] { new NativeVertexStream(0, first, positionStride, 0, positions),
                new NativeVertexStream(0, first + 1, attributeStride, 0, attributes) };
        var native = new NativeMeshData
        {
            Kind = shared ? NativeMeshKind.Entity : NativeMeshKind.Static, VertexStreams = streams,
            IndexData = [], SourceHeader = [], PositionScale = Vector4.One, InputLayoutVerified = true,
            UvTransform = shared ? new(2, 4, 3, 5) : new(2, 99, 3, 5),
            InputElements = [new("TEXCOORD", 0, shared ? 0 : 1, shared ? 12 : 4, format)]
        };
        return new() { Native = native, Indices = [0, 1, 2], SourceVertexIndices = [0, 1, 2],
            Vertices = points.Select(p => new SceneVertex(p, Vector3.UnitZ, new(3, 5), Vector4.One)).ToArray() };
    }

    private static BundleGeometry Geometry(MeshData mesh) => new("mesh",
        mesh.Vertices.Select(x => new[] { x.Position.X, x.Position.Y, x.Position.Z }).ToArray(),
        mesh.Vertices.Select(x => new[] { x.Normal.X, x.Normal.Y, x.Normal.Z }).ToArray(),
        mesh.Vertices.Select(x => new[] { x.TexCoord.X, x.TexCoord.Y }).ToArray(), mesh.Indices);
    private static BundleMeshEdit Edit(BundleGeometry source) => new(source.Id,
        Copy(source.Positions), source.Indices.ToArray(), Normals: Copy(source.Normals), TexCoords: Copy(source.TexCoords));
    private static float[][] Copy(float[][] source) => source.Select(x => (float[])x.Clone()).ToArray();
    private static bool IsFloat(NativeVertexFormat format) => format is NativeVertexFormat.R32G32Float or NativeVertexFormat.R32G32B32A32Float;
    private static short Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset));
    private static float Decode(byte[] bytes, int offset, NativeVertexFormat format) => IsFloat(format)
        ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)) : format == NativeVertexFormat.R16G16Float
            ? (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset))) : Read16(bytes, offset) / 32767f;
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException($"Expected {typeof(T).Name}."); }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidDataException(message); }
}
