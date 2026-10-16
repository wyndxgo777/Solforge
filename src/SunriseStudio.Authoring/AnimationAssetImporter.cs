using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Outcome of a control import: the new clip bytes, channels changed, frame count and the largest sampling error. */
public sealed record AnimationImportResult(uint ClipTag, byte[] Data, int ChangedChannels,
    int FrameCount, float MaximumError);

/** Control interchange edits decoded native channels without guessing a skeleton-to-rig inverse. */
public static class AnimationAssetImporter
{
    /**
     * glTF controls retain explicit native indices under a fixed coordinate parent.
     * @param clipName Display name; the animation name keeps the tag after it.
     */
    public static void ExportControlsGltf(PackageCatalog catalog, uint clipTag, string path,
        string? clipName = null)
    {
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        var layout = new NativeControlLayout(catalog.Read(clipTag), clip);
        var channels = layout.Channels.Where(x => x.Writable).ToArray();
        if (channels.Length == 0) throw new NotSupportedException("This clip has no editable codec-2 or codec-3 controls.");
        if (channels.Sum(x => (long)x.Frames * x.Width) > 8_000_000)
            throw new InvalidDataException("Animation export exceeds its sample limit.");
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, true);
        var views = new List<object>();
        var accessors = new List<object>();
        var samplers = new List<object>();
        var gltfChannels = new List<object>();
        int Add(float[] values, int width, bool timing = false)
        {
            int offset = checked((int)data.Position), view = views.Count, accessor = accessors.Count;
            foreach (float value in values) writer.Write(value);
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = values.Length * 4 });
            var fields = new Dictionary<string, object>
            {
                ["bufferView"] = view, ["componentType"] = 5126, ["count"] = values.Length / width,
                ["type"] = width == 1 ? "SCALAR" : width == 3 ? "VEC3" : "VEC4"
            };
            if (timing) { fields["min"] = new[] { values[0] }; fields["max"] = new[] { values[^1] }; }
            accessors.Add(fields);
            return accessor;
        }
        int[] controls = channels.Select(x => x.Index).Distinct().Order().ToArray();
        var nodes = new List<object>();
        var sampled = new ControlSamples(clip);
        foreach (int index in controls)
        {
            var initial = sampled.At(0, index);
            nodes.Add(new
            {
                name = $"Control {index:D4}", extras = new { sunriseControlIndex = index },
                translation = new[] { initial.Translation.X, initial.Translation.Y, initial.Translation.Z },
                rotation = new[] { initial.Rotation.X, initial.Rotation.Y, initial.Rotation.Z, initial.Rotation.W },
                scale = new[] { initial.Scale, initial.Scale, initial.Scale }
            });
            foreach (var channel in channels.Where(x => x.Index == index))
            {
                float[] times = Enumerable.Range(0, channel.Frames).Select(x => x / 30f).ToArray();
                var values = Enumerable.Range(0, channel.Frames).SelectMany(frame =>
                {
                    float[] value = NativeControlLayout.Sample(sampled.At(frame, index), channel.Path);
                    return channel.Path == "scale" ? new[] { value[0], value[0], value[0] } : value;
                }).ToArray();
                int sampler = samplers.Count;
                samplers.Add(new { input = Add(times, 1, true), output = Add(values, channel.Path == "rotation" ? 4 : 3), interpolation = "LINEAR" });
                gltfChannels.Add(new { sampler, target = new { node = nodes.Count - 1, path = channel.Path } });
            }
        }
        var rotation = ControlGltfDocument.CoordinateRotation;
        nodes.Add(new
        {
            name = "Sunrise control coordinates", extras = new
            {
                sunriseCoordinates = true, sunriseClipTag = $"{clipTag:X8}",
                sunriseFrameCount = clip.FrameCount, sunriseControlCount = clip.ControlCount
            },
            rotation = new[] { rotation.X, rotation.Y, rotation.Z, rotation.W },
            children = Enumerable.Range(0, controls.Length).ToArray()
        });
        var document = new
        {
            asset = new { version = "2.0", generator = "Sunrise Studio native controls" },
            extras = new { sunriseAnimationControls = new { clipTag = $"{clipTag:X8}", frameCount = clip.FrameCount, controlCount = clip.ControlCount, sampleRate = 30 } },
            scene = 0, scenes = new[] { new { nodes = new[] { controls.Length } } }, nodes, bufferViews = views, accessors,
            buffers = new[] { new { byteLength = data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()) } },
            animations = new[] { new { name = clipName is null ? $"Native controls {clipTag:X8}"
                : $"{clipName} [{clipTag:X8}]", samplers, channels = gltfChannels } }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
    }

    /** Verified bytes can join the editor's undo transaction. */
    public static AnimationImportResult PrepareGltf(PackageCatalog catalog, uint clipTag, string path)
    {
        if (!catalog.TryGetEntry(clipTag, out var entry)) throw new KeyNotFoundException("Animation clip is missing.");
        var clip = new AnimationControlReader(catalog).Read(clipTag);
        byte[] original = catalog.Read(clipTag);
        var layout = new NativeControlLayout(original, clip);
        var imported = ControlGltfDocument.Read(path, clipTag, clip.FrameCount, clip.ControlCount);
        byte[] output = original.ToArray();
        var expected = new Dictionary<(int Index, string Path), (float[][] Samples, float Tolerance)>();
        var sampled = new ControlSamples(clip);
        int changed = 0;
        foreach (var channel in imported)
        {
            var native = layout.Channels.SingleOrDefault(x => x.Index == channel.Index && x.Path == channel.Path)
                ?? throw new NotSupportedException("The clip has no matching native control component.");
            if (!native.Writable) throw new NotSupportedException("Sparse animation controls cannot be imported yet.");
            if (native.Frames == 1 && channel.Values.Any(x => Error(x, channel.Values[0], channel.Path) > 0.000001f))
                throw new NotSupportedException("A static native component cannot receive a varying animation.");
            float[][] samples = Enumerable.Range(0, native.Frames).Select(frame => channel.Sample(frame / 30f)).ToArray();
            bool differs = false;
            for (int frame = 0; frame < samples.Length; frame++)
            {
                float[] prior = NativeControlLayout.Sample(sampled.At(frame, channel.Index), channel.Path);
                if (channel.Path == "rotation" && Dot(prior, samples[frame]) < 0)
                    samples[frame] = samples[frame].Select(x => -x).ToArray();
                differs |= Error(prior, samples[frame], channel.Path) > 0.000001f;
            }
            float tolerance = differs ? layout.Write(output, native, samples) : 0.000001f;
            if (differs) changed++;
            expected.Add((channel.Index, channel.Path), (samples, tolerance));
        }
        float maximumError = Verify(catalog, entry, clip, output, layout, expected);
        return new(clipTag, output, changed, clip.FrameCount, maximumError);
    }

    /** Validated native samples become one project delta. */
    public static AnimationImportResult ImportGltf(StudioProject project, PackageCatalog catalog,
        uint clipTag, string path)
    {
        var result = PrepareGltf(catalog, clipTag, path);
        project.ApplyEdit(clipTag, result.Data, catalog);
        return result;
    }

    /** A separate catalog verifies native decoding without replacing the live editor overlay. */
    private static float Verify(PackageCatalog catalog, PackageEntry entry, AnimationControlClip original,
        byte[] output, NativeControlLayout layout,
        IReadOnlyDictionary<(int Index, string Path), (float[][] Samples, float Tolerance)> expected)
    {
        using var verification = PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);
        verification.SetOverlayEntries([new(entry with { Size = output.Length })]);
        verification.ReadOverride = tag => tag == entry.TagHash ? output : null;
        var decoded = new AnimationControlReader(verification).Read(entry.TagHash);
        if (decoded.FrameCount != original.FrameCount || decoded.ControlCount != original.ControlCount
            || decoded.NodeCount != original.NodeCount || decoded.PoseMode != original.PoseMode
            || !decoded.ControlGroups.SequenceEqual(original.ControlGroups))
            throw new InvalidDataException("Animation import changed native clip metadata.");
        float maximum = 0;
        for (int frame = 0; frame < original.FrameCount; frame++)
        {
            var prior = original.Sample(frame / 30f).ToDictionary(x => x.ControlIndex);
            var current = decoded.Sample(frame / 30f).ToDictionary(x => x.ControlIndex);
            foreach (var channel in layout.Channels)
            {
                float[] target = NativeControlLayout.Sample(prior[channel.Index], channel.Path);
                float tolerance = 0.000001f;
                if (expected.TryGetValue((channel.Index, channel.Path), out var imported))
                {
                    target = imported.Samples[channel.Frames == 1 ? 0 : frame];
                    tolerance = imported.Tolerance;
                }
                float error = Error(target, NativeControlLayout.Sample(current[channel.Index], channel.Path), channel.Path);
                if (error > tolerance) throw new InvalidDataException("Native animation decode did not preserve the imported samples.");
                maximum = Math.Max(maximum, error);
            }
        }
        return maximum;
    }

    private static float Dot(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();
    private static float Error(float[] a, float[] b, string path)
    {
        float sign = path == "rotation" && Dot(a, b) < 0 ? -1 : 1;
        return a.Zip(b, (x, y) => Math.Abs(x - sign * y)).Max();
    }

    /** Samples each frame once; a control keeps its track position in every frame. */
    private sealed class ControlSamples(AnimationControlClip clip)
    {
        private readonly AnimationControlClip clip = clip;
        private readonly Dictionary<int, AnimationControlSample[]> frames = [];
        private readonly Dictionary<int, int> positions = Positions(clip.Sample(0));

        /** Control sample for a native control index at a 30 fps frame; samples are cached per frame. */
        public AnimationControlSample At(int frame, int index)
        {
            if (!positions.TryGetValue(index, out int position))
                throw new InvalidDataException($"The clip has no control {index}.");
            if (!frames.TryGetValue(frame, out var sample)) frames[frame] = sample = clip.Sample(frame / 30f);
            return sample[position];
        }

        private static Dictionary<int, int> Positions(AnimationControlSample[] sample)
        {
            var positions = new Dictionary<int, int>();
            for (int i = 0; i < sample.Length; i++)
                if (!positions.TryAdd(sample[i].ControlIndex, i)) throw new InvalidDataException("A control index repeats.");
            return positions;
        }
    }
}
