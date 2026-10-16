using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** A row of an index table: row index, definition hash and tag. */
public sealed record InvestmentIndexRow(int Index, uint Hash, uint Tag);
/** A row of a string-bank index: bank name hash and container tag. */
public sealed record InvestmentBankRow(int Index, uint NameHash, uint ContainerTag);

/** Rows to append to the typed array whose descriptor is at Field. */
public sealed record RowAppend(int Field, int Stride, IReadOnlyList<byte[]> Rows)
{
    /** Row offsets of the array descriptors each row owns; moved rows keep their targets. */
    public IReadOnlyList<int> RowArrays { get; init; } = [];
    /** Per new row, the offset of the row it was copied from, or -1 when it owns no array. */
    public IReadOnlyList<int>? RowSources { get; init; }
}

/** Byte steps that give a cloned item its own index rows, strings and icon. */
public static class InvestmentItemClone
{
    // Index rows: definition hash at +0, target tag at +16; bytes 4-15 and 20-23 are not decoded.
    public const int IndexStride = 24, IndexHash = 0, IndexTag = 16;
    // Bank index rows: name hash at +0, container tag at +4.
    public const int BankStride = 8;
    // Display record: icon index u16 at +128; {u32 bank, u32 hash} pairs for name, type and flavor.
    public const int DisplayIcon = 128, DisplayName = 132, DisplayType = 144, DisplayFlavor = 152, DisplayLength = 160;
    /** The client's index lookup sign-extends a 16-bit index, so rows at 0x8000 and above are unreachable. */
    public const int MaximumIndexRows = 0x8000;
    public const uint EmptyStringHash = 0x811C9DC5;
    private const int TableArray = 8;
    private const uint ArrayMarker = 0x80809FBD;

    /** Reads the rows of an index table. */
    public static IReadOnlyList<InvestmentIndexRow> ReadIndexRows(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(TableArray, stride: IndexStride);
        return Enumerable.Range(0, rows.Count).Select(index =>
        {
            int row = rows.Data + index * IndexStride;
            return new InvestmentIndexRow(index, reader.U32(row + IndexHash), reader.U32(row + IndexTag));
        }).ToArray();
    }

    /** Reads the rows of a string-bank index table. */
    public static IReadOnlyList<InvestmentBankRow> ReadBankRows(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(TableArray, stride: BankStride);
        return Enumerable.Range(0, rows.Count).Select(index =>
            new InvestmentBankRow(index, reader.U32(rows.Data + index * BankStride), reader.U32(rows.Data + index * BankStride + 4)))
            .ToArray();
    }

    /** Null when every row's undecoded bytes are zero, else the first row that differs. */
    public static string? UndecodedIndexBytes(ReadOnlyMemory<byte> table)
    {
        var reader = new TagReader(table);
        var rows = reader.Array(TableArray, stride: IndexStride);
        var span = table.Span;
        for (int index = 0; index < rows.Count; index++)
        {
            var row = span.Slice(rows.Data + index * IndexStride, IndexStride);
            if (row[4..IndexTag].ContainsAnyExcept((byte)0) || row[(IndexTag + 4)..].ContainsAnyExcept((byte)0))
                return $"Index row {index} has non-zero bytes outside its hash and tag. Their meaning is not decoded.";
        }
        return null;
    }

