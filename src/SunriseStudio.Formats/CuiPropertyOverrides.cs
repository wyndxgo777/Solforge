using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** An added property override: selector, value pool and the encoded value bytes. */
public sealed record CuiPropertyOverride(ulong Selector, int PoolDescriptor, ReadOnlyMemory<byte> Value);

/** Override-adding part of property editing. */
public static partial class CuiPropertyEdits
{
    /** Added overrides join the same typed pools and selector paths as native stored properties. */
    public static byte[] Upsert(ReadOnlySpan<byte> bytes, CuiDocument document, CuiComponent component,
        IReadOnlyList<CuiPropertyOverride> values)
    {
        if (document.ClassHash != 0x80804825 || document.SourceSha256 != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new InvalidDataException("The CUI resource changed. Reload it before editing.");
        var owner = document.Objects.SingleOrDefault(row => row.Components.Contains(component))
            ?? throw new InvalidDataException("The component does not belong to this resource.");
        if (values.Select(value => value.Selector).Distinct().Count() != values.Count)
            throw new InvalidDataException("A property override was supplied twice.");
        Dictionary<CuiProperty, ReadOnlyMemory<byte>> replacements = [];
        List<CuiPropertyOverride> added = [];
        foreach (var value in values)
        {
            var schema = CuiDocument.PoolSchema(value.PoolDescriptor);
            if (value.Value.Length != schema.Stride || schema.Class is not (0x8080468D or 0x8080468B or 0x80804694
                or 0x80804693 or 0x80804696 or 0x80804692 or 0x80804690 or 0x8080468F))
                throw new InvalidDataException("The property has unsupported typed storage.");
            if (schema.Class is 0x80804694 or 0x80804693)
                for (int at = 0; at < value.Value.Length; at += 4)
                    if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(value.Value.Span[at..])))
                        throw new InvalidDataException("Enter finite property values.");
            var current = component.Properties.LastOrDefault(property => property.Direct && property.Selector == value.Selector);
            if (current is null) added.Add(value);
            else
            {
                if (current.PoolDescriptor != value.PoolDescriptor)
                    throw new InvalidDataException("Keep the stored property's native pool and units.");
                if (!bytes.Slice(current.ValueOffset, value.Value.Length).SequenceEqual(value.Value.Span))
                    replacements.Add(current, value.Value);
            }
        }
        if (added.Count == 0 && replacements.Count == 0) return bytes.ToArray();
        var offsets = component.Properties.Select(property => property.RecordOffset).ToHashSet();
        if (document.Components.Any(other => !ReferenceEquals(other, component)
            && (other.Offset == component.Offset || other.Properties.Any(property => offsets.Contains(property.RecordOffset)))))
        {
            byte[] detached = bytes.ToArray();
            var oldComponents = new TagReader(detached).Array(owner.Offset + 32, 0x808046D4, 24);
            int index = (component.Offset - oldComponents.Data) / 24;
            int newComponents = CloneArray(ref detached, owner.Offset + 32, 0x808046D4, 24, [16]);
            int newComponent = newComponents + index * 24;
            _ = CloneArray(ref detached, newComponent + 8, 0x80804858, 24, [0, 16]);
            BinaryPrimitives.WriteInt64LittleEndian(detached, detached.Length);
            var copy = CuiDocument.Read(document.TagHash, document.ClassHash, detached, nativeTypes: document.NativeTypes);
            return Upsert(detached, copy, copy.Objects[owner.Index].Components[index], values);
        }
        byte[] output = Apply(bytes, document, replacements);
        if (added.Count == 0) return output;
        var prepared = CuiDocument.Read(document.TagHash, document.ClassHash, output, nativeTypes: document.NativeTypes);
        Dictionary<ulong, int> addresses = [];
        foreach (var group in added.GroupBy(value => value.PoolDescriptor))
        {
            var (type, stride) = CuiDocument.PoolSchema(group.Key);
            var old = new TagReader(output).Array(group.Key, type, stride);
            int count = checked(old.Count + group.Count());
            if (count > 1_048_576) throw new InvalidDataException("CUI value pool exceeds its supported size.");
            int start = AppendArray(group.Key, count, stride, type);
            if (old.Count > 0) output.AsSpan(old.Data, old.Count * stride).CopyTo(output.AsSpan(start));
            foreach (var property in prepared.Properties.Where(property => property.PoolDescriptor == group.Key))
                Q(property.RecordOffset + 16, start + property.ValueOffset - old.Data - property.RecordOffset - 16);
            int index = old.Count;
            foreach (var value in group)
            {
                int at = start + index++ * stride;
                value.Value.Span.CopyTo(output.AsSpan(at)); addresses.Add(value.Selector, at);
            }
        }
        int empty = EmptySlot();
        int targetComponent = component.Offset;
        if (document.Components.Count(row => row.Offset == component.Offset) > 1)
        {
            var r = new TagReader(output); var components = r.Array(owner.Offset + 32, 0x808046D4, 24);
            int start = AppendArray(owner.Offset + 32, components.Count, 24, 0x808046D4);
            output.AsSpan(components.Data, components.Count * 24).CopyTo(output.AsSpan(start));
            for (int i = 0; i < components.Count; i++)
            {
                int source = components.Data + i * 24, target = start + i * 24;
                Rebase(source + 16, target + 16);
                if (source == component.Offset) targetComponent = target;
            }
        }
        var oldRows = new TagReader(output).Array(targetComponent + 8, 0x80804858, 24);
        int propertyCount = checked(oldRows.Count + added.Count);
        if (propertyCount > 65536) throw new InvalidDataException("CUI component has too many properties.");
        int properties = AppendArray(targetComponent + 8, propertyCount, 24, 0x80804858);
        if (oldRows.Count > 0) output.AsSpan(oldRows.Data, oldRows.Count * 24).CopyTo(output.AsSpan(properties));
        for (int i = 0; i < oldRows.Count; i++)
        {
            int source = oldRows.Data + i * 24, target = properties + i * 24;
            Rebase(source, target); Rebase(source + 16, target + 16);
        }
        for (int i = 0; i < added.Count; i++)
        {
            var value = added[i]; int at = properties + (oldRows.Count + i) * 24;
            Q(at, empty - at); Q(at + 8, unchecked((long)value.Selector)); Q(at + 16, addresses[value.Selector] - at - 16);
        }
        Q(0, output.Length);
        _ = CuiDocument.Read(document.TagHash, document.ClassHash, output, nativeTypes: document.NativeTypes);
        return output;

