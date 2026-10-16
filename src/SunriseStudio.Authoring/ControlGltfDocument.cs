using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace SunriseStudio.Authoring;

/** One animation channel read back from a controls glTF: key times, values and interpolation mode. */
internal sealed record ImportedControlChannel(int Index, string Path, float[] Times, float[][] Values,
    string Interpolation)
{
    /** glTF rotations use spherical interpolation before native frame sampling. */
    internal float[] Sample(float time)
    {
        if (time <= Times[0]) return Values[0].ToArray();
        if (time >= Times[^1]) return Values[^1].ToArray();
        int second = Array.BinarySearch(Times, time);
        if (second >= 0) return Values[second].ToArray();
        second = ~second;
        int first = second - 1;
        if (Interpolation == "STEP") return Values[first].ToArray();
        float amount = (time - Times[first]) / (Times[second] - Times[first]);
        if (Path == "rotation")
        {
            Quaternion ToRotation(float[] x) => Quaternion.Normalize(new(x[0], x[1], x[2], x[3]));
            var result = Quaternion.Slerp(ToRotation(Values[first]), ToRotation(Values[second]), amount);
            return [result.X, result.Y, result.Z, result.W];
        }
        return Values[first].Zip(Values[second], (a, b) => a + (b - a) * amount).ToArray();
    }
}

