using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SunriseStudio.Formats;

/** Display stat groups are shared by many items, so one item's curve change gets its own group. */
public static class InvestmentStatGroups
{
    public const uint TableClass = 0x80805CFC;
    private const int GroupsField = 8, GroupStride = 56, StatsField = 16, SpareField = 32;
    private const int StatStride = 24, CurveField = 8, PointStride = 8;
    private const uint GroupClass = 0x80805D02, StatClass = 0x80805D06, CurveClass = 0x80807D1A, ArrayMarker = 0x80809FBD;
    // The native group index is a signed byte checked against the table's row count.
    public const int MaximumGroups = 128;

    /** Number of stat group rows. */
    public static int Count(ReadOnlyMemory<byte> table) => new TagReader(table).Array(GroupsField, GroupClass, GroupStride).Count;

    /** Hashes of every stat group. */
    public static IReadOnlyList<uint> Hashes(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var groups = reader.Array(GroupsField, GroupClass, GroupStride);
        return Enumerable.Range(0, groups.Count).Select(index => reader.U32(groups.Data + index * GroupStride)).ToArray();
    }

    /** The display's stat group, or null when it has no stat block or no group. */
    public static int? ReadDisplayGroup(ReadOnlyMemory<byte> display)
    {
        var reader = new TagReader(display);
        if (!reader.Has(112, 8)) return null;
        int block = reader.Relative(112);
        if (block < 4 || !reader.Has(block, 24) || reader.U32(block - 4) != 0x80805CF1) return null;
        byte group = reader.U8(block + 20);
        return group == byte.MaxValue ? null : group;
    }

    /** An item display selects its stat group by the byte at `+20` of its stat block. */
    public static byte[] SetDisplayGroup(ReadOnlyMemory<byte> display, int group)
    {
        if (group is < 0 or >= MaximumGroups) throw new InvalidDataException("The stat group index is outside the native range.");
        var reader = new TagReader(display);
        int block = reader.Relative(112);
        if (block < 4 || !reader.Has(block, 24) || reader.U32(block - 4) != 0x80805CF1)
            throw new InvalidDataException("The item display has no stat block.");
        byte[] result = display.ToArray();
        result[block + 20] = (byte)group;
        return result;
    }

