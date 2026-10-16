using System.Buffers.Binary;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Reads and writes emitter constants of particle effects. Scalar and vector channel kinds are the sets below. */
internal static class BundleParticlesCodec
{
    private static readonly HashSet<int> Scalars = [5, 10, 12, 13, 14, 15, 16, 17, 19, 21, 22, 24, 31];
    private static readonly HashSet<int> Vectors = [11, 18, 20];

    /** Only decoded constant channels expose editable scalar or vector fields. */
    internal static BundleEmitter Describe(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != EffectReader.EmitterClass)
            throw new InvalidDataException("Particle constant source is not an emitter.");
        var emitter = new EffectReader(catalog).ReadEmitter(tag)
            ?? throw new InvalidDataException("Particle emitter did not decode.");
        byte[] bytes = catalog.Read(tag);
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        long header = 0x10 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x10));
        if (count < 0 || header < 4 || header + 16 + count * 16 > bytes.Length)
            throw new InvalidDataException("Emitter constants exceed their source.");
        var values = new List<BundleEmitterConstant>();
        foreach (var channel in emitter.Channels.Where(value => value.Kind == 6 && value.Constant is not null))
        {
            int width = Scalars.Contains(channel.Index) ? 1 : Vectors.Contains(channel.Index) ? 3 : 0;
            if (width == 0 || channel.Slot + width > count * 4) continue;
            int offset = checked((int)header + 16 + channel.Slot * 4);
            float[] value = Enumerable.Range(0, width).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + i * 4))).ToArray();
            if (value.Any(x => !float.IsFinite(x))) continue;
            values.Add(new(channel.Index, channel.Name, offset, value));
        }
        return new(tag, values);
    }

    /** Channel identity, storage kind and shared constant slots remain fixed on reimport. */
    internal static IReadOnlyDictionary<uint, byte[]> Import(PackageCatalog catalog, IReadOnlyList<BundleEmitterEdit> edits)
    {
        var result = new Dictionary<uint, byte[]>();
        foreach (var group in edits.GroupBy(edit => edit.TagHash))
        {
            var emitter = Describe(catalog, group.Key);
            byte[] bytes = catalog.Read(group.Key);
            var touched = new Dictionary<int, float>();
            foreach (var edit in group)
            {
                var original = emitter.Constants.SingleOrDefault(value => value.Channel == edit.Channel && value.Offset == edit.Offset)
                    ?? throw new InvalidDataException("Particle constant identity changed.");
                if (edit.Value.Length != original.Value.Length || edit.Value.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("Particle constant width or value is invalid.");
                for (int i = 0; i < edit.Value.Length; i++)
                {
                    int offset = edit.Offset + i * 4;
                    if (touched.TryGetValue(offset, out float prior) && prior != edit.Value[i])
                        throw new InvalidDataException("Particle channels disagree on a shared constant slot.");
                    touched[offset] = edit.Value[i];
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), edit.Value[i]);
                }
            }
            result[group.Key] = bytes;
        }
        return result;
    }
}
