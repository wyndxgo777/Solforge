using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Owner-row part of property editing. */
public static partial class CuiPropertyEdits
{
    /** A logical property edit detaches shared owner rows before detaching its pooled value. */
    public static byte[] ApplyToOwner(ReadOnlySpan<byte> bytes, CuiDocument document, CuiPropertyOwner owner,
        ReadOnlyMemory<byte> value)
    {
        if (document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes))
            || (uint)owner.ObjectIndex >= document.Objects.Count
            || (uint)owner.ComponentIndex >= document.Objects[owner.ObjectIndex].Components.Count)
            throw new InvalidDataException("The CUI property owner changed. Reload it before editing.");
        var objectRow = document.Objects[owner.ObjectIndex];
        var component = objectRow.Components[owner.ComponentIndex];
        int index = component.Properties.ToList().FindIndex(property => property.RecordOffset == owner.PropertyOffset);
        if (index < 0) throw new InvalidDataException("The CUI property is not in this owner.");
        var property = component.Properties[index];
        var pool = document.Pools.Single(pool => pool.DescriptorOffset == property.PoolDescriptor);
        if (value.Length != pool.Stride) throw new InvalidDataException("The CUI property size changed.");
        if (bytes.Slice(property.ValueOffset, value.Length).SequenceEqual(value.Span)) return bytes.ToArray();
        bool shared = document.Components.Any(other => !ReferenceEquals(other, component)
            && (other.Offset == component.Offset || other.Properties.Any(candidate => candidate.RecordOffset == property.RecordOffset)));
        if (!shared) return Apply(bytes, document, new Dictionary<CuiProperty, ReadOnlyMemory<byte>> { [property] = value });
        byte[] detached = bytes.ToArray();
        int components = CloneArray(ref detached, objectRow.Offset + 32, 0x808046D4, 24, [16]);
        int target = components + owner.ComponentIndex * 24;
        _ = CloneArray(ref detached, target + 8, 0x80804858, 24, [0, 16]);
        BinaryPrimitives.WriteInt64LittleEndian(detached, detached.Length);
        var copy = CuiDocument.Read(document.TagHash, document.ClassHash, detached, nativeTypes: document.NativeTypes);
        var selected = copy.Objects[owner.ObjectIndex].Components[owner.ComponentIndex].Properties[index];
        return Apply(detached, copy, new Dictionary<CuiProperty, ReadOnlyMemory<byte>> { [selected] = value });
    }
}
