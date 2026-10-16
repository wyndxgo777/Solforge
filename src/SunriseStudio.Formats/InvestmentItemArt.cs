using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Item art rows share the item index and refer to immutable field and handle pools. */
public static class InvestmentItemArt
{
    public const uint TableTag = 0x81A28B23, ClassId = 0x808056CC;
    private const int HandlesField = 8, IntegersField = 24, FieldsField = 40, ItemsField = 56;
    private const int ItemStride = 16, FieldStride = 8;
    private const uint HandleClass = 0x80800014, IntegerClass = 0x80800007, FieldClass = 0x808056D6, ItemClass = 0x808056D1;
    // The four pools in descriptor order; none of their rows holds a pointer.
    private static readonly (int Field, uint Class, int Stride)[] Pools =
        [(HandlesField, HandleClass, 4), (IntegersField, IntegerClass, 4), (FieldsField, FieldClass, FieldStride),
         (ItemsField, ItemClass, ItemStride)];

    /** Number of item rows in the table. */
    public static int Count(ReadOnlyMemory<byte> table) => new TagReader(table).Array(ItemsField, ItemClass, ItemStride).Count;

    /** Type-five fields name sandbox displays whose text and icons the UI must load. */
    public static IReadOnlyList<ushort> ReadSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        var result = new List<ushort>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 5) continue;
            var integers = reader.Array(IntegersField, IntegerClass, 4);
            int first = reader.I32(at), count = reader.U8(at + 5);
            if (first < 0 || first > integers.Count - count)
                throw new InvalidDataException("The item art perks leave the integer pool.");
            for (int element = 0; element < count; element++)
            {
                uint value = reader.U32(integers.Data + (first + element) * 4);
                if (value >= InvestmentItemClone.MaximumIndexRows)
                    throw new InvalidDataException("An item art perk index is outside the native range.");
                result.Add((ushort)value);
            }
        }
        return result;
    }

    /** Perk dependencies copy shared pools before changing an item's field list. */
    public static byte[] SetSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex,
        IReadOnlyList<ushort> perks)
    {
        if (perks.Count > byte.MaxValue || perks.Any(index => index >= InvestmentItemClone.MaximumIndexRows))
            throw new InvalidDataException("The item art perk list exceeds the native range.");
        if (ReadSandboxPerks(table, itemIndex).SequenceEqual(perks)) return table.ToArray();
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        var allFields = reader.Array(FieldsField, FieldClass, FieldStride);
        var replacement = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 5) replacement.Add(table.Span.Slice(at, FieldStride).ToArray());
        }
        var values = new List<byte[]>();
        if (perks.Count > 0)
        {
            var integers = reader.Array(IntegersField, IntegerClass, 4);
            byte[] field = new byte[FieldStride];
            BinaryPrimitives.WriteInt32LittleEndian(field, integers.Count);
            field[4] = 5; field[5] = (byte)perks.Count;
            replacement.Add(field);
            foreach (ushort perk in perks)
            {
                byte[] value = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(value, perk);
                values.Add(value);
            }
        }
        if (replacement.Count > byte.MaxValue)
            throw new InvalidDataException("The item has too many art fields.");
        byte[] output = Append(table, integers: values, fields: replacement);
        int target = ItemOffset(new TagReader(output), itemIndex);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(target + 8), allFields.Count);
        output[target + 12] = (byte)replacement.Count;
        return output;
    }

    /** A cloned item's UI dependencies follow its authored sandbox perk array. */
    public static byte[] SynchronizeSandboxPerks(ReadOnlyMemory<byte> table, int itemIndex,
        ReadOnlyMemory<byte> definition)
    {
        var reader = new TagReader(definition);
        int block = reader.Relative(112);
        if (block < 0) return SetSandboxPerks(table, itemIndex, []);
        if (block < 4 || reader.U32(block - 4) != 0x808077B9)
            throw new InvalidDataException("The item stats block has an unsupported layout.");
        var rows = reader.Array(block + 16, 0x808077BC, 24);
        var perks = Enumerable.Range(0, rows.Count).Select(index => reader.U16(rows.Data + index * 24))
            .Distinct().ToArray();
        return SetSandboxPerks(table, itemIndex, perks);
    }

    /** New item indices must grow this table in the same order as the item definitions. */
    public static byte[] CloneRow(ReadOnlyMemory<byte> table, int sourceItem, int targetItem)
    {
        var reader = new TagReader(table);
        var items = reader.Array(ItemsField, ItemClass, ItemStride);
        if (targetItem != items.Count || targetItem >= InvestmentItemClone.MaximumIndexRows)
            throw new InvalidDataException("Item art rows must follow the item index without gaps.");
        int row = ItemOffset(reader, sourceItem);
        _ = Fields(reader, row);
        return Append(table, items: [table.Span.Slice(row, ItemStride).ToArray()]);
    }

    /** A static item icon is one type-one field containing one container handle. */
    public static uint ReadPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex)
    {
        var reader = new TagReader(table);
        var fields = Fields(reader, ItemOffset(reader, itemIndex));
        int primary = PrimaryField(reader, fields);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        int first = reader.I32(primary);
        if (first < 0 || first >= handles.Count)
            throw new InvalidDataException("The item art icon leaves the handle pool.");
        return reader.U32(handles.Data + first * 4);
    }

    /** Changing one item copies its field list so shared source fields keep their old targets. */
    public static byte[] SetPrimaryIcon(ReadOnlyMemory<byte> table, int itemIndex, uint containerTag)
    {
        if (containerTag is 0 or uint.MaxValue)
            throw new InvalidDataException("An item icon needs a container tag.");
        if (ReadPrimaryIcon(table, itemIndex) == containerTag) return table.ToArray();
        var reader = new TagReader(table);
        int row = ItemOffset(reader, itemIndex);
        var fields = Fields(reader, row);
        int primary = PrimaryField(reader, fields);
        var handles = reader.Array(HandlesField, HandleClass, 4);
        var allFields = reader.Array(FieldsField, FieldClass, FieldStride);
        byte[] handle = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(handle, containerTag);
        var copies = new List<byte[]>();
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            byte[] field = table.Span.Slice(at, FieldStride).ToArray();
            if (at == primary) BinaryPrimitives.WriteInt32LittleEndian(field, handles.Count);
            copies.Add(field);
        }
        byte[] output = Append(table, handles: [handle], fields: copies);
        int target = ItemOffset(new TagReader(output), itemIndex);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(target + 8), allFields.Count);
        return output;
    }

    /**
     * The installed table plus one copy of each pool that grew, at the end in field order.
     * Pools whose row count did not change are written back in place.
     * @param original The installed table.
     * @param current The edited table; every pool keeps its rows and row numbers.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var stock = new TagReader(original);
        var edited = new TagReader(current);
        var before = Pools.Select(pool => stock.Array(pool.Field, pool.Class, pool.Stride)).ToArray();
        var after = Pools.Select(pool => edited.Array(pool.Field, pool.Class, pool.Stride)).ToArray();
        if (Enumerable.Range(0, Pools.Length).Any(index => after[index].Count < before[index].Count))
            throw new InvalidDataException("An item art pool lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            Pools.SelectMany(pool => InvestmentItemClone.ArraySpans(original, pool.Field, pool.Stride)),
            "The item art table changed outside its pools.");
        // Added rows join each pool once; installed rows then take their edited bytes where they live.
        var appends = new List<RowAppend>();
        for (int index = 0; index < Pools.Length; index++)
        {
            int stride = Pools[index].Stride, installed = before[index].Count;
            var added = Enumerable.Range(installed, after[index].Count - installed)
                .Select(row => current.Span.Slice(after[index].Data + row * stride, stride).ToArray()).ToArray();
            if (added.Length > 0) appends.Add(new(Pools[index].Field, stride, added));
        }
        byte[] output = InvestmentItemClone.AppendRows(original, appends, stockLength: original.Length).Table;
        var rebuilt = new TagReader(output);
        for (int index = 0; index < Pools.Length; index++)
        {
            var pool = Pools[index];
            current.Span.Slice(after[index].Data, before[index].Count * pool.Stride)
                .CopyTo(output.AsSpan(rebuilt.Array(pool.Field, pool.Class, pool.Stride).Data));
        }
        return output;
    }

    /** Grows several pools in one step, each moved at most once; Rebuild drops old copies. */
    private static byte[] Append(ReadOnlyMemory<byte> table, IReadOnlyList<byte[]>? handles = null,
        IReadOnlyList<byte[]>? integers = null, IReadOnlyList<byte[]>? fields = null, IReadOnlyList<byte[]>? items = null)
    {
        IReadOnlyList<byte[]>[] rows = [handles ?? [], integers ?? [], fields ?? [], items ?? []];
        return InvestmentItemClone.AppendRows(table, Pools.Select((pool, index) => new RowAppend(pool.Field, pool.Stride, rows[index]))
            .Where(append => append.Rows.Count > 0).ToArray()).Table;
    }

    private static int ItemOffset(TagReader reader, int index)
    {
        var rows = reader.Array(ItemsField, ItemClass, ItemStride);
        if (index < 0 || index >= rows.Count)
            throw new InvalidDataException("The item has no native art row.");
        return rows.Data + index * ItemStride;
    }

    private static (int Count, int Data) Fields(TagReader reader, int row)
    {
        var fields = reader.Array(FieldsField, FieldClass, FieldStride);
        int first = reader.I32(row + 8), count = reader.U8(row + 12);
        if (first < 0 || first > fields.Count - count)
            throw new InvalidDataException("The item art fields leave their pool.");
        return (count, fields.Data + first * FieldStride);
    }

    private static int PrimaryField(TagReader reader, (int Count, int Data) fields)
    {
        int result = -1;
        for (int index = 0; index < fields.Count; index++)
        {
            int at = fields.Data + index * FieldStride;
            if (reader.U8(at + 4) != 1) continue;
            if (result >= 0 || reader.U8(at + 5) != 1)
                throw new NotSupportedException("Custom item icons need one primary art container.");
            result = at;
        }
        return result >= 0 ? result : throw new NotSupportedException("The item has no primary art container.");
    }
}
