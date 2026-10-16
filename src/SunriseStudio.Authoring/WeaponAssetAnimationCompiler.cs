using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Animation;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Compiled weapon animation: template tag, bytes and frame count. */
public sealed record PreparedWeaponAnimation(uint TemplateTag, byte[] Data, int FrameCount,
    int ChangedControls, float MaximumSampleError);

/** Weapon clips keep the donor's timing and bindings while replacing their control samples. */
public static class WeaponAssetAnimationCompiler
{
    private const uint ClipClass = 0x80808F49, CodecClass = 0x80808F71;
    private const uint ArrayClass = 0x80809FBD, WordClass = 0x8080000A, FloatClass = 0x8080000F;
    private const int CodecBytes = 0x48, SampleRate = 30;

    /** A scratch catalog for decoding staged clips; one serves a whole import. */
    internal static PackageCatalog OpenStaging(PackageCatalog catalog) =>
        PackageCatalog.Open(catalog.GameRoot, catalog.KeySource);

    /** Replacement keys use native control indices and one sample per donor frame. */
    internal static PreparedWeaponAnimation Prepare(PackageCatalog catalog, PackageCatalog staging, uint templateTag,
        IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> replacements, byte[] codec)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entry = RequireClip(catalog, templateTag);
        return PrepareCore(catalog, staging, entry, catalog.Read(templateTag), replacements, codec);
    }

    private static PreparedWeaponAnimation PrepareCore(PackageCatalog catalog, PackageCatalog staging,
        PackageEntry entry, byte[] source, IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> replacements,
        byte[] codec)
    {
        if (ReferenceEquals(catalog, staging))
            throw new ArgumentException("The staging catalog must be a separate catalog.", nameof(staging));
        ArgumentNullException.ThrowIfNull(replacements);
        var clip = Decode(catalog, staging, entry, source);
        if (clip.PoseSpace is not null)
            throw new NotSupportedException("Weapon time clips cannot replace pose-space samples.");
        if (replacements.Count == 0)
            throw new InvalidDataException("At least one weapon control must have authored samples.");
        foreach (var (control, samples) in replacements)
        {
            if (control < 0 || control >= clip.ControlCount || samples.Count != clip.FrameCount)
                throw new InvalidDataException("Weapon samples must match the donor control and frame counts.");
            foreach (var transform in samples) Validate(transform);
        }

        var frames = Enumerable.Range(0, clip.FrameCount).Select(frame =>
            clip.Sample(frame / (float)SampleRate).ToDictionary(value => value.ControlIndex)).ToArray();
        foreach (var (control, samples) in replacements)
            for (int frame = 0; frame < frames.Length; frame++)
            {
                var value = samples[frame];
                frames[frame][control] = new(control, value.Scale, Quaternion.Normalize(value.Rotation),
                    value.Translation, true, true, true);
            }

        int[] scales = frames[0].Values.Where(value => value.HasScale).Select(value => value.ControlIndex).Order().ToArray();
        int[] rotations = frames[0].Values.Where(value => value.HasRotation).Select(value => value.ControlIndex).Order().ToArray();
        int[] translations = frames[0].Values.Where(value => value.HasTranslation).Select(value => value.ControlIndex).Order().ToArray();
        int components = checked(scales.Length + rotations.Length * 4 + translations.Length * 3);
        if ((long)components * clip.FrameCount > 8_000_000)
            throw new InvalidDataException("Weapon control samples exceed the native authoring limit.");

        var words = new ushort[checked(components * clip.FrameCount)];
        var extents = new float[components];
        var biases = new float[components];
        int component = 0;
        void Quantize(int control, int width, Func<AnimationControlSample, float[]> select)
        {
            var samples = frames.Select(frame => select(frame[control])).ToArray();
            if (width == 4)
                for (int frame = 1; frame < samples.Length; frame++)
                    if (samples[frame - 1].Zip(samples[frame], (a, b) => a * b).Sum() < 0)
                        for (int axis = 0; axis < width; axis++) samples[frame][axis] = -samples[frame][axis];
            for (int axis = 0; axis < width; axis++)
            {
                float minimum = samples.Min(value => value[axis]);
                float extent = samples.Max(value => value[axis]) - minimum;
                if (!float.IsFinite(extent))
                    throw new InvalidDataException("Weapon quantization bounds must be finite.");
                biases[component + axis] = minimum;
                extents[component + axis] = extent;
                for (int frame = 0; frame < samples.Length; frame++)
                {
                    double normalized = extent == 0 ? 0 : ((double)samples[frame][axis] - minimum) / extent;
                    words[component * clip.FrameCount + frame * width + axis] =
                        (ushort)Math.Clamp(Math.Round(normalized * ushort.MaxValue), 0, ushort.MaxValue);
                }
            }
            component += width;
        }
        foreach (int control in scales) Quantize(control, 1, value => [value.Scale]);
        foreach (int control in rotations) Quantize(control, 4,
            value => [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W]);
        foreach (int control in translations) Quantize(control, 3,
            value => [value.Translation.X, value.Translation.Y, value.Translation.Z]);

        var writer = new Writer(source);
        int resource = writer.Resource(CodecClass, codec);
        writer.U16(resource, 2);
        writer.U16(resource + 2, checked((ushort)scales.Length));
        writer.U16(resource + 4, checked((ushort)rotations.Length));
        writer.U16(resource + 6, checked((ushort)translations.Length));
        writer.U32(resource + 0x10, checked((uint)clip.FrameCount));
        writer.Array(resource + 0x18, WordClass, Words(words), 2);
        writer.Array(resource + 0x28, FloatClass, Floats(extents), 4);
        writer.Array(resource + 0x38, FloatClass, Floats(biases), 4);
        writer.U32(0x128, 0);
        writer.U32(0x12C, checked((uint)(writer.NextResourceOffset - resource)));
        writer.I64(0x10, 0);
        writer.I64(0x18, resource - 0x18);
        for (int kind = 0; kind < 3; kind++) writer.Array(0xA8 + kind * 16, WordClass, [], 2);
        writer.Array(0xD8, WordClass, Words(scales.Select(value => checked((ushort)value)).ToArray()), 2);
        writer.Array(0xE8, WordClass, Words(rotations.Select(value => checked((ushort)value)).ToArray()), 2);
        writer.Array(0xF8, WordClass, Words(translations.Select(value => checked((ushort)value)).ToArray()), 2);
        byte[] output = writer.Finish();
        float tolerance = extents.DefaultIfEmpty(0).Max() / ushort.MaxValue * 4 + 0.00002f;
        float error = Verify(catalog, staging, entry, clip, output, frames, tolerance);
        return new(entry.TagHash, output, clip.FrameCount, replacements.Count, error);
    }

    /** Added clips use an installed empty-auxiliary profile and the destination rig's binding tables. */
    internal static PreparedWeaponAnimation PrepareNew(PackageCatalog catalog, PackageCatalog staging,
        uint bindingTemplateTag, uint emptyTemplateTag, IReadOnlyDictionary<int, IReadOnlyList<BoneTransform>> samples,
        byte[] codec)
    {
        var binding = new AnimationControlReader(catalog).Read(bindingTemplateTag);
        var empty = new AnimationControlReader(catalog).Read(emptyTemplateTag);
        if (binding.PoseMode != 0 || empty.PoseMode != 0 || empty.PoseSpace is not null
            || empty.AuxiliarySections.Count != 0 || samples.Count != binding.ControlCount
            || !samples.Keys.Order().SequenceEqual(Enumerable.Range(0, binding.ControlCount)))
            throw new NotSupportedException("A new weapon clip needs all destination controls and an absolute empty-auxiliary template.");
        int frames = samples.Values.First().Count;
        if (frames is < 2 or > short.MaxValue || samples.Values.Any(values => values.Count != frames))
            throw new InvalidDataException("New weapon control samples need one shared frame count.");
        byte[] source = catalog.Read(emptyTemplateTag).ToArray();
        byte[] bound = catalog.Read(bindingTemplateTag);
        if (BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(0x90)) != 0
            || BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(0x108)) != 0
            || BinaryPrimitives.ReadInt64LittleEndian(source.AsSpan(0x160)) != 0)
            throw new NotSupportedException("New weapon clips require an empty channel, auxiliary-map and event profile.");
        var writer = new Writer(source);
        writer.I64(0x10, 0); writer.I64(0x18, 0);
        writer.U32(0x128, 0); writer.U32(0x12C, 0);
        writer.U16(0x13C, checked((ushort)frames));
        writer.U16(0x13E, checked((ushort)binding.NodeCount));
        writer.U16(0x140, checked((ushort)binding.ControlCount));
        writer.U32(0xA0, BinaryPrimitives.ReadUInt32LittleEndian(bound.AsSpan(0xA0)));
        writer.U32(0xA4, BinaryPrimitives.ReadUInt32LittleEndian(bound.AsSpan(0xA4)));
        for (int field = 0xA8; field <= 0xF8; field += 16) writer.Array(field, WordClass, [], 2);
        byte[] groups = new byte[checked(binding.ControlGroups.Count * 8)];
        for (int index = 0; index < binding.ControlGroups.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(groups.AsSpan(index * 8), binding.ControlGroups[index].NameHash);
            BinaryPrimitives.WriteUInt16LittleEndian(groups.AsSpan(index * 8 + 4), checked((ushort)binding.ControlGroups[index].ControlCount));
        }
        writer.Array(0x170, 0x8080907F, groups, 8);
        var keys = new byte[checked(binding.RetargetKeys.Count * 4)];
        for (int index = 0; index < binding.RetargetKeys.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(keys.AsSpan(index * 4), binding.RetargetKeys[index]);
        writer.Array(0x150, 0x80800070, keys, 4);
        byte[] prepared = writer.Finish();
        var entry = RequireClip(catalog, emptyTemplateTag);
        return PrepareCore(catalog, staging, entry, prepared, samples, codec);
    }

    /** Decodes clip bytes in place of the template entry; other tags read through the catalog. */
    private static AnimationControlClip Decode(PackageCatalog catalog, PackageCatalog staging,
        PackageEntry entry, byte[] bytes)
    {
        staging.SetOverlayEntries([new(entry with { Size = bytes.Length })]);
        staging.ReadOverride = tag => tag == entry.TagHash ? bytes : catalog.Read(tag);
        return new AnimationControlReader(staging).Read(entry.TagHash);
    }

    /** The installed codec-2 header of a clip that decodes; read once per import. */
    internal static byte[] CodecHeader(PackageCatalog catalog, uint tag)
    {
        RequireClip(catalog, tag);
        byte[] data = catalog.Read(tag);
        int offset = FindCodec(data);
        if (offset < 0) throw new NotSupportedException("The codec template needs an installed codec-2 section.");
        _ = new AnimationControlReader(catalog).Read(tag);
        return data.AsSpan(offset, CodecBytes).ToArray();
    }

    private static int FindCodec(byte[] data)
    {
        if (data.Length < 0x190) return -1;
        foreach (int field in new[] { 0x10, 0x18 })
        {
            long relative = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(field));
            long offset = relative + field;
            if (relative != 0 && offset >= 4 && offset <= data.Length - CodecBytes
                && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset - 4)) == CodecClass
                && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset)) == 2) return (int)offset;
        }
        return -1;
    }

    /** Readback checks every decoded control while retaining all clip bindings and timing. */
    private static float Verify(PackageCatalog catalog, PackageCatalog staging, PackageEntry entry,
        AnimationControlClip source, byte[] bytes, IReadOnlyList<Dictionary<int, AnimationControlSample>> expected,
        float tolerance)
    {
        var actual = Decode(catalog, staging, entry, bytes);
        if (actual.FrameCount != source.FrameCount || actual.ControlCount != source.ControlCount
            || actual.NodeCount != source.NodeCount || actual.PoseMode != source.PoseMode
            || !actual.ControlGroups.SequenceEqual(source.ControlGroups)
            || !actual.RetargetKeys.SequenceEqual(source.RetargetKeys))
            throw new InvalidDataException("Compiled weapon animation changed its donor bindings.");
        float maximum = 0;
        for (int frame = 0; frame < expected.Count; frame++)
        {
            var decoded = actual.Sample(frame / (float)SampleRate).ToDictionary(value => value.ControlIndex);
            if (decoded.Count != expected[frame].Count)
                throw new InvalidDataException("Compiled weapon animation changed its control set.");
            foreach (var (index, target) in expected[frame])
            {
                if (!decoded.TryGetValue(index, out var value) || target.HasScale != value.HasScale
                    || target.HasRotation != value.HasRotation || target.HasTranslation != value.HasTranslation)
                    throw new InvalidDataException("Compiled weapon animation changed its component maps.");
                float error = Math.Max(Math.Abs(target.Scale - value.Scale),
                    Vector3.Distance(target.Translation, value.Translation));
                float sign = Quaternion.Dot(target.Rotation, value.Rotation) < 0 ? -1 : 1;
                float[] a = [target.Rotation.X, target.Rotation.Y, target.Rotation.Z, target.Rotation.W];
                float[] b = [value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W];
                error = Math.Max(error, a.Zip(b, (left, right) => Math.Abs(left - sign * right)).Max());
                maximum = Math.Max(maximum, error);
                if (error > tolerance)
                    throw new InvalidDataException("Compiled weapon animation exceeds its sample error limit.");
            }
        }
        return maximum;
    }

    private static void Validate(BoneTransform value)
    {
        if (!float.IsFinite(value.Scale) || value.Scale <= 0
            || !float.IsFinite(value.Translation.X) || !float.IsFinite(value.Translation.Y)
            || !float.IsFinite(value.Translation.Z) || !float.IsFinite(value.Rotation.LengthSquared())
            || Math.Abs(value.Rotation.LengthSquared() - 1) > 0.01f)
            throw new InvalidDataException("Weapon controls need finite translations, unit rotations and positive scale.");
    }

    private static PackageEntry RequireClip(PackageCatalog catalog, uint tag) =>
        catalog.TryGetEntry(tag, out var entry) && entry.Reference == ClipClass ? entry
            : throw new InvalidDataException("Weapon animation template is not a native clip.");

    private static byte[] Words(ushort[] values)
    {
        var bytes = new byte[checked(values.Length * 2)];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
        return bytes;
    }

    private static byte[] Floats(float[] values)
    {
        var bytes = new byte[checked(values.Length * 4)];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), values[index]);
        return bytes;
    }

    /** Appended typed resources leave donor-relative pointers at their original offsets. */
    private sealed class Writer(byte[] source)
    {
        private readonly List<byte> bytes = [.. source];
        internal int NextResourceOffset => checked(((bytes.Count + 15) & ~15) + 16);

        /** Writes a ushort at an offset in the output. */
        internal void U16(int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(CollectionsMarshal.AsSpan(bytes)[offset..], value);
        /** Writes a uint at an offset in the output. */
        internal void U32(int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(bytes)[offset..], value);
        /** Writes a long at an offset in the output. */
        internal void I64(int offset, long value) =>
            BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshal.AsSpan(bytes)[offset..], value);

        /** Appends a typed resource on a 16-byte boundary and returns its payload offset. */
        internal int Resource(uint type, byte[] payload)
        {
            while ((bytes.Count & 15) != 0) bytes.Add(0);
            int start = bytes.Count + 16;
            bytes.AddRange(new byte[16]);
            bytes.AddRange(payload);
            U32(start - 4, type);
            return start;
        }

        /** Appends an array resource and links it from a field with a relative pointer. */
        internal void Array(int field, uint type, byte[] payload, int stride)
        {
            I64(field, payload.Length / stride);
            if (payload.Length == 0) { I64(field + 8, 0); return; }
            int header = Resource(ArrayClass, new byte[16 + payload.Length]);
            I64(field + 8, header - field - 8);
            I64(header, payload.Length / stride);
            U32(header + 8, type);
            payload.CopyTo(CollectionsMarshal.AsSpan(bytes)[(header + 16)..]);
        }

        /** Writes the size header and returns the finished bytes. */
        internal byte[] Finish()
        {
            I64(0, bytes.Count);
            return bytes.ToArray();
        }
    }
}