        int EmptySlot()
        {
            var r = new TagReader(output); var slots = r.Array(120, 0x80804943, 16);
            for (int i = 0; i < slots.Count; i++)
                if (r.U64(slots.Data + i * 16) == 0) return slots.Data + i * 16;
            int start = AppendArray(120, checked(slots.Count + 1), 16, 0x80804943);
            if (slots.Count != 0) output.AsSpan(slots.Data, slots.Count * 16).CopyTo(output.AsSpan(start));
            for (int i = 0; i < slots.Count; i++) Rebase(slots.Data + i * 16 + 8, start + i * 16 + 8);
            foreach (var property in prepared.Properties) Redirect(property.RecordOffset);
            var bindings = new TagReader(output).Array(88, 0x808046D8, 56);
            for (int i = 0; i < bindings.Count; i++)
            { Redirect(bindings.Data + i * 56 + 8); Redirect(bindings.Data + i * 56 + 32); }
            return start + slots.Count * 16;

            void Redirect(int pointer)
            {
                int target = new TagReader(output).Relative(pointer);
                if (target >= slots.Data && target < slots.Data + slots.Count * 16 && (target - slots.Data) % 16 == 0)
                    Q(pointer, start + target - slots.Data - pointer);
            }
        }
        int AppendArray(int descriptor, int count, int stride, uint type)
        {
            int header = checked((output.Length + 11) & ~7), start = checked(header + 16);
            Array.Resize(ref output, checked(start + count * stride));
            U(header - 4, 0x80809FBD); Q(header, count); Q(header + 8, type);
            Q(descriptor, count); Q(descriptor + 8, header - descriptor - 8); return start;
        }
        void Rebase(int source, int target)
        { int address = new TagReader(output).Relative(source); Q(target, address < 0 ? 0 : address - target); }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
    }

    /** A shared component or reference array is detached before any selected property changes. */
    private static int CloneArray(ref byte[] bytes, int descriptor, uint type, int stride, int[] pointers)
    {
        var reader = new TagReader(bytes); var source = reader.Array(descriptor, type, stride);
        if (source.Count == 0) return 0;
        int header = checked((bytes.Length + 11) & ~7), data = checked(header + 16);
        Array.Resize(ref bytes, checked(data + source.Count * stride));
        bytes.AsSpan(source.Data, source.Count * stride).CopyTo(bytes.AsSpan(data));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header - 4), 0x80809FBD);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(header), source.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(header + 8), type);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(descriptor), source.Count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(descriptor + 8), header - descriptor - 8);
        for (int i = 0; i < source.Count; i++)
            foreach (int pointer in pointers)
            {
                int original = source.Data + i * stride + pointer, target = data + i * stride + pointer;
                int address = reader.Relative(original);
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(target), address < 0 ? 0 : address - target);
            }
        return data;
    }
}
