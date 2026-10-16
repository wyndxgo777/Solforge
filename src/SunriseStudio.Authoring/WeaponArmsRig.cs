using System.Numerics;
using System.Text;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Arms action and the weapon event it plays for. */
public sealed record WeaponArmsClip(string Action, string Event);
/** Arms rig glTF path with its clips. */
public sealed record WeaponArmsSource(string Path, IReadOnlyList<WeaponArmsClip> Clips);

/** First-person arm motion is authored in the weapon frame and solved back into rig controls. */
public static class WeaponArmsRig
{
    public const string SourceSuffix = ".sunrise-arms.glb";
    public const string ActionPrefix = "Arms ";
    // The presentation skeleton holds the body and arms first; bone 72 is the weapon root.
    private const int WeaponBone = 72, ArmBones = 72, CameraBone = 2, SampleRate = 30;
    // A solved hand or weapon bone further than this from its target is out of reach.
    private const float ReachTolerance = 0.002f;
    // The preview camera sits on the aim line, below player bone 2.
    private const float AimLineDrop = 0.0285f, PreviewFieldOfView = 1.2f;

    /** Joint names carry the native bone index; Blender keeps them as bone names. */
    public static string JointName(SkeletonRig skeleton, int bone) => $"fp_{bone:D2}_" + System.Text.RegularExpressions.Regex.Replace(
        skeleton.Bones[bone].Name.Replace(" (candidate)", "", StringComparison.Ordinal), "[^A-Za-z0-9_]", "_");

    /** The checked rig: 72 body and arm bones, then the five weapon bones under the right grip. */
    private static (SkeletonRig Skeleton, AnimationRigBinding Binding) Rig(PackageCatalog catalog)
    {
        var skeleton = new SkeletonReader(catalog).Read(WeaponPresentationAuthoring.PresentationTemplate);
        var binding = new AnimationRigReader(catalog).Read(skeleton);
        if (skeleton.Bones.Count != ArmBones + 5 || binding.Controls.Count != 75
            || skeleton.Bones[WeaponBone].ParentIndex >= ArmBones
            || skeleton.Bones.Take(ArmBones).Any(bone => bone.ParentIndex >= ArmBones))
            throw new NotSupportedException("The checked first-person rig changed.");
        return (skeleton, binding);
    }

    /** Every exported arms action must be listed, and each must animate the full arm set. */
    internal static void Check(PackageCatalog catalog, WeaponArmsSource arms, float metresPerUnit)
    {
        var (skeleton, _) = Rig(catalog);
        string[] joints = Enumerable.Range(0, ArmBones).Select(bone => JointName(skeleton, bone)).ToArray();
        using (var scene = GltfInterchange.ReadJson(arms.Path))
        {
            string[] exported = scene.RootElement.TryGetProperty("animations", out var animations)
                ? animations.EnumerateArray().Select(animation => animation.GetProperty("name").GetString()!).ToArray() : [];
            if (!exported.Order().SequenceEqual(arms.Clips.Select(clip => clip.Action).Order()))
                throw new InvalidDataException("Every exported arms action needs one weapon event.");
        }
        foreach (var clip in arms.Clips)
            _ = WeaponGltfAnimationReader.ReadSkins(arms.Path, clip.Action, joints, 2, metresPerUnit);
    }

    /** Object transforms of every bone for one clip time, after limb solving. */
    private static Matrix4x4[] Evaluate(SkeletonRig skeleton, AnimationRigBinding binding,
        AnimationControlClip clip, float seconds)
    {
        var pose = skeleton.CreateBindPose();
        binding.ApplyValues(WeaponAssetClipCompiler.ActualControls(clip, binding, seconds), pose);
        return pose.ObjectTransforms.ToArray();
    }

    /** Re-expresses object transforms relative to the weapon root. */
    private static Matrix4x4[] WeaponFrame(IReadOnlyList<Matrix4x4> objects)
    {
        if (!Matrix4x4.Invert(objects[WeaponBone], out var inverse))
            throw new InvalidDataException("The weapon root transform is singular.");
        return objects.Select(value => value * inverse).ToArray();
    }

    private static Matrix4x4 Invert(Matrix4x4 value) =>
        Matrix4x4.Invert(value, out var inverse) ? inverse : throw new InvalidDataException("A bone transform is singular.");

