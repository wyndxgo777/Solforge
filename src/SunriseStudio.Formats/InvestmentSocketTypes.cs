using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Socket types: parallel gameplay and display tables, both indexed by the socket's type number. */
public static class InvestmentSocketTypes
{
    /** Root blob slot 94; 96-byte rows with the insertion rules and plug whitelist. */
    public const uint GameplayTable = 0x8131932F;
    /** investment_globals slot 58; 40-byte rows with the same hash at +0. */
    public const uint DisplayTable = 0x81613D06;
    private const int RowsField = 8, GameplayStride = 96, DisplayStride = 40;
    // Row +40 is the plug whitelist: 8-byte rows led by a plug category hash.
    private const int WhitelistField = 40, WhitelistStride = 8;
    // Gameplay rows own arrays at these offsets.
    private static readonly int[] RowArrays = [16, 40, 56, 80];

    /** Number of socket type rows. */
    public static int Count(ReadOnlyMemory<byte> gameplay) => new TagReader(gameplay).Array(RowsField, stride: GameplayStride).Count;

    /** Plug category hashes a socket type accepts. */
    public static IReadOnlyList<uint> Whitelist(ReadOnlyMemory<byte> gameplay, int index)
    {
        var reader = new TagReader(gameplay);
        var rows = reader.Array(RowsField, stride: GameplayStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The socket type is outside the table.");
        var list = reader.Array(rows.Data + index * GameplayStride + WhitelistField, stride: WhitelistStride);
        return Enumerable.Range(0, list.Count).Select(row => reader.U32(list.Data + row * WhitelistStride)).ToArray();
    }

    /** Every plug category any socket type accepts. */
    public static IReadOnlySet<uint> Categories(ReadOnlyMemory<byte> gameplay) =>
        Enumerable.Range(0, Count(gameplay)).SelectMany(index => Whitelist(gameplay, index)).ToHashSet();

    /** Hashes of every socket type. */
    public static IReadOnlySet<uint> Hashes(ReadOnlyMemory<byte> gameplay)
    {
        var reader = new TagReader(gameplay);
        var rows = reader.Array(RowsField, stride: GameplayStride);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * GameplayStride)).ToHashSet();
    }

    /**
     * Appends a copy of one socket type to both tables under a new hash, with its own plug whitelist.
     * The copy keeps the source's insertion rule and unlock expression.
     * @param stockLength Installed gameplay table length, so moved rows leave no dead copy.
     * @return The grown tables and the new type number, which is the same in both.
     */
    public static (byte[] Gameplay, byte[] Display, int Index) Append(ReadOnlyMemory<byte> gameplay,
        ReadOnlyMemory<byte> display, int copyFrom, uint hash, IReadOnlyList<uint> whitelist,
        int stockLength)
    {
        var reader = new TagReader(gameplay);
        var rows = reader.Array(RowsField, stride: GameplayStride);
        var displayRows = new TagReader(display).Array(RowsField, stride: DisplayStride);
        if (rows.Count != displayRows.Count) throw new InvalidDataException("The socket type tables differ in length.");
        if (copyFrom < 0 || copyFrom >= rows.Count) throw new InvalidDataException("The socket type to copy is outside the table.");
        if (rows.Count >= short.MaxValue) throw new InvalidDataException("The socket type table has no index left.");
        if (Hashes(gameplay).Contains(hash)) throw new InvalidDataException("The socket type hash is already used.");
        if (whitelist.Count == 0) throw new InvalidDataException("A socket type needs at least one plug category.");
        int source = rows.Data + copyFrom * GameplayStride;
        var sourceList = reader.Array(source + WhitelistField, stride: WhitelistStride);
        if (sourceList.Count == 0) throw new InvalidDataException("The source socket type has no whitelist row to copy.");
        byte[] row = gameplay.Span.Slice(source, GameplayStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(row, hash);
        byte[] entries = new byte[whitelist.Count * WhitelistStride];
        for (int i = 0; i < whitelist.Count; i++)
        {
            gameplay.Span.Slice(sourceList.Data, WhitelistStride).CopyTo(entries.AsSpan(i * WhitelistStride));
            BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(i * WhitelistStride), whitelist[i]);
        }
        // Block before rows: the rows stay last, so the next append reuses their bytes and leaves no dead copy.
        var (output, blocks) = InvestmentItemClone.AppendRows(gameplay,
            [new(RowsField, GameplayStride, [row]) { RowArrays = RowArrays, RowSources = [source] }],
            [InvestmentItemClone.TypedBlock(whitelist.Count, gameplay.Span.Slice(sourceList.Data - 8, 8), entries)], stockLength);
        int index = rows.Count;
        int descriptor = new TagReader(output).Array(RowsField, stride: GameplayStride).Data
            + index * GameplayStride + WhitelistField;
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(descriptor), whitelist.Count);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(descriptor + 8),
            blocks[0] + InvestmentItemClone.TypedBlockHeader - descriptor - 8);

        int displaySource = displayRows.Data + copyFrom * DisplayStride;
        byte[] displayRow = display.Span.Slice(displaySource, DisplayStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(displayRow, hash);
        byte[] displayOutput = InvestmentItemClone.AppendRow(display, RowsField, DisplayStride, displayRow);
        if (!Whitelist(output, index).SequenceEqual(whitelist) || !Whitelist(output, copyFrom).SequenceEqual(Whitelist(gameplay, copyFrom)))
            throw new InvalidDataException("The new socket type's whitelist did not read back.");
        return (output, displayOutput, index);
    }

    /**
     * The installed gameplay table plus one copy of the rows and of each added whitelist.
     * Type numbers stay; old copies a saved project still carries are dropped.
     * @param original The installed table.
     * @param current The edited table; Studio only appends socket types.
     */
    public static byte[] Rebuild(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> current)
    {
        var stock = new TagReader(original);
        var edited = new TagReader(current);
        var before = stock.Array(RowsField, stride: GameplayStride);
        var after = edited.Array(RowsField, stride: GameplayStride);
        if (after.Count < before.Count) throw new InvalidDataException("The socket type table lost rows.");
        InvestmentItemClone.RequireOnlyChanged(original.Span, current.Span,
            InvestmentItemClone.ArraySpans(original, RowsField, GameplayStride), "The socket type table changed outside its rows.");
        for (int row = 0; row < before.Count; row++)
            if (!SameRow(original.Span, before.Data + row * GameplayStride, current.Span, after.Data + row * GameplayStride))
                throw new InvalidDataException("An installed socket type changed; Studio only appends socket types.");
        if (after.Count == before.Count) return original.ToArray();
        var rows = new List<byte[]>();
        var blocks = new List<byte[]>();
        var links = new List<(int Row, int Field, int Target, bool Block)>();
        (int Start, int End) installedRows = (before.Data - 20, before.Data + before.Count * GameplayStride);
        for (int row = before.Count; row < after.Count; row++)
        {
            int at = after.Data + row * GameplayStride;
            byte[] bytes = current.Span.Slice(at, GameplayStride).ToArray();
            foreach (int field in RowArrays)
            {
                long relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));
                if (relative == 0) continue;
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(field + 8), 0);
                int header = checked((int)(at + field + 8 + relative));
                if (header < original.Length && (header < installedRows.Start || header >= installedRows.End))
                { links.Add((rows.Count, field, header, false)); continue; }
                // Studio gives an added type only its own whitelist; other arrays stay the source's.
                if (field != WhitelistField || header < original.Length)
                    throw new NotSupportedException("An added socket type owns an array Studio did not write.");
                var list = edited.Array(at + field, stride: WhitelistStride);
                links.Add((rows.Count, field, blocks.Count, true));
                blocks.Add(InvestmentItemClone.TypedBlock(list.Count, current.Span.Slice(list.Data - 8, 8),
                    current.Span.Slice(list.Data, list.Count * WhitelistStride)));
            }
            rows.Add(bytes);
        }
        var (output, starts) = InvestmentItemClone.AppendRows(original,
            [new(RowsField, GameplayStride, rows) { RowArrays = RowArrays, RowSources = rows.Select(_ => -1).ToArray() }],
            blocks, original.Length);
        int data = new TagReader(output).Array(RowsField, stride: GameplayStride).Data;
        foreach (var (row, field, target, block) in links)
        {
            int descriptor = data + (before.Count + row) * GameplayStride + field;
            int header = block ? starts[target] + InvestmentItemClone.TypedBlockHeader : target;
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(descriptor + 8), header - descriptor - 8);
        }
        return output;
    }

    /** Two rows match when their bytes match and each owned array names the same header. */
    private static bool SameRow(ReadOnlySpan<byte> first, int a, ReadOnlySpan<byte> second, int b)
    {
        for (int offset = 0; offset < GameplayStride; offset++)
        {
            int field = Array.FindIndex(RowArrays, start => offset >= start + 8 && offset < start + 16);
            if (field >= 0) { offset = RowArrays[field] + 15; continue; }
            if (first[a + offset] != second[b + offset]) return false;
        }
        foreach (int field in RowArrays)
        {
            long x = BinaryPrimitives.ReadInt64LittleEndian(first[(a + field + 8)..]);
            long y = BinaryPrimitives.ReadInt64LittleEndian(second[(b + field + 8)..]);
            if ((x == 0) != (y == 0) || x != 0 && a + x != b + y) return false;
        }
        return true;
    }
}
