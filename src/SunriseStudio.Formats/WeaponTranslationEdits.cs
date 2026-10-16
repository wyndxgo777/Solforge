using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** One column of a weapon-type group: its table index, column index and display name. */
public readonly record struct WeaponTranslationColumn(int Table, int Column, string Name);

/** One table of a weapon-type group: the translation key it reads, its stat range and its rows of columns. */
public sealed record WeaponTranslationTable(int Index, int Key, float Minimum, float Maximum, int RowOffset,
    bool Interpolates, IReadOnlyList<IReadOnlyList<float>> Rows)
{
    /** The weapon stat this table reads, or null when its key names no stat. */
    public byte? Stat => WeaponTranslationEdits.StatOf(Key);
    public int Columns => Rows.Count == 0 ? 0 : Rows.Min(row => row.Count);

    /** The column's value at a stat value, read the way the game reads it. */
    public float Evaluate(int column, float stat)
    {
        int last = Rows.Count - 1;
        float span = Maximum - Minimum;
        float position = span == 0 ? 0 : Math.Clamp((stat - Minimum) * last / span, 0, last);
        position += RowOffset;
        if (!Interpolates) return Rows[Math.Clamp((int)position - RowOffset, 0, last)][column];
        int lower = Math.Clamp((int)MathF.Floor(position) - RowOffset, 0, last), upper = Math.Min(lower + 1, last);
        float blend = position - MathF.Floor(position);
        return Rows[lower][column] + (Rows[upper][column] - Rows[lower][column]) * blend;
    }
}

/** A weapon translation config turns raw item stats into gameplay values through per-type tables. */
public static class WeaponTranslationEdits
{
    /** Auto rifle, pulse, scout and SMG translation config shared by stock weapons. */
    public const uint SharedTranslation = 0x8152C7F9;
    public const byte RateStat = 14, MagazineStat = 24;
    public static readonly WeaponTranslationColumn Rate = new(0, 0, "rounds per second");
    public static readonly WeaponTranslationColumn Damage = new(0, 2, "damage per shot");
    public static readonly WeaponTranslationColumn MagazineReferenceRate = new(1, 0, "magazine reference rate");
    public static readonly WeaponTranslationColumn Precision = new(1, 1, "precision multiplier");
    public static readonly WeaponTranslationColumn MagazineBase = new(7, 0, "magazine base");
    // Weapon-type groups at +0x430; each owns a table array. A table has a row array, and each row is a float
    // array of columns.
    private const int GroupsField = 0x430, GroupStride = 0x30, TableStride = 0x38, RowsField = 16, RowStride = 16;
    private const uint GroupClass = 0x80803975, TableClass = 0x80803979, ColumnClass = 0x8080000F;
    // A table names its key at +32 and its row offset at +36, maps the stat range +40..+44 onto its rows, and
    // blends rows when +48 is set.
    private const int TableKey = 32, TableRowOffset = 36, TableMinimum = 40, TableMaximum = 44, TableInterpolates = 48;
    // Selector rows at +0x620, 40 bytes each: the row whose key at +16 equals the weapon's type key is the group.
    // Each row starts with {i8 key, f32 value} overrides that pin a key's stat.
    private const int SelectorsField = 0x620, SelectorStride = 40, SelectorKey = 16, OverrideStride = 8;

    // Translation key -> weapon stat; keys 2, 12 and 19 read no stat.
    private static readonly byte?[] KeyStats =
        [14, 24, null, 32, 17, 22, 20, 16, 21, 27, 18, 15, null, 25, 26, 26, 26, 30, 31, null];

    /** Stat for a key index, or null when the key has none. */
    public static byte? StatOf(int key) => key >= 0 && key < KeyStats.Length ? KeyStats[key] : null;

    /** Number of groups in the weapon config. */
    public static int GroupCount(ReadOnlyMemory<byte> config) =>
        new TagReader(config).Array(GroupsField, GroupClass, GroupStride).Count;

    /** The group the game selects for a weapon type key; a key no selector row holds selects group 0. */
    public static int SelectGroup(ReadOnlyMemory<byte> config, uint typeKey)
    {
        var rows = new TagReader(config).Array(SelectorsField, stride: SelectorStride);
        for (int index = 0; index < rows.Count; index++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(config.Span[(rows.Data + index * SelectorStride + SelectorKey)..]) == typeKey)
                return index < GroupCount(config) ? index : throw new InvalidDataException("A selector row has no weapon-type group.");
        return 0;
    }

