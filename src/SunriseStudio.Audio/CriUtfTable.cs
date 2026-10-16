using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Audio;

/**
 * A CRI @UTF table (the header tables inside USM chunks), read-only. Written from the public description of the
 * format; all fields are big-endian and offsets count from the byte after the 8-byte "@UTF" + size prefix.
 * Header: u16 version, u16 rows offset, u32 strings offset, u32 data offset, u32 table name, u16 columns,
 * u16 row length, u32 rows. Column: u8 flags (high nibble 1 none / 3 constant / 5 per row, low nibble type),
 * u32 name; a constant's value follows its column.
 */
public sealed class CriUtfTable
{
    public string Name { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; }

    private CriUtfTable(string name, List<IReadOnlyDictionary<string, object?>> rows) { Name = name; Rows = rows; }

    public static bool IsTable(ReadOnlySpan<byte> bytes) => bytes.Length >= 32 && bytes[..4].SequenceEqual("@UTF"u8);

    public static CriUtfTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (!IsTable(bytes)) throw new InvalidDataException("Not a CRI @UTF table.");
        int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]));
        if (size < 24 || size > bytes.Length - 8) throw new InvalidDataException("@UTF table size leaves its chunk.");
        // A copy: tables are small, and the local functions below cannot capture a span.
        byte[] table = bytes.Slice(8, size).ToArray();
        int rowsOffset = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(2));
        int strings = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4)));
        int data = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(8)));
        int nameOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(12)));
        int columnCount = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(16));
        int rowLength = BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(18));
        int rowCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(20)));
        if (strings > size || data > size || rowCount > 1_000_000 || (long)rowsOffset + (long)rowCount * rowLength > size)
            throw new InvalidDataException("@UTF table offsets leave the table.");

        string Text(int offset)
        {
            int start = checked(strings + offset);
            if (offset < 0 || start >= size) return "";
            int end = System.Array.IndexOf(table, (byte)0, start);
            return Encoding.UTF8.GetString(table, start, (end < 0 ? size : end) - start);
        }

        var columns = new List<(string Name, int Storage, int Type, int ConstantAt)>();
        int at = 24;
        for (int c = 0; c < columnCount; c++)
        {
            if (at + 5 > size) throw new InvalidDataException("@UTF column list is truncated.");
            int flags = table[at];
            string name = Text(checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at + 1))));
            at += 5;
            int storage = flags >> 4, type = flags & 0xF;
            int constantAt = -1;
            if (storage == 3) { constantAt = at; at += Width(type); }
            columns.Add((name, storage, type, constantAt));
        }

        object? Value(int type, int offset)
        {
            if (offset + Width(type) > size) throw new InvalidDataException("@UTF value leaves the table.");
            var v = table.AsSpan(offset);
            return type switch
            {
                0 => v[0], 1 => (sbyte)v[0],
                2 => BinaryPrimitives.ReadUInt16BigEndian(v), 3 => BinaryPrimitives.ReadInt16BigEndian(v),
                4 => BinaryPrimitives.ReadUInt32BigEndian(v), 5 => BinaryPrimitives.ReadInt32BigEndian(v),
                6 => BinaryPrimitives.ReadUInt64BigEndian(v), 7 => BinaryPrimitives.ReadInt64BigEndian(v),
                8 => BinaryPrimitives.ReadSingleBigEndian(v), 9 => BinaryPrimitives.ReadDoubleBigEndian(v),
                0xA => Text(checked((int)BinaryPrimitives.ReadUInt32BigEndian(v))),
                0xB => (Offset: (long)data + BinaryPrimitives.ReadUInt32BigEndian(v), Length: (long)BinaryPrimitives.ReadUInt32BigEndian(v[4..])),
                _ => throw new InvalidDataException($"@UTF column type {type} is unknown.")
            };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(rowCount);
        for (int r = 0; r < rowCount; r++)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            int cursor = rowsOffset + r * rowLength;
            foreach (var column in columns)
            {
                switch (column.Storage)
                {
                    case 3: row[column.Name] = Value(column.Type, column.ConstantAt); break;
                    case 5: row[column.Name] = Value(column.Type, cursor); cursor += Width(column.Type); break;
                    default: row[column.Name] = null; break;
                }
            }
            rows.Add(row);
        }
        return new CriUtfTable(Text(nameOffset), rows);
    }

    /** The first row's value as a long, or the fallback when absent or not numeric. */
    public long Long(string column, long fallback = 0, int row = 0) =>
        row < Rows.Count && Rows[row].TryGetValue(column, out var value) ? value switch
        {
            byte b => b, sbyte s => s, ushort u => u, short h => h, uint i => i, int j => j, ulong l => (long)l, long k => k,
            float f => (long)f, double d => (long)d, _ => fallback
        } : fallback;

    public string? String(string column, int row = 0) =>
        row < Rows.Count && Rows[row].TryGetValue(column, out var value) ? value as string : null;

    private static int Width(int type) => type switch
    {
        0 or 1 => 1, 2 or 3 => 2, 4 or 5 or 8 or 0xA => 4, 6 or 7 or 9 or 0xB => 8,
        _ => throw new InvalidDataException($"@UTF column type {type} is unknown.")
    };
}
