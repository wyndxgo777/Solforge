using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SunriseStudio.Formats;

/** Checks glTF skin reading: joint order, hierarchy, bind shape, local skin indices and Sunrise bone properties. */
internal static class GltfSkinChecks
{
    /** Joint order and bind shape determine skin coordinates; the mesh node transform is ignored. */
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sunrise-gltf-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "skin.gltf");
        try
        {
            var fixture = Fixture();
            File.WriteAllText(path, fixture.ToJsonString());
            var result = GltfInterchange.ReadSkinned(path);
            Check(result.Geometry.Positions.Length == 3 && result.Joints.Count == 2
                && result.Joints[0].ParentIndex == 1 && result.Joints[1].ParentIndex == -1,
                "Skin joint order or hierarchy changed.");
            Check(Vector3.Distance(result.Geometry.Positions[0], new(4, -6, 5)) < .0001f
                && result.Influences[0].Bone0 == 1 && result.Influences[1].Bone0 == 0,
                "Bind shape or local skin indices were not preserved.");
            Check(result.Joints[0].SourceBoneIndex == 7 && result.Joints[0].SourceEntityTag == 0x80B82FE3,
                "Sunrise bone properties were not read.");
            foreach (var bone in result.Joints)
                Check(MatrixError(bone.InverseBindObject * bone.BindObject, Matrix4x4.Identity) < .0001f,
                    "Converted inverse bind does not undo its bone bind.");
            fixture["nodes"]![3]!["translation"] = JsonSerializer.SerializeToNode(new[] { -999, 300, 500 });
            File.WriteAllText(path, fixture.ToJsonString());
            Check(GltfInterchange.ReadSkinned(path).Geometry.Positions.SequenceEqual(result.Geometry.Positions),
                "A skinned mesh node transform altered the bind geometry.");
            void Refuse(Action<JsonNode> edit)
            {
                var changed = fixture.DeepClone(); edit(changed);
                File.WriteAllText(path, changed.ToJsonString());
                try { GltfInterchange.ReadSkinned(path); }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; }
                throw new InvalidOperationException("Invalid glTF skin was accepted.");
            }
            Refuse(root => root["skins"]![0]!["joints"]![1] = 2);
            Refuse(root => root["nodes"]![2]!["translation"] = JsonSerializer.SerializeToNode(new[] { 0, 99, 0 }));
            Refuse(root => root["meshes"]![0]!["primitives"]![0]!["attributes"]!["JOINTS_1"] = 3);
            Refuse(root => root["skins"]![0]!.AsObject().Remove("inverseBindMatrices"));
            Refuse(root => root["skins"]![0]!["skeleton"] = 3);
            Refuse(root => root["accessors"]![0]!["normalized"] = true);
            Refuse(root => root["accessors"]![0]!["byteOffset"] = 1);
            Refuse(root => root["bufferViews"]![0]!["byteStride"] = 14);
            Refuse(root =>
            {
                root["nodes"]![1]!.AsObject().Remove("children");
                root["nodes"]![2]!["translation"] = JsonSerializer.SerializeToNode(new[] { 1, 3, 3 });
                root["scenes"]![0]!["nodes"] = JsonSerializer.SerializeToNode(new[] { 0, 2 });
            });
            var complex = Fixture(true);
            File.WriteAllText(path, complex.ToJsonString());
            var rotated = GltfInterchange.ReadSkinned(path);
            var conversion = new Matrix4x4(1,0,0,0, 0,0,1,0, 0,-1,0,0, 0,0,0,1);
            var shape = BindShape(true) * conversion;
            Vector3[] points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
            Check(rotated.Geometry.Positions.Zip(points).All(pair => Vector3.Distance(pair.First,
                Vector3.Transform(pair.Second, shape)) < .0001f), "Rotated/scaled bind shape changed skin coordinates.");
            Matrix4x4.Invert(shape, out var undo);
            var normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, Matrix4x4.Transpose(undo)));
            Check(rotated.Geometry.Normals.All(value => Vector3.Distance(value, normal) < .0001f)
                && rotated.Geometry.Indices.SequenceEqual(new uint[] { 0, 2, 1 }),
                "Reflected bind shape lost its normal transform or winding.");
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
        }
    }

    private static JsonNode Fixture(bool complex = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var accessors = new List<object>(); var views = new List<object>();
        int Floats(float[] values, string type, int width)
        {
            int start = (int)stream.Position;
            foreach (float value in values) writer.Write(value);
            int index = accessors.Count;
            views.Add(new { buffer = 0, byteOffset = start, byteLength = values.Length * 4 });
            accessors.Add(new { bufferView = index, componentType = 5126, count = values.Length / width, type });
            return index;
        }
        int UInts(ushort[] values, string type, int width)
        {
            int start = (int)stream.Position;
            foreach (ushort value in values) writer.Write(value);
            int index = accessors.Count;
            views.Add(new { buffer = 0, byteOffset = start, byteLength = values.Length * 2 });
            accessors.Add(new { bufferView = index, componentType = 5123, count = values.Length / width, type });
            while (stream.Position % 4 != 0) writer.Write((byte)0);
            return index;
        }
        int positions = Floats([0,0,0, 1,0,0, 0,1,0], "VEC3", 3);
        int normals = Floats([0,0,1, 0,0,1, 0,0,1], "VEC3", 3);
        int uv = Floats([0,0, 1,0, 0,1], "VEC2", 2);
        int jointIndices = UInts([1,0,0,0, 0,0,0,0, 0,1,0,0], "VEC4", 4);
        int weights = Floats([1,0,0,0, 1,0,0,0, .25f,.75f,0,0], "VEC4", 4);
        int triangles = UInts([0,1,2], "SCALAR", 1);
        var parent = complex ? Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromYawPitchRoll(.19f, .23f, -.17f)
            * Matrix4x4.CreateTranslation(1, 2, 3) : Matrix4x4.CreateTranslation(1, 2, 3);
        var childLocal = complex ? Matrix4x4.CreateScale(.75f) * Matrix4x4.CreateRotationY(.32f)
            * Matrix4x4.CreateTranslation(0, 1, 0) : Matrix4x4.CreateTranslation(0, 1, 0);
        var child = childLocal * parent;
        Matrix4x4.Invert(child, out var inverseChild); Matrix4x4.Invert(parent, out var inverseParent);
        var bindShape = BindShape(complex);
        int inverse = Floats(AffineValues(bindShape * inverseChild).Concat(AffineValues(bindShape * inverseParent)).ToArray(), "MAT4", 16);
        var document = JsonSerializer.SerializeToNode(new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new object[] { new { name = "Root", children = new[] { 1, 3 } },
                new { name = "Parent", children = new[] { 2 }, translation = new[] { 1,2,3 } },
                new { name = "Child", translation = new[] { 0,1,0 }, extras = new { sunriseBoneIndex = 7, sunriseEntityTag = "80B82FE3" } },
                new { name = "Mesh", mesh = 0, skin = 0, translation = new[] { 100,200,300 } } },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = positions,
                NORMAL = normals, TEXCOORD_0 = uv, JOINTS_0 = jointIndices, WEIGHTS_0 = weights }, indices = triangles } } } },
            skins = new[] { new { joints = new[] { 2, 1 }, inverseBindMatrices = inverse } },
            accessors, bufferViews = views,
            buffers = new[] { new { byteLength = stream.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(stream.ToArray()) } }
        })!;
        if (complex)
        {
            document["nodes"]![1]!.AsObject().Remove("translation");
            document["nodes"]![1]!["matrix"] = JsonSerializer.SerializeToNode(AffineValues(parent));
            document["nodes"]![2]!.AsObject().Remove("translation");
            document["nodes"]![2]!["matrix"] = JsonSerializer.SerializeToNode(AffineValues(childLocal));
        }
        return document;
    }

    private static Matrix4x4 BindShape(bool complex) => complex
        ? Matrix4x4.CreateScale(2, .6f, -1.1f) * Matrix4x4.CreateRotationX(.3f) * Matrix4x4.CreateTranslation(4, 5, 6)
        : Matrix4x4.CreateTranslation(4, 5, 6);

    private static float[] AffineValues(Matrix4x4 value)
    { value.M14 = value.M24 = value.M34 = 0; value.M44 = 1; return Values(value); }
    private static float[] Values(Matrix4x4 value) => [value.M11,value.M12,value.M13,value.M14,
        value.M21,value.M22,value.M23,value.M24,value.M31,value.M32,value.M33,value.M34,value.M41,value.M42,value.M43,value.M44];
    private static float MatrixError(Matrix4x4 first, Matrix4x4 second) => Values(first).Zip(Values(second))
        .Max(pair => MathF.Abs(pair.First - pair.Second));
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