    /** The selector key of each group, in group order. */
    public static IReadOnlyList<uint> SelectorKeys(ReadOnlyMemory<byte> config)
    {
        var rows = new TagReader(config).Array(SelectorsField, stride: SelectorStride);
        return Enumerable.Range(0, rows.Count).Select(index =>
            BinaryPrimitives.ReadUInt32LittleEndian(config.Span[(rows.Data + index * SelectorStride + SelectorKey)..])).ToArray();
    }

    /** Stat values the selected row pins, by translation key. */
    public static IReadOnlyDictionary<int, float> Overrides(ReadOnlyMemory<byte> config, int group)
    {
        var reader = new TagReader(config);
        var rows = reader.Array(SelectorsField, stride: SelectorStride);
        var result = new Dictionary<int, float>();
        if (group >= rows.Count) return result;
        var overrides = reader.Array(rows.Data + group * SelectorStride, stride: OverrideStride);
        for (int index = 0; index < overrides.Count; index++)
        {
            int at = overrides.Data + index * OverrideStride;
            result[(sbyte)config.Span[at]] = BinaryPrimitives.ReadSingleLittleEndian(config.Span[(at + 4)..]);
        }
        return result;
    }

    /** Every table of one group. */
    public static IReadOnlyList<WeaponTranslationTable> Tables(ReadOnlyMemory<byte> config, int group)
    {
        var reader = new TagReader(config);
        var tables = reader.Array(Group(reader, group), TableClass, TableStride);
        return Enumerable.Range(0, tables.Count).Select(index =>
        {
            int table = tables.Data + index * TableStride;
            var rows = reader.Array(table + RowsField, stride: RowStride);
            var values = Enumerable.Range(0, rows.Count).Select(row =>
            {
                var columns = reader.Array(rows.Data + row * RowStride, ColumnClass, 4);
                return (IReadOnlyList<float>)Enumerable.Range(0, columns.Count)
                    .Select(column => BinaryPrimitives.ReadSingleLittleEndian(config.Span[(columns.Data + column * 4)..])).ToArray();
            }).ToArray();
            var span = config.Span;
            return new WeaponTranslationTable(index, BinaryPrimitives.ReadInt32LittleEndian(span[(table + TableKey)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(table + TableMinimum)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(table + TableMaximum)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(table + TableRowOffset)..]), span[table + TableInterpolates] != 0, values);
        }).ToArray();
    }

    /** Every row of the column takes `value`. */
    public static byte[] SetFlat(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"The {column.Name} must be a finite number.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), value);
        return result;
    }

    /** Every row of the column is multiplied by `factor`, so the column keeps its shape across the stat range. */
    public static byte[] Scale(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0) throw new InvalidDataException($"The {column.Name} scale must be positive.");
        byte[] result = config.ToArray();
        foreach (int at in Offsets(config, group, column))
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(at), BinaryPrimitives.ReadSingleLittleEndian(result.AsSpan(at)) * factor);
        return result;
    }

    /** Native magazine size: round half to even of base times rate over the reference rate. */
    public static int Magazine(float magazineBase, float rate, float referenceRate) =>
        (int)Math.Round(magazineBase * rate / referenceRate, MidpointRounding.ToEven);

    private static int Group(TagReader reader, int group)
    {
        var groups = reader.Array(GroupsField, GroupClass, GroupStride);
        if (group < 0 || group >= groups.Count) throw new InvalidDataException("The weapon-type group is outside the config.");
        return groups.Data + group * GroupStride;
    }

    private static IReadOnlyList<int> Offsets(ReadOnlyMemory<byte> config, int group, WeaponTranslationColumn column)
    {
        var reader = new TagReader(config);
        var tables = reader.Array(Group(reader, group), TableClass, TableStride);
        if (column.Table < 0 || column.Table >= tables.Count)
            throw new InvalidDataException($"The weapon-type group has no table for the {column.Name}.");
        var rows = reader.Array(tables.Data + column.Table * TableStride + RowsField, stride: RowStride);
        return Enumerable.Range(0, rows.Count).Select(row =>
        {
            var columns = reader.Array(rows.Data + row * RowStride, ColumnClass, 4);
            return column.Column < columns.Count ? columns.Data + column.Column * 4
                : throw new InvalidDataException($"A translation row has no column for the {column.Name}.");
        }).ToArray();
    }
}
