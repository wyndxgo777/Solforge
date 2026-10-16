using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** A localized text reference: string bank and string hash. */
public readonly record struct InvestmentString(uint Bank, uint Hash);
/** A lore entry: title, subtitle and body text references. */
public sealed record InvestmentLoreRow(int Index, uint Hash, InvestmentString Title, InvestmentString Subtitle,
    InvestmentString Body);

/** An item's lore block holds a u16 row index into two parallel tables of the same length. */
public static class InvestmentLore
{
    /** investment_globals slot 34; 40-byte rows with the lore strings. */
    public const uint ClientTable = 0x81613CF2;
    /** Root blob +840; 16-byte rows. Its reader has no bounds check, so both tables grow together. */
    public const uint ExeTable = 0x81319348;
    public const ushort None = 0xFFFF;
    // Both tables keep their row array at +8.
    private const int RowsField = 8, ClientStride = 40, ExeStride = 16;
    private const int HashField = 8, TitleField = 12, SubtitleField = 20, BodyField = 28;
    // The item definition points at its lore block from +40; the block's class marker precedes it.
    private const int DefinitionBlock = 40;
    private const uint BlockClass = 0x808077ED;
    /** An empty subtitle names no bank and the empty-string hash. */
    public static readonly InvestmentString Empty = new(0xFFFF, 0x811C9DC5);

    /** The lore block's offset, or null when the item has none. */
    public static int? BlockOffset(ReadOnlySpan<byte> definition)
    {
        if (definition.Length < DefinitionBlock + 8) return null;
        long relative = BinaryPrimitives.ReadInt64LittleEndian(definition[DefinitionBlock..]);
        if (relative == 0) return null;
        long at = DefinitionBlock + relative;
        if (at < 4 || at > definition.Length - 4 || BinaryPrimitives.ReadUInt32LittleEndian(definition[(int)(at - 4)..]) != BlockClass)
            throw new InvalidDataException("The item's lore block is not where its pointer says.");
        return (int)at;
    }

    /** Lore row index an item definition points at, or null if it has no lore block. */
    public static ushort? ReadIndex(ReadOnlySpan<byte> definition) =>
        BlockOffset(definition) is { } at ? BinaryPrimitives.ReadUInt16LittleEndian(definition[at..]) : null;

    /** Returns the definition with its lore index changed; throws if the item has no lore block. */
    public static byte[] SetIndex(ReadOnlySpan<byte> definition, ushort index)
    {
        int at = BlockOffset(definition) ?? throw new InvalidDataException("The item has no lore block.");
        byte[] output = definition.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at), index);
        return output;
    }

    /** Number of lore rows in the client table. */
    public static int Count(ReadOnlyMemory<byte> client) => new TagReader(client).Array(RowsField, stride: ClientStride).Count;

    /** Reads one lore row. */
    public static InvestmentLoreRow ReadRow(ReadOnlyMemory<byte> client, int index)
    {
        var reader = new TagReader(client);
        var rows = reader.Array(RowsField, stride: ClientStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The lore row is outside the table.");
        int row = rows.Data + index * ClientStride;
        InvestmentString Read(int field) => new(reader.U16(row + field), reader.U32(row + field + 4));
        return new(index, reader.U32(row + HashField), Read(TitleField), Read(SubtitleField), Read(BodyField));
    }

    /** Hashes of all lore rows. */
    public static IReadOnlySet<uint> Hashes(ReadOnlyMemory<byte> client)
    {
        var reader = new TagReader(client);
        var rows = reader.Array(RowsField, stride: ClientStride);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * ClientStride + HashField)).ToHashSet();
    }

    /**
     * Appends a copy of one row to both tables, under a new lore hash.
     * @return The grown tables and the new row's index, which is the same in both.
     */
    public static (byte[] Client, byte[] Exe, int Index) Append(ReadOnlyMemory<byte> client, ReadOnlyMemory<byte> exe,
        int copyFrom, uint hash)
    {
        var clientRows = new TagReader(client).Array(RowsField, stride: ClientStride);
        var exeRows = new TagReader(exe).Array(RowsField, stride: ExeStride);
        if (clientRows.Count != exeRows.Count) throw new InvalidDataException("The two lore tables differ in length.");
        if (copyFrom < 0 || copyFrom >= clientRows.Count) throw new InvalidDataException("The lore row to copy is outside the table.");
        if (clientRows.Count >= short.MaxValue) throw new InvalidDataException("The lore table has no index left.");
        if (Hashes(client).Contains(hash)) throw new InvalidDataException("The lore hash is already used.");
        byte[] clientRow = client.Span.Slice(clientRows.Data + copyFrom * ClientStride, ClientStride).ToArray();
        byte[] exeRow = exe.Span.Slice(exeRows.Data + copyFrom * ExeStride, ExeStride).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(clientRow.AsSpan(HashField), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(exeRow.AsSpan(HashField), hash);
        return (InvestmentItemClone.AppendRow(client, RowsField, ClientStride, clientRow),
            InvestmentItemClone.AppendRow(exe, RowsField, ExeStride, exeRow), clientRows.Count);
    }

    /** Returns the client table with a row's title, subtitle and body references replaced. */
    public static byte[] SetStrings(ReadOnlyMemory<byte> client, int index, InvestmentString title,
        InvestmentString subtitle, InvestmentString body)
    {
        var rows = new TagReader(client).Array(RowsField, stride: ClientStride);
        if (index < 0 || index >= rows.Count) throw new InvalidDataException("The lore row is outside the table.");
        byte[] output = client.ToArray();
        int row = rows.Data + index * ClientStride;
        void Write(int field, InvestmentString value)
        {
            if (value.Bank > ushort.MaxValue) throw new InvalidDataException("A lore string bank must fit 16 bits.");
            // The bank is a u16; bytes +2 and +3 keep their stored values.
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(row + field), (ushort)value.Bank);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(row + field + 4), value.Hash);
        }
        Write(TitleField, title); Write(SubtitleField, subtitle); Write(BodyField, body);
        return output;
    }
}
