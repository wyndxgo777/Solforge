using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Storage half of CuiConfigBindingCompiler: clones shared arrays so each bound value gets private rows. */
public sealed partial class CuiConfigBindingCompiler
{
    /** Private reference rows prevent a config value from changing another owner of shared storage. */
    private static Dictionary<string, (int Row, int Value)> IsolateProperties(ref byte[] output, uint tag,
        IReadOnlyList<ProjectConfigBinding> bindings)
    {
        var selected = bindings.Where(binding => binding.Site.Profile == ConfigBindingLimits.PropertyProfile).ToArray();
        Dictionary<string, (int Row, int Value)> positions = [];
        if (selected.Length == 0) return positions;
        var original = CuiDocument.Read(tag, ResourceClass, output);
        foreach (var owner in selected.GroupBy(binding => binding.Site.OwnerIndex))
        {
            int components = CloneArray(ref output, original.Objects[owner.Key].Offset + 32, 24, [16]);
            foreach (var component in owner.GroupBy(binding => binding.Site.ComponentIndex))
            {
                int references = CloneArray(ref output, components + component.Key * 24 + 8, 24, [0, 16]);
                var old = original.Objects[owner.Key].Components[component.Key];
                foreach (var binding in component)
                {
                    int index = old.Properties.ToList().FindIndex(property => property.RecordOffset == binding.Site.RowOffset);
                    if (index < 0) throw new InvalidDataException("A bound property row disappeared.");
                    positions.Add(binding.Site.Identity, (references + index * 24, 0));
                }
            }
        }
        Put64(output, 0, output.Length);
        var detached = CuiDocument.Read(tag, ResourceClass, output);
        foreach (var group in selected.GroupBy(binding => binding.Site.PoolOffset))
        {
            var pool = detached.Pools.Single(pool => pool.DescriptorOffset == group.Key);
            int count = checked(pool.Count + group.Count());
            if (count > 1_048_576) throw new InvalidDataException("The CUI value pool is too large.");
            int start = AppendArray(ref output, pool.DescriptorOffset, pool.ClassHash, pool.Stride, count);
            output.AsSpan(pool.DataOffset, pool.Count * pool.Stride).CopyTo(output.AsSpan(start));
            foreach (var property in detached.Properties.Where(property => property.PoolDescriptor == group.Key).DistinctBy(property => property.RecordOffset))
                Put64(output, property.RecordOffset + 16, start + property.ValueOffset - pool.DataOffset - property.RecordOffset - 16);
            int index = pool.Count;
            foreach (var binding in group)
            {
                int row = positions[binding.Site.Identity].Row;
                var property = detached.Properties.Single(property => property.RecordOffset == row);
                int value = start + index++ * pool.Stride;
                output.AsSpan(property.ValueOffset, pool.Stride).CopyTo(output.AsSpan(value));
                Put64(output, row + 16, value - row - 16);
                positions[binding.Site.Identity] = (row, value);
            }
        }
        return positions;
    }

    private static int CloneArray(ref byte[] output, int field, int stride, int[] pointers)
    {
        int count = checked((int)BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(field)));
        int source = checked(field + 24 + (int)BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(field + 8)));
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(source - 8));
        int target = AppendArray(ref output, field, type, stride, count);
        output.AsSpan(source, count * stride).CopyTo(output.AsSpan(target));
        for (int row = 0; row < count; row++)
            foreach (int pointer in pointers)
            {
                int before = source + row * stride + pointer, after = target + row * stride + pointer;
                long relative = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(before));
                Put64(output, after, relative == 0 ? 0 : checked(before + relative - after));
            }
        return target;
    }

    private static int AppendArray(ref byte[] output, int field, uint type, int stride, int count)
    {
        int header = checked((output.Length + 11) & ~7), data = checked(header + 16);
        Grow(ref output, checked(data + count * stride));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), 0x80809FBD);
        Put64(output, header, count); Put64(output, header + 8, type);
        Put64(output, field, count); Put64(output, field + 8, header - field - 8);
        return data;
    }
}
