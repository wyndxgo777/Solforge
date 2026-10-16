using System.Buffers.Binary;
using SunriseStudio.Animation;

namespace SunriseStudio.Authoring;

/** One native animation control: frame count, codec and word offsets in the clip data. */
internal sealed record NativeControlChannel(int Index, string Path, int Frames, int Codec,
    int Resource, int Component, int WordStart, int ExtentStart, int BiasStart)
{
    internal int Width => Path == "rotation" ? 4 : Path == "translation" ? 3 : 1;
    internal bool Writable => Codec is 2 or 3;
}

/** Only existing dense sample words are rewritten; clip maps and resource layouts stay intact. */
internal sealed class NativeControlLayout
{
    internal byte[] Data { get; }
    internal IReadOnlyList<NativeControlChannel> Channels { get; }

    /** Static and dynamic sections cannot map the same control component twice. */
    internal NativeControlLayout(byte[] data, AnimationControlClip clip)
    {
        Data = data;
        var channels = new List<NativeControlChannel>();
        ReadSection(channels, 0x10, 0xA8, 1, clip.ControlCount);
        ReadSection(channels, 0x18, 0xD8, clip.FrameCount, clip.ControlCount);
        if (channels.GroupBy(x => (x.Index, x.Path)).Any(x => x.Count() != 1))
            throw new InvalidDataException("Animation component is mapped more than once.");
        Channels = channels.AsReadOnly();
    }

    /** Each component map names controls; codec arrays keep their authored sample ordering. */
    private void ReadSection(List<NativeControlChannel> output, int pointer, int maps, int frames, int controls)
    {
        int[][] indices = Enumerable.Range(0, 3).Select(i => Words(maps + i * 16)).ToArray();
        if (indices.SelectMany(x => x).Any(x => x >= controls))
            throw new InvalidDataException("Animation control map exceeds its control count.");
        if (I64(pointer) == 0)
        {
            if (indices.Any(x => x.Length > 0)) throw new InvalidDataException("Animation maps have no payload.");
            return;
        }
        int resource = Pointer(pointer), codec = U16(resource);
        uint expected = codec switch { 1 => 0x80808F72, 2 => 0x80808F71, 3 => 0x80808F6F, _ => 0U };
        if (expected == 0 || U32(resource - 4) != expected)
            throw new NotSupportedException("Animation codec is not decoded by Sunrise Studio.");
        int wordStart = 0, extents = 0, biases = 0;
        if (codec is 2 or 3)
        {
            int components = indices[0].Length + indices[1].Length * 4 + indices[2].Length * 3;
            if (U32(resource + 0x10) != frames) throw new InvalidDataException("Animation frame count changed.");
            wordStart = Array(resource + (codec == 3 ? 0x38 : 0x18), 0x8080000A, 2, components * frames);
            if (codec == 2)
            {
                extents = Array(resource + 0x28, 0x8080000F, 4, components);
                biases = Array(resource + 0x38, 0x8080000F, 4, components);
            }
        }
        int component = 0;
        string[] paths = ["scale", "rotation", "translation"];
        for (int kind = 0; kind < indices.Length; kind++)
        {
            if (U16(resource + 2 + kind * 2) != indices[kind].Length)
                throw new InvalidDataException("Animation codec counts disagree with its maps.");
            foreach (int index in indices[kind])
            {
                output.Add(new(index, paths[kind], frames, codec, resource, component, wordStart, extents, biases));
                component += kind == 1 ? 4 : kind == 2 ? 3 : 1;
            }
        }
    }

    /** Existing quantizers reject values outside their representable range. */
    internal float Write(byte[] output, NativeControlChannel channel, float[][] values)
    {
        if (!channel.Writable) throw new NotSupportedException("Sparse animation controls cannot be imported yet.");
        if (values.Length != channel.Frames) throw new InvalidDataException("Animation sample count changed.");
        float tolerance = 0;
        for (int axis = 0; axis < channel.Width; axis++)
        {
            var (extent, bias) = Quantizer(channel, axis);
            tolerance = Math.Max(tolerance, Math.Abs(extent) / 65535f * 4 + 0.00002f);
            for (int frame = 0; frame < channel.Frames; frame++)
            {
                float value = values[frame][axis];
                double word = extent == 0 ? 0 : ((double)value - bias) / extent * 65535;
                if (!float.IsFinite(value) || extent == 0 && Math.Abs(value - bias) > 0.000001f
                    || word < -0.1 || word > 65535.1)
                    throw new InvalidDataException($"Control {channel.Index} {channel.Path} exceeds native quantization bounds.");
                int index = channel.Component * channel.Frames + frame * channel.Width + axis;
                ushort quantized = (ushort)Math.Clamp(Math.Round(word), 0, 65535);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(channel.WordStart + index * 2), quantized);
            }
        }
        return tolerance;
    }

    private (float Extent, float Bias) Quantizer(NativeControlChannel channel, int axis)
    {
        if (channel.Codec == 2)
            return (F32(channel.ExtentStart + (channel.Component + axis) * 4),
                F32(channel.BiasStart + (channel.Component + axis) * 4));
        if (channel.Path == "rotation") return (2, -1);
        return channel.Path == "scale" ? (F32(channel.Resource + 0x14), F32(channel.Resource + 0x18))
            : (F32(channel.Resource + 0x1C + axis * 4), F32(channel.Resource + 0x28 + axis * 4));
    }

    /** Components of a control sample for a channel path (scale, rotation or translation). */
    internal static float[] Sample(AnimationControlSample value, string path) => path switch
    {
        "scale" => [value.Scale],
        "rotation" => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W],
        "translation" => [value.Translation.X, value.Translation.Y, value.Translation.Z],
        _ => throw new NotSupportedException("Unknown animation component.")
    };

    private int[] Words(int field)
    {
        int count = checked((int)I64(field));
        int start = Array(field, 0x8080000A, 2, count);
        return Enumerable.Range(0, count).Select(i => (int)U16(start + i * 2)).ToArray();
    }

    /** Typed arrays must match their expected count and remain inside the clip. */
    private int Array(int field, uint type, int stride, int expected)
    {
        long count = I64(field);
        if (count != expected || count is < 0 or > 8_000_000)
            throw new InvalidDataException("Animation array count is invalid.");
        if (count == 0) return 0;
        int header = Pointer(field + 8), start = checked(header + 16);
        if (U32(header - 4) != 0x80809FBD || U32(header + 8) != type)
            throw new InvalidDataException("Animation array type is invalid.");
        Slice(start, checked((int)count * stride));
        return start;
    }

    private int Pointer(int field) => checked((int)(field + I64(field)));
    private ReadOnlySpan<byte> Slice(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Data.Length - length)
            throw new InvalidDataException("Animation data exceeds its clip.");
        return Data.AsSpan(offset, length);
    }
    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
    private long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8));
    private float F32(int offset)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));
        return float.IsFinite(value) ? value : throw new InvalidDataException("Animation quantizer is not finite.");
    }
}
