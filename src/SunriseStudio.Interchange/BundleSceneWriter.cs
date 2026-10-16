using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;

namespace SunriseStudio.Interchange;

/** Writes the bundle's glTF scene (meshes, skeletons and animation). Exports meshes, rigs and clips into glTF files. */
internal static class BundleSceneWriter
{
    /**
     * All nodes share one Z-up to Y-up root, including mesh instances and joint hierarchies.
     * @param names Display name by mesh id; a mesh not in it is named by its id. Identity stays in sunriseMeshId.
     */
    internal static void Write(string path, IReadOnlyList<(BundleMesh Info, BundleGeometry Geometry)> meshes,
        SkeletonRig? rig, IReadOnlyList<(uint Tag, string Name, SampledClip Clip)> clips,
        IReadOnlyDictionary<string, string>? names = null)
    {
        using var data = new MemoryStream();
        using var binary = new BinaryWriter(data);
        var views = new List<object>(); var accessors = new List<object>();
        var nodes = new List<Dictionary<string, object>>(); var gltfMeshes = new List<object>();
        var materials = meshes.Select(x => x.Info.MaterialTag).Distinct().ToArray();
        int Accessor(float[] values, string type, int width, bool bounds = false)
        {
            int offset = checked((int)data.Position), view = views.Count;
            foreach (float value in values)
            {
                if (!float.IsFinite(value)) throw new InvalidDataException("Export contains nonfinite geometry or animation.");
                binary.Write(value);
            }
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = values.Length * 4 });
            var fields = new Dictionary<string, object> { ["bufferView"] = view, ["componentType"] = 5126,
                ["count"] = values.Length / width, ["type"] = type };
            if (bounds)
            {
                fields["min"] = Enumerable.Range(0, width).Select(c => values.Where((_, i) => i % width == c).Min()).ToArray();
                fields["max"] = Enumerable.Range(0, width).Select(c => values.Where((_, i) => i % width == c).Max()).ToArray();
            }
            int result = accessors.Count; accessors.Add(fields); return result;
        }
        int Indices(uint[] values, int component = 5125, string type = "SCALAR", int width = 1)
        {
            int offset = checked((int)data.Position), view = views.Count;
            foreach (uint value in values)
                if (component == 5123) binary.Write(checked((ushort)value)); else binary.Write(value);
            int bytes = checked((int)data.Position) - offset;
            while (data.Position % 4 != 0) binary.Write((byte)0);
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = bytes });
            int result = accessors.Count;
            accessors.Add(new { bufferView = view, componentType = component, count = values.Length / width, type });
            return result;
        }
        if (rig is not null)
            foreach (var bone in rig.Bones)
            {
                var value = bone.BindLocal;
                var node = new Dictionary<string, object> { ["name"] = $"bone_{bone.Index:D4}_{bone.Name}",
                    ["translation"] = new[] { value.Translation.X, value.Translation.Y, value.Translation.Z },
                    ["rotation"] = new[] { value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W },
                    ["scale"] = new[] { value.Scale, value.Scale, value.Scale },
                    ["extras"] = new { sunriseBoneIndex = bone.Index, sunriseEntityTag = rig.EntityTag.ToString("X8") } };
                int[] children = rig.Bones.Where(x => x.ParentIndex == bone.Index).Select(x => x.Index).ToArray();
                if (children.Length != 0) node["children"] = children;
                nodes.Add(node);
            }
        int firstMeshNode = nodes.Count;
        foreach (var (info, mesh) in meshes)
        {
            var attributes = new Dictionary<string, int>
            {
                ["POSITION"] = Accessor(mesh.Positions.SelectMany(x => x).ToArray(), "VEC3", 3, true),
                ["NORMAL"] = Accessor(mesh.Normals.SelectMany(x => x).ToArray(), "VEC3", 3),
                ["TEXCOORD_0"] = Accessor(mesh.TexCoords.SelectMany(x => new[] { x[0], 1 - x[1] }).ToArray(), "VEC2", 2)
            };
            if (mesh.Tangents is not null)
                attributes["TANGENT"] = Accessor(mesh.Tangents.SelectMany(value =>
                {
                    var tangent = Vector3.Normalize(new Vector3(value[0], value[1], value[2]));
                    return new[] { tangent.X, tangent.Y, tangent.Z, -MathF.Sign(value[3]) };
                }).ToArray(), "VEC4", 4);
            if (info.Skinned && rig is not null && mesh.Joints is not null && mesh.Weights is not null)
            {
                var (joints, weights) = GltfInfluences(mesh.Joints, mesh.Weights, rig.Bones.Count);
                attributes["JOINTS_0"] = Indices(joints, 5123, "VEC4", 4);
                attributes["WEIGHTS_0"] = Accessor(weights, "VEC4", 4);
            }
            int meshIndex = gltfMeshes.Count;
            string name = names?.GetValueOrDefault(info.Id) ?? info.Id;
            gltfMeshes.Add(new { name, primitives = new[] { new { attributes, indices = Indices(mesh.Indices),
                mode = 4, material = Array.IndexOf(materials, info.MaterialTag) } } });
            var node = new Dictionary<string, object> { ["name"] = name, ["mesh"] = meshIndex,
                ["extras"] = new { sunriseMeshId = info.Id, sunriseModelTag = info.ModelTag.ToString("X8"),
                    sunrisePartIndex = info.PartIndex, sunriseMaterialTag = info.MaterialTag.ToString("X8"), sunriseLod = info.Lod,
                    sunriseInitiallyVisible = info.InitiallyVisible, sunriseInitialVisibilityReason = info.InitialVisibilityReason } };
            if (info.Skinned && rig is not null) node["skin"] = 0;
            else node["matrix"] = info.Transform;
            nodes.Add(node);
        }
        var animations = new List<object>();
        if (rig is not null)
            foreach (var (tag, name, clip) in clips)
            {
                var samplers = new List<object>(); var channels = new List<object>();
                int time = Accessor(clip.Times, "SCALAR", 1, true);
                int? ends = null;
                for (int bone = 0; bone < rig.Bones.Count; bone++)
                    foreach (string property in new[] { "translation", "rotation", "scale" })
                    {
                        float[] values = clip.Frames.SelectMany(frame => Values(frame[bone], property)).ToArray();
                        int width = property == "rotation" ? 4 : 3, input = time;
                        // A channel that stays within 1e-5 keeps only its first and last key; the clip range stays.
                        if (clip.Times.Length > 2 && values.Skip(width).Select((value, index) => MathF.Abs(value - values[index % width]) <= 1e-5f)
                            .All(same => same))
                        {
                            input = ends ??= Accessor([clip.Times[0], clip.Times[^1]], "SCALAR", 1, true);
                            values = [.. values.Take(width), .. values.Take(width)];
                        }
                        int output = Accessor(values, width == 4 ? "VEC4" : "VEC3", width);
                        int sampler = samplers.Count; samplers.Add(new { input, output, interpolation = "LINEAR" });
                        channels.Add(new { sampler, target = new { node = bone, path = property } });
                    }
                // Blender pose lookup matches "Pose_<tag>" inside the action name.
                animations.Add(new { name = $"{name} [Pose_{tag:X8}]", samplers, channels,
                    extras = new { sunriseClipTag = tag.ToString("X8"), sunrisePosePreview = true } });
            }
        var document = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "Sunrise Studio asset bundle" },
            ["scene"] = 0, ["scenes"] = new[] { new { nodes = new[] { nodes.Count } } },
            ["nodes"] = nodes, ["meshes"] = gltfMeshes, ["accessors"] = accessors, ["bufferViews"] = views,
            ["materials"] = materials.Select(tag => new { name = $"Material_{tag:X8}",
                extras = new { sunriseMaterialTag = tag.ToString("X8") },
                pbrMetallicRoughness = new { baseColorFactor = new[] { .7, .7, .7, 1 }, metallicFactor = 0, roughnessFactor = .7 } }).ToArray()
        };
        if (rig is not null)
        {
            int inverse = Accessor(rig.Bones.SelectMany(bone => MatrixValues(bone.InverseBindObject.Matrix)).ToArray(), "MAT4", 16);
            document["skins"] = new[] { new { inverseBindMatrices = inverse, joints = Enumerable.Range(0, rig.Bones.Count).ToArray() } };
            if (animations.Count != 0) document["animations"] = animations;
        }
        var roots = Enumerable.Range(firstMeshNode, meshes.Count).Concat(rig?.Bones.Where(x => x.ParentIndex < 0).Select(x => x.Index) ?? []);
        nodes.Add(new() { ["name"] = "Sunrise coordinates", ["matrix"] = new[] { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 }, ["children"] = roots.ToArray() });
        document["buffers"] = new[] { new { byteLength = data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()) } };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document));
    }

    /** Sampled times and per-frame bone transforms of a clip. */
    internal sealed record SampledClip(float[] Times, BoneTransform[][] Frames);
    /** glTF permits one nonzero weight per joint; native lane order remains in the bundle geometry. */
    internal static (uint[] Joints, float[] Weights) GltfInfluences(int[][] sourceJoints, float[][] sourceWeights, int bones)
    {
        if (sourceJoints.Length != sourceWeights.Length) throw new InvalidDataException("Skin influence counts disagree.");
        var joints = new uint[checked(sourceJoints.Length * 4)];
        var weights = new float[joints.Length];
        for (int vertex = 0; vertex < sourceJoints.Length; vertex++)
        {
            if (sourceJoints[vertex].Length != 4 || sourceWeights[vertex].Length != 4)
                throw new InvalidDataException("Skin vertices need four source lanes.");
            int used = 0, start = vertex * 4;
            for (int lane = 0; lane < 4; lane++)
            {
                float weight = sourceWeights[vertex][lane];
                if (!float.IsFinite(weight) || weight < 0) throw new InvalidDataException("Skin weight is invalid.");
                if (weight == 0) continue;
                int bone = sourceJoints[vertex][lane];
                if ((uint)bone >= bones) throw new InvalidDataException("Skin joint exceeds the exported rig.");
                int target = 0;
                while (target < used && joints[start + target] != bone) target++;
                if (target == used) joints[start + used++] = (uint)bone;
                weights[start + target] += weight;
            }
        }
        return (joints, weights);
    }
    /** Flattens a matrix to 16 floats in row order. */
    internal static float[] MatrixValues(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
    private static float[] Values(BoneTransform value, string property) => property switch
    {
        "translation" => [value.Translation.X, value.Translation.Y, value.Translation.Z],
        "rotation" => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W],
        _ => [value.Scale, value.Scale, value.Scale]
    };
}
