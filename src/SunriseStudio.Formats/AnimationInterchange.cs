using System.Numerics;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Exports package skeleton animation to glTF; sampled joint poses are written as a skinned-rig animation. One joint of the sampled rig: name, parent index and bind-pose local transform. */
public sealed record AnimationJoint(string Name, int ParentIndex, BoneTransform BindLocal);
/** One sampled time step: local transforms for every joint, in joint order. */
public sealed record AnimationFrame(float Time, IReadOnlyList<BoneTransform> LocalTransforms);
/** A clip resampled at a fixed frame rate over a joint hierarchy, ready for a writer. */
public sealed record SampledAnimation(string Name, IReadOnlyList<AnimationJoint> Joints,
    IReadOnlyList<AnimationFrame> Frames);

/** Exported joint poses retain their native control graph's evaluation. */
public static class AnimationInterchange
{
    /** Reads the entity's rig and the clip from the packages, samples it and writes a glTF file. */
    public static void ExportNativeAnimation(PackageCatalog catalog, uint entityTag, uint clipTag,
        string path, int framesPerSecond = 30)
    {
        var rig = new SkeletonReader(catalog).Read(entityTag);
        var binding = new AnimationRigReader(catalog).Read(rig);
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        WriteGltf(path, rig, binding, clip, framesPerSecond);
    }

