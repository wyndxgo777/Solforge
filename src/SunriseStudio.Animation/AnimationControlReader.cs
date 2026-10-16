using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Codecs 1, 2 and 3 retain the game's control indices and continuous sampling rules. */
public sealed class AnimationControlReader(PackageCatalog catalog)
{
    // Element class of the clip's +0x150 key array and of a retarget row's keys: packed u32 hashes.
    internal const uint RetargetKeyClass = 0x80800070;
    internal const int RetargetKeyStride = 4;

    private sealed class TrackBuilder
    {
        public float[] Scales = [];
        public Quaternion[] Rotations = [];
        public Vector3[] Translations = [];
        public Func<float, float>? ScaleCurve;
        public Func<float, Quaternion>? RotationCurve;
        public Func<float, Vector3>? TranslationCurve;
    }

    /** Unsupported compressed codecs fail before a partial control clip is published. */
    public AnimationControlClip Read(uint tag)
    {
        var info = new ClipMetadataReader(catalog).Read(tag);
        var data = new Data(catalog.Read(tag));
        int nodeCount = data.U16(0x13E), controlCount = data.U16(0x140);
        if (nodeCount is < 1 or > 4096 || controlCount is < 1 or > 4096)
            throw new InvalidDataException("Animation node or control count is invalid.");
        var tracks = Enumerable.Range(0, controlCount).Select(_ => new TrackBuilder()).ToArray();
        ReadSection(data, 0x10, 0xA8, 1, tracks);
        ReadSection(data, 0x18, 0xD8, info.FrameCount, tracks);
        var output = tracks.Select((track, index) => new AnimationControlTrack(index,
            track.Scales, track.Rotations, track.Translations,
            track.ScaleCurve, track.RotationCurve, track.TranslationCurve))
            .Where(track => track.ScaleKeys.Count + track.RotationKeys.Count + track.TranslationKeys.Count > 0)
            .ToArray();
        var groups = data.Rows(0x170, 0x8080907F, 8).Select(row =>
            new AnimationControlGroup(data.U32(row), data.I16(row + 4))).ToArray();
        if (groups.Any(group => group.ControlCount < 0)
            || groups.Sum(group => (long)group.ControlCount) != controlCount)
            throw new InvalidDataException("Clip control groups disagree with its control count.");
        return new AnimationControlClip(tag, info.FrameCount, nodeCount, controlCount,
            data.U16(0x180) >> 8, output,
            info.PayloadSections.Where(section => section.PointerField is not (0x10 or 0x18 or 0x50)).ToArray(), groups,
            ReadPoseSpace(data, info.FrameCount))
        { RetargetKeys = Array.AsReadOnly(data.Rows(0x150, RetargetKeyClass, RetargetKeyStride).Select(data.U32).ToArray()) };
    }

    /** Section class and simplex element class and stride, by the space kind byte at +0x143. */
    private static readonly Dictionary<sbyte, (uint Section, uint Simplex, int Stride)> poseSpaceLayouts = new()
    {
        [1] = (0x80808F5F, 0x80808F69, 4),
        [2] = (0x80808F63, 0x80808F6C, 4),
        [3] = (0x80808F61, 0x80808F6A, 6),
        [4] = (0x80808F65, 0x80808F6D, 6)
    };

    /** A pose space holds one point per clip frame; a clip without one plays by time. */
    private static AnimationPoseSpace? ReadPoseSpace(Data data, int frames)
    {
        if (data.I64(0x50) == 0) return null;
        sbyte kind = unchecked((sbyte)data.U8(0x143));
        if (!poseSpaceLayouts.TryGetValue(kind, out var layout))
            throw new NotSupportedException($"Pose space kind {kind} is not decoded.");
        int section = data.Pointer(0x50);
        if (data.U32(section - 4) != layout.Section)
            throw new InvalidDataException("Pose space section class disagrees with its kind.");
        var points = data.Rows(section, 0x80808F6E, 8)
            .Select(row => new Vector2(data.F32(row), data.F32(row + 4))).ToArray();
        if (points.Length != frames)
            throw new InvalidDataException("Pose space point count differs from the clip frame count.");
        var simplices = data.Rows(section + 0x10, layout.Simplex, layout.Stride)
            .Select(row => Enumerable.Range(0, layout.Stride).Select(i => unchecked((sbyte)data.U8(row + i))).ToArray())
            .ToArray();
        return new AnimationPoseSpace((AnimationPoseSpaceKind)kind, points, simplices);
    }