    /**
     * Writes a GLB with the arm bones in the weapon frame, a guide mesh, a preview camera, and one
     * action per clip named <c>Arms event</c>. Blender imports it beside the weapon rig.
     * @param clips Weapon event names and the clips whose arm motion each action starts from.
     */
    public static void Export(PackageCatalog catalog, IReadOnlyList<(string Event, uint ClipTag)> clips, string path)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (clips.Count == 0 || clips.Select(clip => clip.Event).Distinct().Count() != clips.Count)
            throw new InvalidDataException("Export needs clips with different events.");
        var (skeleton, binding) = Rig(catalog);
        var rest = WeaponFrame(skeleton.Bones.Select(bone => bone.BindObject.Matrix).ToArray());
        var toScene = Invert(WeaponGltfAnimationReader.Conversion(1));
        var glb = new Glb();
        var nodes = new List<object>();
        for (int bone = 0; bone < ArmBones; bone++)
        {
            var local = Local(rest, skeleton, bone);
            var children = Enumerable.Range(0, ArmBones).Where(child => skeleton.Bones[child].ParentIndex == bone).ToList();
            if (bone == CameraBone) children.Add(ArmBones + 1);
            nodes.Add(Node(JointName(skeleton, bone), local, children));
        }
        var (positions, joints, weights, indices) = Guide(rest, skeleton);
        nodes.Add(new { name = "Arms guide", mesh = 0, skin = 0 });
        var camera = Matrix4x4.Identity with
        {
            M11 = 0, M12 = -1, M13 = 0, M21 = 0, M22 = 0, M23 = 1, M31 = -1, M32 = 0, M33 = 0,
            M41 = 0, M42 = 0, M43 = -AimLineDrop
        };
        nodes.Add(Node("Arms preview camera", BoneTransform.FromMatrix(camera), [], cameraIndex: 0));
        var roots = Enumerable.Range(0, ArmBones).Where(bone => skeleton.Bones[bone].ParentIndex < 0).Append(ArmBones).ToArray();
        var sceneRotation = Quaternion.CreateFromRotationMatrix(toScene);
        nodes.Add(new
        {
            name = "Sunrise first-person arms",
            rotation = new[] { sceneRotation.X, sceneRotation.Y, sceneRotation.Z, sceneRotation.W },
            children = roots
        });
        var scenePositions = positions.Select(point => Vector3.Transform(point, toScene)).ToArray();
        int position = glb.Add(scenePositions.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToArray(), "VEC3",
            minimum: [scenePositions.Min(p => p.X), scenePositions.Min(p => p.Y), scenePositions.Min(p => p.Z)],
            maximum: [scenePositions.Max(p => p.X), scenePositions.Max(p => p.Y), scenePositions.Max(p => p.Z)]);
        int jointAccessor = glb.AddBytes(joints, "VEC4", 5121);
        int weightAccessor = glb.Add(weights, "VEC4");
        int indexAccessor = glb.AddIndices(indices);
        int inverseBinds = glb.Add(Enumerable.Range(0, ArmBones).SelectMany(bone =>
            Floats(Invert(rest[bone] * toScene))).ToArray(), "MAT4");
        var animations = new List<object>();
        foreach (var (name, clipTag) in clips)
        {
            var clip = new AnimationControlReader(catalog).Read(clipTag);
            if (binding.CompatibleControlCount(clip) != binding.Controls.Count || clip.PoseMode != 0)
                throw new NotSupportedException("A weapon clip no longer matches its first-person rig.");
            var translations = Enumerable.Range(0, ArmBones).Select(_ => new List<float>()).ToArray();
            var rotations = Enumerable.Range(0, ArmBones).Select(_ => new List<float>()).ToArray();
            for (int frame = 0; frame < clip.FrameCount; frame++)
            {
                var pose = WeaponFrame(Evaluate(skeleton, binding, clip, frame / (float)SampleRate));
                for (int bone = 0; bone < ArmBones; bone++)
                {
                    var local = Local(pose, skeleton, bone);
                    var rotation = local.Rotation;
                    var prior = rotations[bone];
                    if (prior.Count != 0 && Quaternion.Dot(rotation, new(prior[^4], prior[^3], prior[^2], prior[^1])) < 0)
                        rotation = Quaternion.Negate(rotation);
                    translations[bone].AddRange([local.Translation.X, local.Translation.Y, local.Translation.Z]);
                    rotations[bone].AddRange([rotation.X, rotation.Y, rotation.Z, rotation.W]);
                }
            }
            float[] times = Enumerable.Range(0, clip.FrameCount).Select(frame => frame / (float)SampleRate).ToArray();
            int input = glb.Add(times, "SCALAR", minimum: [times[0]], maximum: [times[^1]]);
            var samplers = new List<object>();
            var channels = new List<object>();
            for (int bone = 0; bone < ArmBones; bone++)
            {
                channels.Add(new { sampler = samplers.Count, target = new { node = bone, path = "translation" } });
                samplers.Add(new { input, output = glb.Add(translations[bone].ToArray(), "VEC3"), interpolation = "LINEAR" });
                channels.Add(new { sampler = samplers.Count, target = new { node = bone, path = "rotation" } });
                samplers.Add(new { input, output = glb.Add(rotations[bone].ToArray(), "VEC4"), interpolation = "LINEAR" });
            }
            animations.Add(new { name = ActionPrefix + name, samplers, channels });
        }
        var document = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "Sunrise Studio first-person arms" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { nodes.Count - 1 } } },
            ["nodes"] = nodes,
            ["meshes"] = new[] { new { name = "Arms guide", primitives = new[] { new
            {
                attributes = new Dictionary<string, int> { ["POSITION"] = position, ["JOINTS_0"] = jointAccessor, ["WEIGHTS_0"] = weightAccessor },
                indices = indexAccessor
            } } } },
            ["skins"] = new[] { new { inverseBindMatrices = inverseBinds, joints = Enumerable.Range(0, ArmBones).ToArray() } },
            ["cameras"] = new[] { new { type = "perspective", perspective = new { yfov = PreviewFieldOfView, znear = 0.01f, zfar = 100f } } },
            ["animations"] = animations,
        };
        glb.Write(path, document);
    }

    /** A bone's transform relative to its parent within the arm set. */
    private static BoneTransform Local(Matrix4x4[] objects, SkeletonRig skeleton, int bone)
    {
        int parent = skeleton.Bones[bone].ParentIndex;
        return BoneTransform.FromMatrix(parent < 0 ? objects[bone] : objects[bone] * Invert(objects[parent]));
    }

    private static object Node(string name, BoneTransform local, IReadOnlyList<int> children, int? cameraIndex = null)
    {
        var value = new Dictionary<string, object>
        {
            ["name"] = name,
            ["translation"] = new[] { local.Translation.X, local.Translation.Y, local.Translation.Z },
            ["rotation"] = new[] { local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W },
        };
        if (children.Count != 0) value["children"] = children;
        if (cameraIndex is { } camera) value["camera"] = camera;
        return value;
    }

    /** Thin boxes from each bone to its children, rigidly bound to the parent, show the arms in Blender. */
    private static (Vector3[] Positions, byte[] Joints, float[] Weights, ushort[] Indices) Guide(
        Matrix4x4[] rest, SkeletonRig skeleton)
    {
        var positions = new List<Vector3>();
        var joints = new List<byte>();
        var weights = new List<float>();
        var indices = new List<ushort>();
        void Box(Vector3 from, Vector3 to, float width, int bone)
        {
            var axis = to - from;
            if (axis.Length() < 0.004f) { from -= new Vector3(width); to += new Vector3(width); axis = to - from; }
            var direction = Vector3.Normalize(axis);
            var side = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY)) * width;
            var up = Vector3.Normalize(Vector3.Cross(side, direction)) * width;
            int first = positions.Count;
            foreach (var end in new[] { from, to })
                foreach (var corner in new[] { -side - up, side - up, side + up, -side + up })
                {
                    positions.Add(end + corner);
                    joints.AddRange([checked((byte)bone), 0, 0, 0]);
                    weights.AddRange([1, 0, 0, 0]);
                }
            int[] faces = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
            indices.AddRange(faces.Select(index => checked((ushort)(first + index))));
        }
        for (int bone = 0; bone < ArmBones; bone++)
        {
            int parent = skeleton.Bones[bone].ParentIndex;
            var at = rest[bone].Translation;
            if (parent >= 0)
            {
                var from = rest[parent].Translation;
                Box(from, at, Vector3.Distance(from, at) > 0.08f ? 0.012f : 0.004f, parent);
            }
            if (!Enumerable.Range(0, ArmBones).Any(child => skeleton.Bones[child].ParentIndex == bone))
                Box(at, at, 0.006f, bone);
        }
        return (positions.ToArray(), joints.ToArray(), weights.ToArray(), indices.ToArray());
    }

    private static float[] Floats(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    /**
     * Solves every rig control per frame; the camera bone keeps the donor's object transform.
     * @param followDonor True when the clip replaces the donor and keeps its timing.
     * @return Control values with the donor's retarget scale removed.
     */
    internal static Dictionary<int, IReadOnlyList<BoneTransform>> Controls(PackageCatalog catalog, string path,
        string action, float metresPerUnit, AnimationControlClip donor, bool followDonor,
        SkeletonRig mechanical, WeaponSourceAnimation weapon)
    {
        var (skeleton, binding) = Rig(catalog);
        if (mechanical.Bones.Count != 5) throw new NotSupportedException("The weapon frame needs five weapon bones.");
        string[] joints = Enumerable.Range(0, ArmBones).Select(bone => JointName(skeleton, bone)).ToArray();
        int frames = weapon.FrameCount;
        var sampled = WeaponGltfAnimationReader.ReadSkins(path, action, joints, frames, metresPerUnit);
        if (MathF.Abs(sampled.SourceDuration - weapon.SourceDuration) > 1.5f / SampleRate)
            throw new InvalidDataException("The arms action and the weapon action must last the same time.");
        var rest = WeaponFrame(skeleton.Bones.Select(bone => bone.BindObject.Matrix).ToArray());
        float retarget = binding.TranslationScale(donor);
        var hands = binding.IkControlIndices.Select(index => (int)binding.Controls[index].BoneIndex)
            .Concat(Enumerable.Range(WeaponBone, 5)).ToArray();
        var output = Enumerable.Range(0, binding.Controls.Count).ToDictionary(control => control, _ => new List<BoneTransform>());
        Matrix4x4? fixedCamera = followDonor ? null : Evaluate(skeleton, binding, donor, 0)[CameraBone];
        for (int frame = 0; frame < frames; frame++)
        {
            var arms = Enumerable.Range(0, ArmBones).Select(bone => rest[bone] * sampled.Skins[frame][bone]).ToArray();
            var camera = fixedCamera ?? Evaluate(skeleton, binding, donor, frame / (float)SampleRate)[CameraBone];
            var anchor = Invert(arms[CameraBone]) * camera;
            var targets = new Matrix4x4[skeleton.Bones.Count];
            for (int bone = 0; bone < ArmBones; bone++) targets[bone] = arms[bone] * anchor;
            var weaponObjects = new Matrix4x4[5];
            foreach (var bone in mechanical.Bones)
            {
                var local = weapon.Controls[bone.Index][frame].Matrix;
                weaponObjects[bone.Index] = bone.ParentIndex < 0 ? local : local * weaponObjects[bone.ParentIndex];
                var skin = Invert(bone.BindObject.Matrix) * weaponObjects[bone.Index];
                targets[WeaponBone + bone.Index] = rest[WeaponBone + bone.Index] * skin * anchor;
            }
            var values = binding.Solve(targets.Select(BoneTransform.FromMatrix).ToArray());
            var pose = skeleton.CreateBindPose();
            binding.ApplyValues(values, pose);
            foreach (int bone in hands)
                if (Vector3.Distance(pose.ObjectTransforms[bone].Translation, targets[bone].Translation) > ReachTolerance)
                    throw new InvalidDataException(
                        $"Frame {frame}: {skeleton.Bones[bone].Name} cannot reach its authored position. Keep the hands within arm's length.");
            foreach (var control in binding.Controls)
            {
                var value = values[control.Index];
                output[control.Index].Add(value with { Translation = value.Translation / retarget });
            }
        }
        return output.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<BoneTransform>)pair.Value.ToArray());
    }

    /** One embedded buffer; every accessor is tightly packed and four-byte aligned. */
    private sealed class Glb
    {
        private readonly MemoryStream data = new();
        private readonly List<object> views = [], accessors = [];

        /** Adds a float accessor of a glTF type and returns its index. */
        public int Add(float[] values, string type, float[]? minimum = null, float[]? maximum = null)
        {
            int width = type switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            var accessor = new Dictionary<string, object>
            {
                ["bufferView"] = View(bytes), ["componentType"] = 5126, ["count"] = values.Length / width, ["type"] = type
            };
            if (minimum is not null) accessor["min"] = minimum;
            if (maximum is not null) accessor["max"] = maximum;
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        /** Adds an accessor over raw four-byte elements and returns its index. */
        public int AddBytes(byte[] values, string type, int componentType)
        {
            accessors.Add(new { bufferView = View(values), componentType, count = values.Length / 4, type });
            return accessors.Count - 1;
        }

        /** Adds a 16-bit index accessor and returns its index. */
        public int AddIndices(ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            accessors.Add(new { bufferView = View(bytes), componentType = 5123, count = values.Length, type = "SCALAR" });
            return accessors.Count - 1;
        }

        private int View(byte[] bytes)
        {
            while (data.Length % 4 != 0) data.WriteByte(0);
            views.Add(new { buffer = 0, byteOffset = data.Length, byteLength = bytes.Length });
            data.Write(bytes);
            return views.Count - 1;
        }

        /** Writes the glb with the collected buffers, views and accessors. */
        public void Write(string path, Dictionary<string, object> document)
        {
            while (data.Length % 4 != 0) data.WriteByte(0);
            document["buffers"] = new[] { new { byteLength = data.Length } };
            document["bufferViews"] = views;
            document["accessors"] = accessors;
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document));
            GltfInterchange.WriteGlb(path, json, data.ToArray());
        }
    }
}
