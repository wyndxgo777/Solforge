using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;

/** Checks glTF skin lane merging and canonical zero padding so exported and re-imported skins match the native baseline. */
internal static class BundleSkinChecks
{
    private static readonly MethodInfo Merge = typeof(AssetBundle).Assembly.GetType("SunriseStudio.Interchange.BundleSceneWriter")!
        .GetMethod("GltfInfluences", BindingFlags.Static | BindingFlags.NonPublic)!;

    /** Duplicate lanes combine in first-use order; native source arrays and zero lanes stay separate. */
    internal static void Synthetic()
    {
        int[][] joints = [[4, 2, 4, 2], [-1, 4, 999, -1]];
        float[][] weights = [[.125f, .25f, .375f, .25f], [0, 1, 0, 0]];
        var result = Run(joints, weights, 8);
        Check(result.Joints.SequenceEqual(new uint[] { 4, 2, 0, 0, 4, 0, 0, 0 })
            && result.Weights.SequenceEqual(new float[] { .5f, .5f, 0, 0, 1, 0, 0, 0 }), "glTF skin lane merge changed.");
        Check(joints[0].SequenceEqual([4, 2, 4, 2]) && weights[0].SequenceEqual([.125f, .25f, .375f, .25f]),
            "glTF export changed native baseline skin lanes.");
        var repeated = Run(joints, weights, 8);
        Check(result.Joints.SequenceEqual(repeated.Joints) && result.Weights.SequenceEqual(repeated.Weights),
            "glTF skin merge is nondeterministic.");
        Console.WriteLine("bundle_skin duplicate_lanes=merged zero_padding=canonical native_baseline=unchanged");
    }

    /** An installed repeated-lane vertex must preserve its effective weight on each native bone. */
    internal static void Owned(PackageCatalog catalog)
    {
        var rig = new SkeletonReader(catalog).Read(0x80B82FE3);
        var vertices = new ModelSkinReader(catalog).Read(0x80C4424F, rig).SelectMany(binding => binding.Vertices);
        var source = vertices.First(value =>
        {
            int[] bones = [value.Bone0, value.Bone1, value.Bone2, value.Bone3];
            float[] weights = [value.Weights.X, value.Weights.Y, value.Weights.Z, value.Weights.W];
            return Enumerable.Range(0, 4).Where(i => weights[i] > 0).GroupBy(i => bones[i]).Any(group => group.Count() > 1);
        });
        int[] originalJoints = [source.Bone0, source.Bone1, source.Bone2, source.Bone3];
        float[] originalWeights = [source.Weights.X, source.Weights.Y, source.Weights.Z, source.Weights.W];
        var merged = Run([originalJoints], [originalWeights], rig.Bones.Count);
        var expected = Enumerable.Range(0, 4).Where(i => originalWeights[i] > 0).GroupBy(i => originalJoints[i])
            .ToDictionary(group => group.Key, group => group.Sum(i => originalWeights[i]));
        var actual = Enumerable.Range(0, 4).Where(i => merged.Weights[i] > 0)
            .ToDictionary(i => (int)merged.Joints[i], i => merged.Weights[i]);
        Check(expected.Count == actual.Count && expected.All(pair => actual[pair.Key] == pair.Value)
            && MathF.Abs(originalWeights.Sum() - merged.Weights.Sum()) < 1e-6f,
            "The owned glTF skin merge changed effective bone weights.");
        WriterGeometry(rig, originalJoints, originalWeights);
        Console.WriteLine($"bundle_skin_owned model=80C4424F source_joints={string.Join(',', originalJoints)} effective_joints={actual.Count} weights=preserved");
    }

    /** Skin canonicalization must leave the exported geometry and native baseline unchanged. */
    private static void WriterGeometry(SkeletonRig rig, int[] joints, float[] weights)
    {
        string path = Path.Combine(Path.GetTempPath(), "sunrise-bundle-skin-" + Guid.NewGuid().ToString("N") + ".gltf");
        var geometry = new BundleGeometry("fixture", [[1, 2, 3], [-4, 5, 6], [7, 8, -9]],
            [[0, 0, 1], [0, 0, 1], [0, 0, 1]], [[.25f, .75f], [.5f, 1.25f], [-.5f, 0]], [0, 1, 2],
            [joints, joints, joints], [weights, weights, weights]);
        byte[] baseline = JsonSerializer.SerializeToUtf8Bytes(geometry);
        var info = new BundleMesh("fixture", 0x80C4424F, 0x300, 0, 0, 0, "", "", [], true, "");
        var method = Merge.DeclaringType!.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clips = Array.CreateInstance(method.GetParameters()[3].ParameterType.GetGenericArguments()[0], 0);
        try
        {
            method.Invoke(null, [path, new (BundleMesh, BundleGeometry)[] { (info, geometry) }, rig, clips, null]);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var attributes = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            string uri = root.GetProperty("buffers")[0].GetProperty("uri").GetString()!;
            byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
            float[] Values(string name, int width)
            {
                var accessor = root.GetProperty("accessors")[attributes.GetProperty(name).GetInt32()];
                var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
                int offset = view.GetProperty("byteOffset").GetInt32();
                return Enumerable.Range(0, accessor.GetProperty("count").GetInt32() * width)
                    .Select(index => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + index * 4))).ToArray();
            }
            Check(Values("POSITION", 3).SequenceEqual(geometry.Positions.SelectMany(value => value))
                && Values("TEXCOORD_0", 2).SequenceEqual(geometry.TexCoords.SelectMany(value => new[] { value[0], 1 - value[1] }))
                && Values("WEIGHTS_0", 4).SequenceEqual(Enumerable.Repeat(Run([joints], [weights], rig.Bones.Count).Weights, 3).SelectMany(value => value))
                && baseline.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(geometry)),
                "glTF lane merging changed positions, UV conversion, or the native baseline.");
        }
        finally { File.Delete(path); }
    }

    private static (uint[] Joints, float[] Weights) Run(int[][] joints, float[][] weights, int bones)
    {
        try { return ((uint[], float[]))Merge.Invoke(null, [joints, weights, bones])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