    /** Each component map assigns tracks to explicit control indices. */
    private static void ReadSection(Data data, int pointerField, int mapsField, int expectedFrames,
        TrackBuilder[] tracks)
    {
        int[] scales = data.Map(mapsField, tracks.Length);
        int[] rotations = data.Map(mapsField + 16, tracks.Length);
        int[] translations = data.Map(mapsField + 32, tracks.Length);
        if (data.I64(pointerField) == 0)
        {
            if (scales.Length + rotations.Length + translations.Length != 0)
                throw new InvalidDataException("Animation maps reference an absent codec payload.");
            return;
        }
        int resource = data.Pointer(pointerField);
        uint resourceClass = data.U32(resource - 4);
        int codec = data.U16(resource);
        if (codec == 1 && resourceClass == 0x80808F72)
        {
            var sparse = new SparseAnimationCodec(data, resource, expectedFrames,
                scales.Length, rotations.Length, translations.Length);
            for (int i = 0; i < scales.Length; i++)
            {
                var track = tracks[scales[i]];
                if (track.Scales.Length != 0) throw new InvalidDataException("Duplicate animation scale control.");
                track.ScaleCurve = sparse.Scale(i);
                track.Scales = Enumerable.Range(0, expectedFrames).Select(x => track.ScaleCurve(x)).ToArray();
            }
            for (int i = 0; i < rotations.Length; i++)
            {
                var track = tracks[rotations[i]];
                if (track.Rotations.Length != 0) throw new InvalidDataException("Duplicate animation rotation control.");
                track.RotationCurve = sparse.Rotation(i);
                track.Rotations = Enumerable.Range(0, expectedFrames).Select(x => track.RotationCurve(x)).ToArray();
            }
            for (int i = 0; i < translations.Length; i++)
            {
                var track = tracks[translations[i]];
                if (track.Translations.Length != 0) throw new InvalidDataException("Duplicate animation translation control.");
                track.TranslationCurve = sparse.Translation(i);
                track.Translations = Enumerable.Range(0, expectedFrames).Select(x => track.TranslationCurve(x)).ToArray();
            }
            return;
        }
        if (!((codec == 3 && resourceClass == 0x80808F6F)
            || (codec == 2 && resourceClass == 0x80808F71)))
            throw new NotSupportedException($"Animation codec {codec}, class 0x{resourceClass:X8}, is not decoded.");
        int frames = checked((int)data.U32(resource + 0x10));
        if (frames != expectedFrames || data.U16(resource + 2) != scales.Length
            || data.U16(resource + 4) != rotations.Length || data.U16(resource + 6) != translations.Length)
            throw new InvalidDataException("Animation codec counts disagree with the clip maps.");
        int components = checked(scales.Length + rotations.Length * 4 + translations.Length * 3);
        ushort[] words = data.Words(resource + (codec == 3 ? 0x38 : 0x18));
        if (words.Length != checked(components * frames))
            throw new InvalidDataException("Animation component stream size disagrees with its track counts.");
        float[] extents = codec == 2 ? data.Floats(resource + 0x28) : [];
        float[] biases = codec == 2 ? data.Floats(resource + 0x38) : [];
        if (codec == 2 && (extents.Length != components || biases.Length != components))
            throw new InvalidDataException("Animation component quantizers disagree with its track counts.");
        int component = 0;
        foreach (int index in scales)
        {
            if (tracks[index].Scales.Length != 0) throw new InvalidDataException("Duplicate animation scale control.");
            float extent = codec == 3 ? data.F32(resource + 0x14) : extents[component];
            float bias = codec == 3 ? data.F32(resource + 0x18) : biases[component];
            var keys = new float[frames];
            for (int frame = 0; frame < frames; frame++)
                keys[frame] = words[component * frames + frame] * extent / 65535f + bias;
            tracks[index].Scales = keys;
            component++;
        }
        foreach (int index in rotations)
        {
            if (tracks[index].Rotations.Length != 0) throw new InvalidDataException("Duplicate animation rotation control.");
            var keys = new Quaternion[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int source = component * frames + frame * 4;
                float Value(int axis) => codec == 3 ? words[source + axis] * (2f / 65535f) - 1
                    : words[source + axis] * (extents[component + axis] / 65535f) + biases[component + axis];
                keys[frame] = new Quaternion(Value(0), Value(1), Value(2), Value(3));
                if (!float.IsFinite(keys[frame].LengthSquared()) || keys[frame].LengthSquared() < 1e-12f)
                    throw new InvalidDataException("Animation rotation key has no finite direction.");
            }
            tracks[index].Rotations = keys;
            component += 4;
        }
        foreach (int index in translations)
        {
            if (tracks[index].Translations.Length != 0) throw new InvalidDataException("Duplicate animation translation control.");
            var keys = new Vector3[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int source = component * frames + frame * 3;
                float Value(int axis)
                {
                    float extent = codec == 3 ? data.F32(resource + 0x1C + axis * 4) : extents[component + axis];
                    float bias = codec == 3 ? data.F32(resource + 0x28 + axis * 4) : biases[component + axis];
                    return words[source + axis] * (extent / 65535f) + bias;
                }
                keys[frame] = new Vector3(Value(0), Value(1), Value(2));
            }
            tracks[index].Translations = keys;
            component += 3;
        }
    }