    /**
     * Appends a copy of `sourceGroup` with its own stat rows and the given curves.
     * @param hash Identity of the new group; it must not name another group.
     * @return The table and the new group's index.
     */
    public static (byte[] Table, int Index) AddGroup(ReadOnlyMemory<byte> table, int sourceGroup, uint hash,
        IReadOnlyDictionary<byte, IReadOnlyList<InvestmentStatPoint>> curves)
    {
        var reader = new TagReader(table);
        var groups = reader.Array(GroupsField, GroupClass, GroupStride);
        if (sourceGroup < 0 || sourceGroup >= groups.Count) throw new InvalidDataException("The source stat group is outside the table.");
        if (groups.Count + 1 > MaximumGroups) throw new InvalidDataException("The stat group table is full.");
        if (Enumerable.Range(0, groups.Count).Any(index => reader.U32(groups.Data + index * GroupStride) == hash))
            throw new InvalidDataException("Another stat group already uses this identity.");
        int source = groups.Data + sourceGroup * GroupStride;
        byte[] row = table.Span.Slice(source, GroupStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(row, hash);
        byte[] output = InvestmentItemClone.AppendRow(table, GroupsField, GroupStride, row, [StatsField, SpareField], source);
        return (SetCurves(output, groups.Count, curves), groups.Count);
    }

    /**
     * Gives `group` a private copy of its stat rows with the given curves replaced.
     * Only a group no installed item uses may be changed this way.
     */
    public static byte[] SetCurves(ReadOnlyMemory<byte> table, int group,
        IReadOnlyDictionary<byte, IReadOnlyList<InvestmentStatPoint>> curves)
    {
        var reader = new TagReader(table);
        var groups = reader.Array(GroupsField, GroupClass, GroupStride);
        if (group < 0 || group >= groups.Count) throw new InvalidDataException("The stat group is outside the table.");
        int row = groups.Data + group * GroupStride;
        var rows = reader.Array(row + StatsField, StatClass, StatStride);
        var present = Enumerable.Range(0, rows.Count).Select(index => reader.U8(rows.Data + index * StatStride)).ToHashSet();
        foreach (var (stat, points) in curves)
        {
            if (!present.Contains(stat)) throw new InvalidDataException($"The stat group has no row for stat {stat}.");
            if (points.Count == 0 || points.Zip(points.Skip(1)).Any(pair => pair.Second.Input <= pair.First.Input))
                throw new InvalidDataException("Stat curve inputs must increase.");
        }
        int rowHeader = Align(table.Length + 4);
        int size = rowHeader + 16 + rows.Count * StatStride;
        var curveHeaders = new Dictionary<byte, int>();
        foreach (var (stat, points) in curves)
        {
            curveHeaders[stat] = Align(size + 4);
            size = curveHeaders[stat] + 16 + points.Count * PointStride;
        }
        byte[] result = new byte[size];
        table.Span.CopyTo(result);
        Header(result, rowHeader, rows.Count, StatClass);
        for (int i = 0; i < rows.Count; i++)
        {
            int from = rows.Data + i * StatStride, to = rowHeader + 16 + i * StatStride;
            table.Span.Slice(from, StatStride).CopyTo(result.AsSpan(to));
            byte stat = result[from];
            if (curveHeaders.TryGetValue(stat, out int curve))
            {
                I64(result, to + CurveField, curves[stat].Count);
                I64(result, to + CurveField + 8, curve - (to + CurveField + 8));
            }
            else if (I64(result, from + CurveField) != 0)
            {
                long target = from + CurveField + 8 + I64(result, from + CurveField + 8);
                I64(result, to + CurveField + 8, target - (to + CurveField + 8));
            }
        }
        foreach (var (stat, points) in curves)
        {
            int header = curveHeaders[stat];
            Header(result, header, points.Count, CurveClass);
            for (int i = 0; i < points.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(header + 16 + i * PointStride), points[i].Input);
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(header + 20 + i * PointStride), points[i].Output);
            }
        }
        I64(result, row + StatsField + 8, rowHeader - (row + StatsField + 8));
        if (BinaryPrimitives.ReadInt64LittleEndian(table.Span) == table.Length) I64(result, 0, result.Length);
        return result;
    }

    /**
     * The installed table plus one live copy of each array Studio added; group indices stay.
     * @param original The installed table.
     * @param current The edited table; it keeps every installed group in order.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var stock = new TagReader(original);
        var edited = new TagReader(current);
        bool sized = stock.Has(0, 8) && stock.U64(0) == (ulong)original.Length;
        var before = stock.Array(GroupsField, GroupClass, GroupStride);
        var after = edited.Array(GroupsField, GroupClass, GroupStride);
        if (before.Count == 0 || after.Count < before.Count || current.Length < original.Length)
            throw new InvalidDataException("The stat group table lost installed groups.");
        for (int index = 0; index < before.Count; index++)
            if (stock.U32(before.Data + index * GroupStride) != edited.U32(after.Data + index * GroupStride))
                throw new InvalidDataException("An installed stat group changed its identity.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, GroupsField, GroupStride),
            "The stat group table changed outside its groups.");

        var output = new List<byte>(current.Length);
        output.AddRange(original.Span);
        var copies = new Dictionary<int, int>();
        // Group rows keep their installed header when the count holds or they end the installed table.
        bool inPlace = after.Count == before.Count || before.Data + before.Count * GroupStride == original.Length;
        int groups = Copy(after.Data, after.Count, GroupStride, inPlace ? before.Data : -1);
        Set(GroupsField, after.Count);
        Set(GroupsField + 8, groups - 16 - GroupsField - 8);
        for (int index = 0; index < after.Count; index++)
        {
            int from = after.Data + index * GroupStride, to = groups + index * GroupStride;
            if (edited.Relative(from + SpareField + 8) >= original.Length)
                throw new NotSupportedException("A stat group's spare array lies outside the installed table.");
            Keep(from + SpareField, to + SpareField, 0, 0);
            int stats = Keep(from + StatsField, to + StatsField, StatClass, StatStride);
            if (stats < 0) continue;
            var rows = edited.Array(from + StatsField, StatClass, StatStride);
            for (int row = 0; row < rows.Count; row++)
                Keep(rows.Data + row * StatStride + CurveField, stats + row * StatStride + CurveField, CurveClass, PointStride);
        }
        byte[] result = output.ToArray();
        if (sized) I64(result, 0, result.Length);
        return result;

        // Points the descriptor at `to` where the one at `from` points; an array Studio added is copied.
        // Returns the copy's data offset, or -1 when nothing was copied.
        int Keep(int from, int to, uint type, int stride)
        {
            int header = edited.Relative(from + 8);
            var array = edited.Array(from, type, stride);
            if (header < 0 || header >= original.Length && array.Count == 0) { Set(to + 8, 0); return -1; }
            if (header < original.Length) { Set(to + 8, header - to - 8); return -1; }
            int data = Copy(array.Data, array.Count, stride);
            Set(to + 8, data - 16 - to - 8);
            return data;
        }
        // A Studio array two descriptors share is copied once.
        int Copy(int from, int count, int stride, int at = -1)
        {
            if (copies.TryGetValue(from, out int known)) return known;
            int data = at >= 0 ? at : Align(output.Count + 4) + 16;
            int end = checked(data + count * stride);
            if (end > output.Count) output.AddRange(new byte[end - output.Count]);
            var span = CollectionsMarshal.AsSpan(output);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(data - 20)..], ArrayMarker);
            BinaryPrimitives.WriteInt64LittleEndian(span[(data - 16)..], count);
            current.Span.Slice(from - 8, 8).CopyTo(span[(data - 8)..]);
            current.Span.Slice(from, count * stride).CopyTo(span[data..]);
            return copies[from] = data;
        }
        void Set(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(CollectionsMarshal.AsSpan(output)[at..], value);
    }

    private static void Header(byte[] bytes, int header, int count, uint type)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header - 4), ArrayMarker);
        I64(bytes, header, count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 8), type);
    }

    private static int Align(int value) => (value + 15) & ~15;
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static void I64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
}
