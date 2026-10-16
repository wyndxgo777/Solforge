using System.Buffers.Binary;
using SunriseStudio.Formats;

/** Checks item art rows: cloning grows the table, keeps art fields and parent, and points the clone at its own icon. */
internal static class InvestmentItemArtChecks
{
    /** Cloned rows may share fields until an icon assignment makes a private copy. */
    public static void Run()
    {
        byte[] original = Fixture(4);
        byte[] grown = InvestmentItemArt.CloneRow(original, 2, 4);
        Check(InvestmentItemArt.Count(grown) == 5, "Item art row count did not grow.");
        for (int index = 0; index < 4; index++)
            Check(Row(original, index).SequenceEqual(Row(grown, index)), "An existing item art row changed.");
        Check(Row(original, 2).SequenceEqual(Row(grown, 4)), "The clone lost its art fields or parent.");
        byte[] edited = InvestmentItemArt.SetPrimaryIcon(grown, 4, 0x81D62071);
        Check(InvestmentItemArt.ReadPrimaryIcon(edited, 4) == 0x81D62071,
            "The cloned item still selects the donor icon.");
        for (int index = 0; index < 4; index++)
            Check(InvestmentItemArt.ReadPrimaryIcon(edited, index) == 0x81326128,
                "Retargeting a clone changed a shared source icon.");
        Check(InvestmentItemArt.SetPrimaryIcon(edited, 4, 0x81D62071).SequenceEqual(edited),
            "Repeating an icon assignment grew the table.");
        Check(InvestmentItemArt.ReadPrimaryIcon(original, 2) == 0x81326128,
            "The source table was mutated.");
        Reject(() => InvestmentItemArt.CloneRow(original, 2, 5));
        Reject(() => InvestmentItemArt.ReadPrimaryIcon(original, 4));
        byte[] broken = original.ToArray(); I(broken, 176 + 8, 9);
        Reject(() => InvestmentItemArt.CloneRow(broken, 0, 4));
        byte[] withPool = new byte[grown.Length + 48]; grown.CopyTo(withPool, 0);
        int header = (grown.Length + 19) & ~15;
        Array(withPool, 24, header, 1, 0x80800007); U(withPool, header + 16, 450);
        Q(withPool, 0, withPool.Length);
        byte[] withPerks = InvestmentItemArt.SetSandboxPerks(withPool, 4, [2481, 2482]);
        Check(InvestmentItemArt.ReadSandboxPerks(withPerks, 4).SequenceEqual(new ushort[] { 2481, 2482 }),
            "The clone did not load its authored sandbox displays.");
        for (int index = 0; index < 4; index++)
            Check(InvestmentItemArt.ReadSandboxPerks(withPerks, index).Count == 0,
                "Changing clone perks changed a shared source field list.");
        Check(InvestmentItemArt.ReadPrimaryIcon(withPerks, 4) == 0x81326128,
            "Changing perk dependencies changed the primary icon.");
        Check(InvestmentItemArt.SetSandboxPerks(withPerks, 4, [2481, 2482]).SequenceEqual(withPerks),
            "Repeating perk dependencies grew the art table.");
        byte[] cleared = InvestmentItemArt.SetSandboxPerks(withPerks, 4, []);
        Check(InvestmentItemArt.ReadSandboxPerks(cleared, 4).Count == 0,
            "Clearing perks retained the donor dependencies.");
        Reject(() => InvestmentItemArt.SetSandboxPerks(withPerks, 4, [ushort.MaxValue]));
        Console.WriteLine("item_art clone=pass native_index=pass shared_source=preserved repeat=stable bounds=reject");
        IconPasses();
        TailAppends();
    }