/** Imported nodes need explicit control indices and the exported coordinate parent. */
internal static class ControlGltfDocument
{
    private const int MaximumBytes = 256 * 1024 * 1024;
    internal static readonly Quaternion CoordinateRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);

    /** One explicitly mapped animation can update the selected native clip. */
    internal static IReadOnlyList<ImportedControlChannel> Read(string path, uint clipTag, int frames, int controls)
    {
        byte[] bytes = ReadFile(path);
        byte[]? binary = null;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
                throw new InvalidDataException("Invalid GLB header.");
            byte[]? json = null;
            for (int cursor = 12; cursor < bytes.Length;)
            {
                if (cursor > bytes.Length - 8) throw new InvalidDataException("Truncated GLB chunk.");
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4));
                if (length < 0 || length % 4 != 0 || length > bytes.Length - cursor - 8)
                    throw new InvalidDataException("Invalid GLB chunk length.");
                byte[] chunk = bytes.AsSpan(cursor + 8, length).ToArray();
                if (type == 0x4E4F534A && json is null && cursor == 12) json = chunk;
                else if (type == 0x004E4942 && binary is null && json is not null) binary = chunk;
                else throw new NotSupportedException("GLB contains unsupported or duplicate chunks.");
                cursor += length + 8;
            }
            bytes = json ?? throw new InvalidDataException("GLB has no JSON chunk.");
        }
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0"
            || root.TryGetProperty("extensionsRequired", out var required) && required.GetArrayLength() != 0)
            throw new NotSupportedException("Animation import requires core glTF 2.0.");
        JsonElement nodes = root.GetProperty("nodes");
        if (nodes.GetArrayLength() is < 1 or > 8192) throw new InvalidDataException("Invalid glTF node count.");
        bool identified = false;
        if (root.TryGetProperty("extras", out var extras)
            && extras.TryGetProperty("sunriseAnimationControls", out var identity))
        {
            identified = identity.GetProperty("clipTag").GetString() == $"{clipTag:X8}"
                && identity.GetProperty("frameCount").GetInt32() == frames
                && identity.GetProperty("controlCount").GetInt32() == controls;
            if (!identified) throw new InvalidDataException("Animation identity differs from the selected clip.");
        }
        if (!identified)
            identified = nodes.EnumerateArray().Any(node => node.TryGetProperty("extras", out var properties)
                && properties.TryGetProperty("sunriseCoordinates", out var marker) && marker.ValueKind == JsonValueKind.True
                && properties.TryGetProperty("sunriseClipTag", out var tag) && tag.GetString() == $"{clipTag:X8}"
                && properties.TryGetProperty("sunriseFrameCount", out var frameCount) && frameCount.GetInt32() == frames
                && properties.TryGetProperty("sunriseControlCount", out var controlCount) && controlCount.GetInt32() == controls);
        if (!identified)
            throw new InvalidDataException("Export native controls before importing this clip; bone poses need rig retargeting.");
        var nodeIndices = ReadNodes(nodes, controls);
        var buffers = ReadBuffers(root, path, binary);
        var animations = root.GetProperty("animations");
        if (animations.GetArrayLength() != 1) throw new NotSupportedException("Import one animation at a time.");
        var animation = animations[0];
        var samplers = animation.GetProperty("samplers");
        var output = new List<ImportedControlChannel>();
        foreach (var channel in animation.GetProperty("channels").EnumerateArray())
        {
            var target = channel.GetProperty("target");
            int node = target.GetProperty("node").GetInt32();
            if (!nodeIndices.TryGetValue(node, out int index))
                throw new NotSupportedException("Animation channels must target explicit native control nodes.");
            string component = target.GetProperty("path").GetString() ?? "";
            int width = component switch { "translation" or "scale" => 3, "rotation" => 4, _ => 0 };
            if (width == 0) throw new NotSupportedException("Only translation, rotation and uniform scale are supported.");
            int samplerIndex = channel.GetProperty("sampler").GetInt32();
            if (samplerIndex < 0 || samplerIndex >= samplers.GetArrayLength()) throw new InvalidDataException("Invalid animation sampler.");
            var sampler = samplers[samplerIndex];
            string interpolation = sampler.TryGetProperty("interpolation", out var mode) ? mode.GetString()! : "LINEAR";
            if (interpolation is not ("LINEAR" or "STEP"))
                throw new NotSupportedException("Bake cubic animation curves to LINEAR keys before importing.");
            float[] times = Accessor(root, buffers, sampler.GetProperty("input").GetInt32(), 1);
            float[] values = Accessor(root, buffers, sampler.GetProperty("output").GetInt32(), width);
            if (times.Length == 0 || values.Length != times.Length * width)
                throw new InvalidDataException("Animation values and times disagree.");
            float last = -1;
            foreach (float time in times)
            {
                if (time < 0 || time <= last || time > (frames - 1) / 30f + 0.0001f)
                    throw new InvalidDataException("Animation times must stay within the native clip duration.");
                last = time;
            }
            var samples = new float[times.Length][];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = values.AsSpan(i * width, width).ToArray();
                if (component == "scale")
                {
                    if (samples[i][0] == 0 || Math.Abs(samples[i][0] - samples[i][1]) > 0.00001f
                        || Math.Abs(samples[i][0] - samples[i][2]) > 0.00001f)
                        throw new NotSupportedException("Native animation scale must be uniform and nonzero.");
                    samples[i] = [samples[i][0]];
                }
                else if (component == "rotation")
                {
                    var q = new Quaternion(samples[i][0], samples[i][1], samples[i][2], samples[i][3]);
                    if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 0.000001f)
                        throw new InvalidDataException("Animation rotation has no finite direction.");
                    q = Quaternion.Normalize(q);
                    samples[i] = [q.X, q.Y, q.Z, q.W];
                }
            }
            if (output.Any(x => x.Index == index && x.Path == component))
                throw new InvalidDataException("A native control component is animated twice.");
            output.Add(new(index, component, times, samples, interpolation));
        }
        if (output.Count == 0) throw new InvalidDataException("Animation has no control channels.");
        return output;
    }

    /** A fixed coordinate parent keeps Blender's world axes out of native control channels. */
    private static Dictionary<int, int> ReadNodes(JsonElement nodes, int controls)
    {
        var result = new Dictionary<int, int>();
        int rootIndex = -1;
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            var node = nodes[i];
            if (!node.TryGetProperty("extras", out var extras)) continue;
            if (extras.TryGetProperty("sunriseControlIndex", out var control))
            {
                int index = control.GetInt32();
                if (index < 0 || index >= controls || result.ContainsValue(index)
                    || node.TryGetProperty("matrix", out _) || node.TryGetProperty("children", out _))
                    throw new InvalidDataException("Native control mapping is invalid.");
                result.Add(i, index);
            }
            if (extras.TryGetProperty("sunriseCoordinates", out var coordinate) && coordinate.GetBoolean())
            {
                if (rootIndex >= 0) throw new InvalidDataException("Animation has duplicate coordinate roots.");
                rootIndex = i;
                if (node.TryGetProperty("matrix", out _) || node.TryGetProperty("translation", out _)
                    || node.TryGetProperty("scale", out _))
                    throw new NotSupportedException("Keep the exported coordinate root unchanged.");
                var q = node.GetProperty("rotation").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (q.Length != 4 || q.Any(x => !float.IsFinite(x))
                    || Math.Abs(q.Sum(x => x * x) - 1) > 0.0001f
                    || Math.Abs(Quaternion.Dot(new(q[0], q[1], q[2], q[3]), CoordinateRotation)) < 0.99999f)
                    throw new NotSupportedException("Keep the exported coordinate rotation unchanged.");
            }
        }
        if (rootIndex < 0) throw new InvalidDataException("The native control coordinate root is missing.");
        int[] children = nodes[rootIndex].GetProperty("children").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (children.Distinct().Count() != children.Length || !children.Order().SequenceEqual(result.Keys.Order()))
            throw new NotSupportedException("Keep the exported native control hierarchy unchanged.");
        for (int i = 0; i < nodes.GetArrayLength(); i++)
            if (i != rootIndex && nodes[i].TryGetProperty("children", out var childrenOfOther)
                && childrenOfOther.EnumerateArray().Any(x => result.ContainsKey(x.GetInt32()) || x.GetInt32() == rootIndex))
                throw new NotSupportedException("Native controls cannot have an additional parent.");
        return result;
    }

    /** External buffers must remain inside the imported file's directory. */
    private static byte[][] ReadBuffers(JsonElement root, string path, byte[]? binary)
    {
        var declarations = root.GetProperty("buffers");
        if (declarations.GetArrayLength() is < 1 or > 64) throw new InvalidDataException("Invalid glTF buffer count.");
        var result = new byte[declarations.GetArrayLength()][];
        long total = 0;
        for (int i = 0; i < result.Length; i++)
        {
            var item = declarations[i];
            int length = item.GetProperty("byteLength").GetInt32();
            if (length < 0 || (total += length) > MaximumBytes) throw new InvalidDataException("Animation buffers are too large.");
            if (item.TryGetProperty("uri", out var uriValue))
            {
                string uri = uriValue.GetString() ?? "";
                const string prefix = "data:application/octet-stream;base64,";
                if (uri.StartsWith(prefix, StringComparison.Ordinal)) result[i] = Convert.FromBase64String(uri[prefix.Length..]);
                else
                {
                    string relative = Uri.UnescapeDataString(uri);
                    if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Contains(".."))
                        throw new InvalidDataException("Animation buffer must stay beside its glTF file.");
                    string bufferPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, relative));
                    for (string? current = bufferPath; current is not null; current = Path.GetDirectoryName(current))
                        if ((File.Exists(current) || Directory.Exists(current))
                            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Animation buffer paths cannot use links.");
                    result[i] = ReadFile(bufferPath);
                }
            }
            else result[i] = i == 0 && binary is not null ? binary : throw new InvalidDataException("Animation buffer is missing.");
            bool paddedBinary = i == 0 && !item.TryGetProperty("uri", out _) && binary is not null;
            if (result[i].Length < length || result[i].Length > length + (paddedBinary ? 3L : 0L))
                throw new InvalidDataException("Animation buffer length is invalid.");
            if (result[i].Length != length) result[i] = result[i].AsSpan(0, length).ToArray();
        }
        return result;
    }

    /** Only bounded float accessors without sparse storage reach native encoding. */
    private static float[] Accessor(JsonElement root, byte[][] buffers, int index, int width)
    {
        var accessors = root.GetProperty("accessors");
        if (index < 0 || index >= accessors.GetArrayLength()) throw new InvalidDataException("Animation accessor is missing.");
        var accessor = accessors[index];
        string type = width == 1 ? "SCALAR" : width == 3 ? "VEC3" : "VEC4";
        if (accessor.GetProperty("componentType").GetInt32() != 5126 || accessor.GetProperty("type").GetString() != type
            || accessor.TryGetProperty("sparse", out _) || accessor.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean())
            throw new NotSupportedException("Animation accessors must contain ordinary floating-point values.");
        int count = accessor.GetProperty("count").GetInt32();
        if (count <= 0 || (long)count * width > 8_000_000) throw new InvalidDataException("Animation accessor is too large.");
        var views = root.GetProperty("bufferViews");
        int viewIndex = accessor.GetProperty("bufferView").GetInt32();
        if (viewIndex < 0 || viewIndex >= views.GetArrayLength()) throw new InvalidDataException("Animation buffer view is missing.");
        var view = views[viewIndex];
        int buffer = view.GetProperty("buffer").GetInt32(), viewOffset = Int(view, "byteOffset"), offset = Int(accessor, "byteOffset");
        int viewLength = view.GetProperty("byteLength").GetInt32();
        int stride = view.TryGetProperty("byteStride", out var strideValue) ? strideValue.GetInt32() : width * 4;
        if (buffer < 0 || buffer >= buffers.Length || viewOffset < 0 || offset < 0 || offset % 4 != 0 || viewOffset % 4 != 0
            || stride < width * 4 || stride > 252 || stride % 4 != 0 || viewLength < 0
            || viewOffset > buffers[buffer].Length - viewLength || (long)offset + (long)(count - 1) * stride + width * 4 > viewLength)
            throw new InvalidDataException("Animation accessor exceeds its buffer view.");
        float[] values = new float[count * width];
        for (int i = 0; i < count; i++)
            for (int axis = 0; axis < width; axis++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(buffers[buffer].AsSpan(viewOffset + offset + i * stride + axis * 4));
                if (!float.IsFinite(value)) throw new InvalidDataException("Animation value is not finite.");
                values[i * width + axis] = value;
            }
        return values;
    }

    private static int Int(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
    private static byte[] ReadFile(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Animation file is too large.");
        return File.ReadAllBytes(path);
    }
}
