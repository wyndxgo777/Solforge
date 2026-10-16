using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Compact replacements preserve source AO offsets, range flags and every admitted header lane. */
internal static class InstanceIsolationWriter
{
    /** Compacted placement table bytes plus the origin of each kept row. */
    internal sealed record Table(byte[] Bytes, IReadOnlyDictionary<int, PackageInstanceOrigin> Origins);

    /** Rewrites a placement table to keep only the selected indices, with matching bounds boxes. */
    internal static Table Compact(uint sourceTag, byte[] source, byte[] bounds, int[] indices, uint boundsTag)
    {
        var data = new NativeRows(source);
        int[] rows = data.Rows(0x40, 0x808071A3, 48), resources = data.Rows(0x58, 0x8080967D, 4),
            ranges = data.Rows(0x68, 0x80807190, 8), boxes = new NativeRows(bounds).Rows(8, 0x80809673, 48);
        if (data.I64(0) != source.Length || data.I64(0x30) != 112 || data.I64(0x38) != rows.Length
            || data.I64(8) != 0 || data.I64(0x10) != 0 || data.I64(0x20) != 0 || data.I64(0x28) != 0
            || boxes.Length != rows.Length || indices.Length is 0 or > ushort.MaxValue)
            throw new InvalidDataException("The static table has no supported compact profile.");
        var selected = indices.Select((index, compact) => (index, compact)).ToDictionary(x => x.index, x => x.compact);
        if (indices.Any(index => index < 0 || index >= rows.Length))
            throw new InvalidDataException("The isolated instance index is invalid.");
        var resourceIndices = new Dictionary<int, int>();
        var compactRanges = new List<byte[]>();
        var covered = new HashSet<int>();
        foreach (int range in ranges)
        {
            int count = data.U16(range), first = data.U16(range + 2), resource = data.U16(range + 4);
            if (resource >= resources.Length || first + count > rows.Length)
                throw new InvalidDataException("The static range leaves its source arrays.");
            for (int index = first; index < first + count; index++)
            {
                if (!selected.TryGetValue(index, out int compact)) continue;
                if (!resourceIndices.TryGetValue(resource, out int mapped))
                    resourceIndices.Add(resource, mapped = resourceIndices.Count);
                if (mapped > ushort.MaxValue) throw new InvalidDataException("The compact resource index is too large.");
                byte[] next = data.Span(range, 8).ToArray();
                U16(next, 0, 1); U16(next, 2, checked((ushort)compact)); U16(next, 4, (ushort)mapped);
                compactRanges.Add(next); covered.Add(index);
            }
        }
        if (covered.Count != indices.Length) throw new InvalidDataException("An isolated instance has no draw range.");
        byte[] result = data.Span(0, 160).ToArray();
        U32(result, 0x18, boundsTag); I64(result, 0x38, indices.Length);
        result = Array(result, 0x40, 0x808071A3, indices.Select(index => data.Span(rows[index], 48).ToArray()).ToArray());
        result = Array(result, 0x58, 0x8080967D, resourceIndices.OrderBy(x => x.Value)
            .Select(x => data.Span(resources[x.Key], 4).ToArray()).ToArray());
        result = Array(result, 0x68, 0x80807190, compactRanges);
        var boxData = new NativeRows(bounds);
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (int index in indices)
        {
            Vector3 low = boxData.V3(boxes[index]), high = boxData.V3(boxes[index] + 16);
            if (!Finite(low) || !Finite(high) || low.X > high.X || low.Y > high.Y || low.Z > high.Z)
                throw new InvalidDataException("An isolated instance has invalid bounds.");
            min = Vector3.Min(min, low); max = Vector3.Max(max, high);
        }
        NativeRows.Write(result, 0x80, min.X, min.Y, min.Z, 1);
        NativeRows.Write(result, 0x90, max.X, max.Y, max.Z, 1);
        return new(result, selected.ToDictionary(x => x.Value, x => new PackageInstanceOrigin(sourceTag, x.Key)));
    }

    /** Empty coarse bounds use the native aggregate-only profile; table rows still drive fine culling. */
    internal static byte[] EmptyBounds()
    {
        byte[] result = new byte[24]; I64(result, 0, result.Length); return result;
    }

    /** Copy of a static owner record pointed at a new table. */
    internal static byte[] Owner(byte[] source, uint table)
    {
        if (source.Length != 40) throw new InvalidDataException("The static owner size is unsupported.");
        byte[] result = source.ToArray(); U32(result, 8, table); I64(result, 0x18, -1);
        result[0x20] = 1; result[0x21] = 1; return result;
    }

    /** Source placement rows stay at their offsets; only the new list receives copied constructor data. */
    internal static (byte[] Bytes, Dictionary<int, PackagePlacementOrigin> Origins) List(
        uint sourceTag, byte[] source, IReadOnlyList<(int Row, int Index, int Auxiliary, uint Owner)> placements)
    {
        var data = new NativeRows(source);
        byte[] result = data.Span(0, 24).ToArray();
        result = Array(result, 8, 0x808099D8, placements.Select(x => data.Span(x.Row, 144).ToArray()).ToArray());
        int[] rows = new NativeRows(result).Rows(8, 0x808099D8, 144);
        var origins = new Dictionary<int, PackagePlacementOrigin>();
        for (int index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            if (data.I64(placement.Row + 0x80) != 0 || data.I64(placement.Auxiliary) != 0
                || data.U32(placement.Auxiliary - 4) != 0x808071B3)
                throw new InvalidDataException("The static placement has unsupported attachment data.");
            int prefix = Align(result.Length, 8), auxiliary = prefix + 8;
            System.Array.Resize(ref result, checked(prefix + 32));
            data.Span(placement.Auxiliary - 8, 32).CopyTo(result.AsSpan(prefix));
            I64(result, rows[index] + 0x78, auxiliary - (rows[index] + 0x78));
            U32(result, auxiliary + 0x10, placement.Owner);
            origins.Add(rows[index], new(sourceTag, placement.Row, placement.Index));
        }
        I64(result, 0, result.Length); return (result, origins);
    }

    /** Appending a typed array leaves every unrelated relative pointer at its original address. */
    internal static byte[] Array(byte[] source, int field, uint type, IReadOnlyList<byte[]> rows)
    {
        int header = Align(checked(source.Length + 4), 16);
        int length = checked(header + 16 + rows.Sum(row => row.Length));
        byte[] result = new byte[length]; source.CopyTo(result, 0);
        I64(result, field, rows.Count); I64(result, field + 8, header - (field + 8));
        U32(result, header - 4, 0x80809FBD); I64(result, header, rows.Count); U32(result, header + 8, type);
        int at = header + 16;
        foreach (byte[] row in rows) { row.CopyTo(result, at); at += row.Length; }
        I64(result, 0, result.Length); return result;
    }

    /** Little-endian bytes of a tag. */
    internal static byte[] Tag(uint value) { byte[] bytes = new byte[4]; U32(bytes, 0, value); return bytes; }
    /** Writes a little-endian uint at an offset. */
    internal static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    /** Writes a little-endian long at an offset. */
    internal static void I64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