    /** Typed arrays must remain inside the decoded clip. */
    internal sealed class Data(byte[] bytes)
    {
        private ReadOnlySpan<byte> Slice(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new InvalidDataException("Animation control range exceeds its clip.");
            return bytes.AsSpan(offset, count);
        }
        /** Reads a little-endian u16 at offset, range-checked. */
        public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(offset, 2));
        /** Reads a u8 at offset, range-checked. */
        public byte U8(int offset) => Slice(offset, 1)[0];
        /** Reads an i16 at offset. */
        public short I16(int offset) => unchecked((short)U16(offset));
        /** Reads a little-endian u32 at offset, range-checked. */
        public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
        /** Reads a little-endian i64 at offset, range-checked. */
        public long I64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8));
        /** Reads an f32 at offset; non-finite values are rejected. */
        public float F32(int offset)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("Animation quantizer is not finite.");
        }
        /** Resolves a self-relative pointer field to an absolute offset. */
        public int Pointer(int field) => checked((int)(field + I64(field)));
        /** Row offsets of a typed array field whose element class must match. */
        public int[] Rows(int field, uint expectedClass, int stride)
        {
            var (count, start) = Array(field, expectedClass, stride);
            return Enumerable.Range(0, count).Select(i => start + i * stride).ToArray();
        }
        /** Reads a u16 array field. */
        public ushort[] Words(int field)
        {
            var (count, start) = Array(field, 0x8080000A, 2);
            return Enumerable.Range(0, count).Select(i => U16(start + i * 2)).ToArray();
        }
        /** Reads an i16 array field. */
        public short[] SignedWords(int field)
        {
            var (count, start) = Array(field, 0x80800006, 2);
            return Enumerable.Range(0, count).Select(i => unchecked((short)U16(start + i * 2))).ToArray();
        }
        /** Reads a byte array field. */
        public byte[] Bytes(int field)
        {
            var (count, start) = Array(field, 0x80800009, 1);
            return Slice(start, count).ToArray();
        }
        /** Reads an f32 array field. */
        public float[] Floats(int field)
        {
            var (count, start) = Array(field, 0x8080000F, 4);
            return Enumerable.Range(0, count).Select(i => F32(start + i * 4)).ToArray();
        }
        /** Reads a control index map; it must be strictly increasing and stay below limit. */
        public int[] Map(int field, int limit)
        {
            ushort[] values = Words(field);
            int previous = -1;
            foreach (int value in values)
            {
                if (value >= limit || value <= previous)
                    throw new InvalidDataException("Animation control map is unordered or exceeds the clip.");
                previous = value;
            }
            return values.Select(x => (int)x).ToArray();
        }
        private (int Count, int Start) Array(int field, uint expectedClass, int stride)
        {
            long count = I64(field);
            if (count < 0 || count > 100_000_000) throw new InvalidDataException("Invalid animation array count.");
            if (count == 0) return (0, 0);
            int header = Pointer(field + 8);
            if (U32(header - 4) != 0x80809FBD || U32(header + 8) != expectedClass)
                throw new InvalidDataException("Animation array element class is unsupported.");
            int start = checked(header + 16);
            _ = Slice(start, checked((int)count * stride));
            return ((int)count, start);
        }
    }
}