    /**
     * Appends an index row whose undecoded bytes are zero, like every installed row.
     * @return The table and the new row's index.
     */
    public static (byte[] Table, int Index) AppendIndexRow(ReadOnlyMemory<byte> table, uint hash, uint tag)
    {
        if (UndecodedIndexBytes(table) is { } problem) throw new InvalidDataException(problem);
        int index = new TagReader(table).Array(TableArray, stride: IndexStride).Count;
        if (index + 1 > MaximumIndexRows) throw new InvalidDataException("The index table has no reachable row left.");
        byte[] row = new byte[IndexStride];
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(IndexHash), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(IndexTag), tag);
        return (AppendRow(table, TableArray, IndexStride, row), index);
    }

    /** Appends a bank row; returns the new table and the new row's index. */
    public static (byte[] Table, int Index) AppendBankRow(ReadOnlyMemory<byte> table, uint nameHash, uint containerTag)
    {
        int index = new TagReader(table).Array(TableArray, stride: BankStride).Count;
        byte[] row = new byte[BankStride];
        BinaryPrimitives.WriteUInt32LittleEndian(row, nameHash);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), containerTag);
        return (AppendRow(table, TableArray, BankStride, row), index);
    }

    /**
     * Adds one row to a typed array without moving any other field.
     * @param field Offset of the array descriptor.
     * @param row Row bytes; its length is the stride.
     * @param rowArrays Row offsets of the array descriptors each row owns; moved rows keep their targets.
     * @param rowSource Offset of the row `row` was copied from, whose array offsets it still holds.
     * @return Rows at the blob end grow in place; other arrays move to a new block at the end.
     */
    public static byte[] AppendRow(ReadOnlyMemory<byte> table, int field, int stride, ReadOnlySpan<byte> row,
        IReadOnlyList<int>? rowArrays = null, int rowSource = -1) =>
        AppendRows(table, [new(field, stride, [row.ToArray()]) { RowArrays = rowArrays ?? [], RowSources = [rowSource] }]).Table;

    /**
     * Appends rows to typed arrays; each array moves at most once and installed bytes never shift.
     * @param appends Arrays to grow; one with no rows moves only when rewritten in place.
     * @param blocks New bytes placed 16-byte aligned before the rewritten arrays.
     * @param stockLength Installed table length; past it, an array that ends the table grows in place.
     * @return The table and the start offset of each block, in order.
     */
    public static (byte[] Table, int[] Blocks) AppendRows(ReadOnlyMemory<byte> table, IReadOnlyList<RowAppend> appends,
        IReadOnlyList<byte[]>? blocks = null, int stockLength = int.MaxValue)
    {
        blocks ??= [];
        if (appends.Select(append => append.Field).Distinct().Count() != appends.Count)
            throw new ArgumentException("Each array may appear once.", nameof(appends));
        var reader = new TagReader(table);
        var arrays = appends.Select((append, index) => Grown.Read(reader, append) with { Index = index }).ToArray();
        // The tail: arrays of this call that end the table back to back, laid out as below.
        var tail = new List<int>();
        for (int boundary = table.Length, found = 0; found >= 0;)
        {
            found = Array.FindIndex(arrays, array => !tail.Contains(array.Index) && (tail.Count == 0
                ? array.End == boundary : NextStart(array.End) == boundary && Zero(table.Span[array.End..boundary])));
            if (found < 0) break;
            var next = arrays[found];
            if (next.Start < stockLength)
            {
                // An installed array keeps its header and grows over Studio's bytes only.
                if (blocks.Count == 0 && (tail.Count == 0 || next.End >= stockLength)) tail.Insert(0, found);
                break;
            }
            tail.Insert(0, found);
            boundary = next.Start;
        }
        while (tail.Count > 0 && appends[tail[0]].Rows.Count == 0) tail.RemoveAt(0);
        var order = tail.Concat(arrays.Where(array => appends[array.Index].Rows.Count > 0 && !tail.Contains(array.Index))
            .Select(array => array.Index)).ToArray();
        if (order.Length == 0 && blocks.Count == 0) return (table.ToArray(), []);
        int cut = tail.Count > 0 ? arrays[tail[0]].Start : table.Length;
        if (appends.Any(append => append.Field + 16 > cut))
            throw new NotSupportedException("An array descriptor lies inside the rewritten end of the table.");

        int position = cut;
        int[] blockStarts = new int[blocks.Count];
        for (int index = 0; index < blocks.Count; index++)
        {
            blockStarts[index] = checked((position + 15) & ~15);
            position = checked(blockStarts[index] + blocks[index].Length);
        }
        int[] headers = new int[order.Length];
        for (int index = 0; index < order.Length; index++)
        {
            var array = arrays[order[index]];
            // The first rewritten array keeps its header so a lone array at the end grows in place.
            headers[index] = index == 0 && tail.Count > 0 && blocks.Count == 0 ? array.Header : checked((position + 19) & ~15);
            position = checked(headers[index] + 16 + array.NewCount * appends[order[index]].Stride);
        }
        byte[] output = new byte[position];
        table.Span[..cut].CopyTo(output);
        for (int index = 0; index < blocks.Count; index++) blocks[index].CopyTo(output, blockStarts[index]);
        for (int index = 0; index < order.Length; index++)
        {
            var array = arrays[order[index]];
            var append = appends[order[index]];
            int header = headers[index], data = header + 16, stride = append.Stride;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(header - 4), ArrayMarker);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(header), array.NewCount);
            table.Span.Slice(array.Header + 8, 8).CopyTo(output.AsSpan(header + 8));
            table.Span.Slice(array.Data, array.Count * stride).CopyTo(output.AsSpan(data));
            for (int row = 0; row < array.Count; row++)
                Rebase(output, table.Span, cut, append.RowArrays, array.Data + row * stride, data + row * stride);
            for (int row = 0; row < append.Rows.Count; row++)
            {
                int at = data + (array.Count + row) * stride;
                append.Rows[row].CopyTo(output, at);
                int source = append.RowSources?[row] ?? -1;
                if (source >= 0) Rebase(output, table.Span, cut, append.RowArrays, source, at);
            }
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(append.Field), array.NewCount);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(append.Field + 8), header - append.Field - 8);
        }
        if (reader.Has(0, 8) && reader.U64(0) == (ulong)table.Length)
            BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return (output, blockStarts);

        static int NextStart(int end) => checked(((end + 19) & ~15) - 4);
        static bool Zero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);
    }

    /**
     * A table rebuild may start from the installed bytes only when the edit left the rest alone.
     * @param kept Installed ranges the edit may change, as start and length.
     * @param problem The error text when a byte outside them changed.
     */
    public static void RequireOnlyChanged(ReadOnlySpan<byte> original, ReadOnlySpan<byte> current,
        IEnumerable<(int Start, int Length)> kept, string problem)
    {
        if (current.Length < original.Length) throw new InvalidDataException(problem);
        var open = new bool[original.Length];
        foreach (var (start, length) in kept) open.AsSpan(start, length).Fill(true);
        for (int offset = 0; offset < original.Length; offset++)
            if (!open[offset] && original[offset] != current[offset]) throw new InvalidDataException(problem);
    }

    /** @return The installed ranges a table's size word, descriptor and typed array span. */
    public static IEnumerable<(int Start, int Length)> ArraySpans(ReadOnlyMemory<byte> original, int field, int stride)
    {
        var reader = new TagReader(original);
        if (reader.Has(0, 8) && reader.U64(0) == (ulong)original.Length) yield return (0, 8);
        yield return (field, 16);
        var rows = reader.Array(field, stride: stride);
        if (rows.Count > 0) yield return (rows.Data - 20, 20 + rows.Count * stride);
    }

    /** A TypedBlock's array header offset; the marker is the 4 bytes before it. */
    public const int TypedBlockHeader = 16;

    /** An AppendRows block holding one typed array: count, 8 class bytes, then rows. */
    public static byte[] TypedBlock(long count, ReadOnlySpan<byte> classData, ReadOnlySpan<byte> rows)
    {
        if (classData.Length != 8) throw new ArgumentException("Array class data is 8 bytes.", nameof(classData));
        byte[] block = new byte[TypedBlockHeader + 16 + rows.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(TypedBlockHeader - 4), ArrayMarker);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(TypedBlockHeader), count);
        classData.CopyTo(block.AsSpan(TypedBlockHeader + 8));
        rows.CopyTo(block.AsSpan(TypedBlockHeader + 16));
        return block;
    }

    /** One array of an AppendRows call, read and checked before any byte is written. */
    private sealed record Grown(int Index, int Count, int Data, int Header, int Start, int End, int NewCount)
    {
        /** Validates an append request and reads the target array; throws on stride, source-row or array-ownership mismatch. */
        public static Grown Read(TagReader reader, RowAppend append)
        {
            if (append.Stride <= 0 || append.Rows.Any(row => row.Length != append.Stride))
                throw new ArgumentException("Each row must be one stride long.", nameof(append));
            if (append.RowSources is { } sources && sources.Count != append.Rows.Count)
                throw new ArgumentException("Each new row needs a source entry.", nameof(append));
            for (int row = 0; row < append.Rows.Count; row++)
                if ((append.RowSources?[row] ?? -1) < 0 && append.RowArrays.Any(offset =>
                    BinaryPrimitives.ReadInt64LittleEndian(append.Rows[row].AsSpan(offset + 8)) != 0))
                    throw new ArgumentException("A row that owns arrays needs the row it was copied from.", nameof(append));
            var rows = reader.Array(append.Field, stride: append.Stride);
            if (rows.Count == 0) throw new InvalidDataException("An empty array carries no row type to extend.");
            int count = checked(rows.Count + append.Rows.Count);
            if (count > TagReader.MaximumArrayCount)
                throw new InvalidDataException("The table has reached the editor's row limit.");
            return new(-1, rows.Count, rows.Data, rows.Data - 16, rows.Data - 20,
                checked(rows.Data + rows.Count * append.Stride), count);
        }
    }

    /**
     * A row copied from `from` to `to` keeps each array target; an offset of 0 means no array.
     * A target inside the rewritten end of the table would be overwritten, so it is refused.
     */
    private static void Rebase(byte[] output, ReadOnlySpan<byte> table, int cut, IReadOnlyList<int> rowArrays, int from, int to)
    {
        foreach (int offset in rowArrays)
        {
            int at = to + offset + 8;
            long relative = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(at));
            if (relative == 0) continue;
            long target = checked(from + offset + 8 + relative);
            if (target >= cut && target < table.Length)
                throw new NotSupportedException("A moved row owns an array inside the rewritten end of the table.");
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), target - at);
        }
    }

    /** Reads the (bank, hash) text reference stored at offset in a display. */
    public static (uint Bank, uint Hash) ReadDisplayString(ReadOnlySpan<byte> display, int offset)
    {
        RequireDisplay(display, offset);
        return (BinaryPrimitives.ReadUInt32LittleEndian(display[offset..]), BinaryPrimitives.ReadUInt32LittleEndian(display[(offset + 4)..]));
    }

    /** A display string is a {u32 bank index, u32 string hash} pair. */
    public static byte[] SetDisplayString(ReadOnlySpan<byte> display, int offset, uint bank, uint hash)
    {
        RequireDisplay(display, offset);
        if (offset is not (DisplayName or DisplayType or DisplayFlavor)) throw new ArgumentOutOfRangeException(nameof(offset));
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), bank);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 4), hash);
        return output;
    }

    /** Reads a display's icon index. */
    public static ushort ReadDisplayIcon(ReadOnlySpan<byte> display)
    {
        RequireDisplay(display, DisplayIcon);
        return BinaryPrimitives.ReadUInt16LittleEndian(display[DisplayIcon..]);
    }

    /** The icon index selects a shared icon row; 0xFFFF means no icon. */
    public static byte[] SetDisplayIcon(ReadOnlySpan<byte> display, ushort icon)
    {
        RequireDisplay(display, DisplayIcon);
        byte[] output = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(DisplayIcon), icon);
        return output;
    }

    /** FNV-1 over UTF-8, the engine's string hash. */
    public static uint Fnv1(string text) => SunriseStudio.Packages.StringBankWriter.Hash(text);

    /** A definition hash no installed or added row uses; 0, 0xFFFFFFFF and the empty marker are skipped. */
    public static uint NewDefinitionHash(string key, IReadOnlySet<uint> used)
    {
        for (int attempt = 0; attempt < 4096; attempt++)
        {
            uint hash = Fnv1(attempt == 0 ? key : $"{key}#{attempt}");
            if (hash is not (0 or uint.MaxValue or EmptyStringHash) && !used.Contains(hash)) return hash;
        }
        throw new InvalidDataException("No unused definition hash was found.");
    }

    /** A name hash for a new bank row; a table already sorted by name hash stays sorted. */
    public static uint NewBankNameHash(string key, IReadOnlyList<InvestmentBankRow> rows)
    {
        bool sorted = true;
        for (int i = 1; i < rows.Count && sorted; i++) sorted = rows[i - 1].NameHash < rows[i].NameHash;
        var used = rows.Select(row => row.NameHash).ToHashSet();
        uint last = rows.Count == 0 ? 0 : rows[^1].NameHash;
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            uint hash = Fnv1(attempt == 0 ? key : $"{key}#{attempt}");
            if (hash is 0 or uint.MaxValue or EmptyStringHash || used.Contains(hash)) continue;
            if (!sorted || hash > last) return hash;
        }
        throw new InvalidDataException("No bank name hash keeps the bank index order.");
    }

    // Every installed item definition repeats its own hash here.
    public const int DefinitionHashField = 0xA0;

    /** A copied definition must name its own hash, not the base item's. */
    public static byte[] SetDefinitionHash(ReadOnlySpan<byte> definition, uint hash)
    {
        if (definition.Length < DefinitionHashField + 4) throw new InvalidDataException("The item definition is truncated.");
        byte[] result = definition.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(DefinitionHashField), hash);
        return result;
    }

    /** Offsets of 4-byte-aligned words equal to a value, such as an item's own hash. */
    public static IReadOnlyList<int> FindAlignedValue(ReadOnlySpan<byte> bytes, uint value)
    {
        var offsets = new List<int>();
        for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) == value) offsets.Add(offset);
        return offsets;
    }

    private static void RequireDisplay(ReadOnlySpan<byte> display, int offset)
    {
        if (display.Length < DisplayLength || offset < 0 || offset + 8 > display.Length)
            throw new InvalidDataException("The item display record is truncated.");
    }
}