    /** Icon passes keep one copy of each grown pool; a repeated pass changes nothing. */
    private static void IconPasses()
    {
        byte[] stock = WithIntegers(Fixture(4));
        const int items = 3, passes = 4;
        byte[] table = stock;
        for (int item = 0; item < items; item++) table = InvestmentItemArt.CloneRow(table, 2, 4 + item);
        uint Icon(int pass, int item) => 0x81D60000u + (uint)(pass * 16 + item);
        for (int pass = 0; pass < passes; pass++)
        {
            for (int item = 0; item < items; item++)
            {
                table = InvestmentItemArt.SetPrimaryIcon(table, 4 + item, Icon(pass, item));
                table = InvestmentItemArt.SetSandboxPerks(table, 4 + item, [(ushort)(2481 + item), (ushort)pass]);
            }
            table = InvestmentItemArt.Rebuild(stock, table);
        }
        long bound = stock.Length;
        foreach (int field in new[] { 8, 24, 40, 56 })
        {
            long before = BinaryPrimitives.ReadInt64LittleEndian(stock.AsSpan(field));
            long after = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(field));
            int stride = field == 56 ? 16 : field == 40 ? 8 : 4;
            if (after > before) bound += 32 + after * stride;
        }
        Check(table.Length <= bound, $"Icon passes grew the art table to {table.Length} bytes; the bound is {bound}.");
        Check(OnlyPoolsChanged(stock, table) && Markers(table, stock.Length) == 4,
            "The rebuilt table changed installed bytes or kept more than one copy of a pool.");
        for (int item = 0; item < items; item++)
        {
            Check(InvestmentItemArt.ReadPrimaryIcon(table, 4 + item) == Icon(passes - 1, item), "An icon pass lost its icon.");
            Check(InvestmentItemArt.ReadSandboxPerks(table, 4 + item).SequenceEqual(new[] { (ushort)(2481 + item), (ushort)(passes - 1) }),
                "An icon pass lost its perks.");
        }
        for (int index = 0; index < 4; index++)
            Check(InvestmentItemArt.ReadPrimaryIcon(table, index) == 0x81326128
                && InvestmentItemArt.ReadSandboxPerks(table, index).Count == 0, "An icon pass changed an installed item.");
        byte[] again = table;
        for (int item = 0; item < items; item++)
        {
            again = InvestmentItemArt.SetPrimaryIcon(again, 4 + item, Icon(passes - 1, item));
            again = InvestmentItemArt.SetSandboxPerks(again, 4 + item, [(ushort)(2481 + item), (ushort)(passes - 1)]);
        }
        Check(InvestmentItemArt.Rebuild(stock, again).SequenceEqual(table), "A repeated icon pass changed the art table.");
        byte[] outside = table.ToArray(); outside[80] ^= 1;
        Reject(() => InvestmentItemArt.Rebuild(stock, outside));
        Console.WriteLine($"item_art passes={passes} items={items} size={table.Length} bound={bound} repeat=unchanged");
    }

    /** Arrays past the installed length grow in place; installed bytes never shift. */
    private static void TailAppends()
    {
        byte[] stock = WithIntegers(Fixture(4));
        byte[] Grow(byte[] table, int round) => InvestmentItemClone.AppendRows(table,
            [new(8, 4, [BitConverter.GetBytes(0x81D70000u + (uint)round)]), new(24, 4, [BitConverter.GetBytes((uint)round)])],
            stockLength: stock.Length).Table;
        byte[] first = Grow(stock, 0), second = Grow(first, 1), third = Grow(second, 2);
        Check(Markers(first, stock.Length) == 2 && Markers(third, stock.Length) == 2, "A repeated append moved an array again.");
        Check(OnlyPoolsChanged(stock, third), "An append shifted installed bytes.");
        var handles = ReadArray(third, 8);
        var integers = ReadArray(third, 24);
        Check(handles.Count == 4 && integers.Count == 4 && BinaryPrimitives.ReadUInt32LittleEndian(third.AsSpan(handles.Data)) == 0x81326128
            && BinaryPrimitives.ReadUInt32LittleEndian(third.AsSpan(handles.Data + 12)) == 0x81D70002
            && BinaryPrimitives.ReadUInt32LittleEndian(third.AsSpan(integers.Data + 12)) == 2, "Appended rows did not read back.");
        Check(BinaryPrimitives.ReadInt64LittleEndian(third) == third.Length, "The size word did not follow the appends.");
        byte[] unknown = InvestmentItemClone.AppendRows(Grow(stock, 0),
            [new(8, 4, [new byte[4]]), new(24, 4, [new byte[4]])]).Table;
        Check(OnlyPoolsChanged(stock, unknown), "An append with an unknown installed length shifted installed bytes.");
        Console.WriteLine($"append_rows tail=in_place size={third.Length} installed=unchanged");
    }

    /** Array headers past the installed length; each is one live or dead copy of a pool. */
    private static int Markers(byte[] table, int from)
    {
        int count = 0;
        for (int at = from; at + 4 <= table.Length; at += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(at)) == 0x80809FBD) count++;
        return count;
    }

    /** The ints pool after the fixture's trailing bytes, holding one value. */
    private static byte[] WithIntegers(byte[] table)
    {
        byte[] output = new byte[table.Length + 48]; table.CopyTo(output, 0);
        int header = (table.Length + 19) & ~15;
        Array(output, 24, header, 1, 0x80800007); U(output, header + 16, 450);
        Q(output, 0, output.Length);
        return output;
    }

    /** Installed bytes stay unchanged, except descriptors, the size word and moved pools. */
    private static bool OnlyPoolsChanged(byte[] stock, byte[] table)
    {
        if (table.Length < stock.Length) return false;
        var free = new bool[stock.Length];
        free.AsSpan(0, 8).Fill(true);
        foreach (int field in new[] { 8, 24, 40, 56 })
        {
            free.AsSpan(field, 16).Fill(true);
            var (count, data) = ReadArray(stock, field);
            int stride = field == 56 ? 16 : field == 40 ? 8 : 4;
            if (count > 0) free.AsSpan(data - 20, 20 + count * stride).Fill(true);
        }
        for (int offset = 0; offset < stock.Length; offset++)
            if (!free[offset] && stock[offset] != table[offset]) return false;
        // A pool that kept its rows in place must hold its installed rows unchanged.
        foreach (int field in new[] { 8, 24, 40, 56 })
        {
            var (count, data) = ReadArray(stock, field);
            int stride = field == 56 ? 16 : field == 40 ? 8 : 4;
            if (count > 0 && ReadArray(table, field).Data == data
                && !stock.AsSpan(data, count * stride).SequenceEqual(table.AsSpan(data, count * stride))) return false;
        }
        return true;
    }

    private static (int Count, int Data) ReadArray(byte[] bytes, int field)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
        if (count == 0) return (0, 0);
        return ((int)count, field + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8)) + 16);
    }

    /** Item rows share one primary container and leave unrelated bytes after their array. */
    public static byte[] Fixture(int count)
    {
        byte[] bytes = new byte[176 + count * 16 + 16];
        Q(bytes, 0, bytes.Length);
        Array(bytes, 8, 96, 1, 0x80800014); U(bytes, 112, 0x81326128);
        Array(bytes, 40, 128, 1, 0x808056D6); bytes[148] = 1; bytes[149] = 1;
        Array(bytes, 56, 160, count, 0x808056D1);
        for (int index = 0; index < count; index++)
        {
            U(bytes, 176 + index * 16, 0xFFFFFFFF);
            I(bytes, 180 + index * 16, index + 1); bytes[188 + index * 16] = 1;
        }
        bytes[^1] = 0xAC;
        return bytes;
    }

    private static byte[] Row(byte[] bytes, int index) => bytes.AsSpan(
        64 + checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64))) + 16 + index * 16, 16).ToArray();
    private static void Array(byte[] bytes, int field, int header, int count, uint type)
    {
        Q(bytes, field, count); Q(bytes, field + 8, header - field - 8);
        U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); U(bytes, header + 8, type);
    }
    private static void I(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Q(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Invalid item art input was accepted.");
    }
}