    /** Native control indices reach bones only through the linked rig binding. */
    public static SampledAnimation Sample(SkeletonRig rig, AnimationRigBinding binding,
        AnimationControlClip clip, int framesPerSecond = 30, bool previewLayers = false)
    {
        if (framesPerSecond is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        var pose = rig.CreateBindPose();
        List<AnimationFrame> frames = [];
        int count = checked((int)Math.Ceiling((clip.FrameCount - 1) * (double)framesPerSecond / 30) + 1);
        if ((long)count * rig.Bones.Count > 8_000_000) throw new InvalidDataException("Animation exceeds sample limits.");
        for (int i = 0; i < count; i++)
        {
            float time = Math.Min(i / (float)framesPerSecond, clip.DurationSeconds);
            if (previewLayers) binding.ApplyPreview(clip, time, pose);
            else binding.Apply(clip, time, pose);
            frames.Add(new(time, pose.LocalTransforms.ToArray()));
        }
        return new($"Animation {clip.TagHash:X8}", rig.Bones.Select(x =>
            new AnimationJoint(x.Name, x.ParentIndex, x.BindLocal)).ToArray(), frames);
    }

    /** Samples an already-read rig, binding and clip, then writes the result as glTF. */
    public static void WriteGltf(string path, SkeletonRig rig, AnimationRigBinding binding,
        AnimationControlClip clip, int framesPerSecond = 30) =>
        WriteGltf(path, Sample(rig, binding, clip, framesPerSecond));

    /** A root rotation converts package Z-up coordinates into glTF Y-up coordinates. */
    public static void WriteGltf(string path, SampledAnimation animation)
    {
        Validate(animation);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
        List<object> accessors = [], views = [], samplers = [], channels = [];
        int AddFloats(float[] values, int components, string type, bool timing = false)
        {
            int start = checked((int)data.Position);
            foreach (float value in values) writer.Write(value);
            int view = views.Count;
            views.Add(new { buffer = 0, byteOffset = start, byteLength = values.Length * 4 });
            var accessor = new Dictionary<string, object>
            {
                ["bufferView"] = view,
                ["componentType"] = 5126,
                ["count"] = values.Length / components,
                ["type"] = type
            };
            if (timing) { accessor["min"] = new[] { values[0] }; accessor["max"] = new[] { values[^1] }; }
            int index = accessors.Count;
            accessors.Add(accessor);
            return index;
        }
        int times = AddFloats(animation.Frames.Select(x => x.Time).ToArray(), 1, "SCALAR", true);
        List<object> nodes = [];
        for (int i = 0; i < animation.Joints.Count; i++)
        {
            var joint = animation.Joints[i];
            var bind = joint.BindLocal with { Rotation = Quaternion.Normalize(joint.BindLocal.Rotation) };
            var node = new Dictionary<string, object>
            {
                ["name"] = joint.Name,
                ["translation"] = new[] { bind.Translation.X, bind.Translation.Y, bind.Translation.Z },
                ["rotation"] = new[] { bind.Rotation.X, bind.Rotation.Y, bind.Rotation.Z, bind.Rotation.W },
                ["scale"] = new[] { bind.Scale, bind.Scale, bind.Scale }
            };
            var children = Enumerable.Range(0, animation.Joints.Count).Where(j => animation.Joints[j].ParentIndex == i).ToArray();
            if (children.Length != 0) node["children"] = children;
            nodes.Add(node);
            List<float> translations = [], rotations = [], scales = [];
            Quaternion previous = Quaternion.Identity;
            foreach (var frame in animation.Frames)
            {
                var value = frame.LocalTransforms[i];
                Quaternion rotation = Quaternion.Normalize(value.Rotation);
                if (Quaternion.Dot(previous, rotation) < 0)
                    rotation = new(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
                previous = rotation;
                translations.AddRange([value.Translation.X, value.Translation.Y, value.Translation.Z]);
                rotations.AddRange([rotation.X, rotation.Y, rotation.Z, rotation.W]);
                scales.AddRange([value.Scale, value.Scale, value.Scale]);
            }
            foreach (var (target, output) in new[]
            {
                ("translation", AddFloats(translations.ToArray(), 3, "VEC3")),
                ("rotation", AddFloats(rotations.ToArray(), 4, "VEC4")),
                ("scale", AddFloats(scales.ToArray(), 3, "VEC3"))
            })
            {
                int sampler = samplers.Count;
                samplers.Add(new { input = times, output, interpolation = "LINEAR" });
                channels.Add(new { sampler, target = new { node = i, path = target } });
            }
        }
        var rootRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
        nodes.Add(new
        {
            name = "Sunrise coordinates",
            rotation = new[] { rootRotation.X, rootRotation.Y, rootRotation.Z, rootRotation.W },
            children = Enumerable.Range(0, animation.Joints.Count).Where(i => animation.Joints[i].ParentIndex < 0).ToArray()
        });
        var document = new
        {
            asset = new { version = "2.0", generator = "Sunrise Studio" },
            scene = 0,
            scenes = new[] { new { nodes = new[] { animation.Joints.Count } } },
            nodes,
            accessors,
            bufferViews = views,
            buffers = new[] { new { byteLength = data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()) } },
            animations = new[] { new { name = animation.Name, samplers, channels } }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    /** Every frame covers the same acyclic joint tree at strictly increasing times. */
    private static void Validate(SampledAnimation animation)
    {
        if (animation.Joints.Count is < 1 or > 4096 || animation.Frames.Count == 0
            || (long)animation.Joints.Count * animation.Frames.Count > 8_000_000)
            throw new InvalidDataException("Animation joint or frame count is invalid.");
        for (int i = 0; i < animation.Joints.Count; i++)
        {
            if (animation.Joints[i].ParentIndex < -1)
                throw new InvalidDataException("Animation parent index is invalid.");
            ValidateTransform(animation.Joints[i].BindLocal);
            HashSet<int> visited = [];
            for (int j = i; j >= 0; j = animation.Joints[j].ParentIndex)
            {
                if (j >= animation.Joints.Count || !visited.Add(j))
                    throw new InvalidDataException("Animation hierarchy is invalid.");
            }
        }
        float previous = -1;
        foreach (var frame in animation.Frames)
        {
            if (!float.IsFinite(frame.Time) || frame.Time < 0 || frame.Time <= previous
                || frame.LocalTransforms.Count != animation.Joints.Count)
                throw new InvalidDataException("Animation frame timing or joint count is invalid.");
            foreach (var transform in frame.LocalTransforms) ValidateTransform(transform);
            previous = frame.Time;
        }
    }

    private static void ValidateTransform(BoneTransform value)
    {
        if (!float.IsFinite(value.Scale) || value.Scale == 0 || !float.IsFinite(value.Translation.X)
            || !float.IsFinite(value.Translation.Y) || !float.IsFinite(value.Translation.Z)
            || !float.IsFinite(value.Rotation.LengthSquared()) || value.Rotation.LengthSquared() < 1e-12f)
            throw new InvalidDataException("Animation contains an invalid transform.");
    }
}
