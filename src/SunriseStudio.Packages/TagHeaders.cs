using System.Buffers.Binary;

namespace SunriseStudio.Packages;

/**
 * Solforge Phase 07: the 32-bit tag hash and the 16-byte entry record (Studio.md §5.2, §5.4).
 * TagHash = 0x80800000 + (PackageId << 13) + EntryIndex; addition, because the package bits overlap the base.
 * The hash carries no class; the class comes from the entry's Reference and TypeInfo.
 */
public static class TagHeaders
{
    public const uint TagBase = 0x80800000;
    public const int EntryRecordSize = 16;
    /** Entry indices are 13 bits (PackageDependencyTable.MaximumEntry). */
    public const int MaximumEntryIndex = PackageDependencyTable.MaximumEntry;

    /** Tag hash for a package id and entry index. */
    public static uint Compose(ushort packageId, int entryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entryIndex, MaximumEntryIndex);
        return checked(TagBase + ((uint)packageId << 13) + (uint)entryIndex);
    }

    /** False for hashes below the base or whose package id does not fit 16 bits. */
    public static bool TryDecompose(uint tagHash, out ushort packageId, out int entryIndex)
    {
        packageId = 0;
        entryIndex = 0;
        if (tagHash < TagBase) return false;
        uint offset = tagHash - TagBase;
        if (offset >> 13 > ushort.MaxValue) return false;
        packageId = (ushort)(offset >> 13);
        entryIndex = (int)(offset & MaximumEntryIndex);
        return true;
    }

    /** Parses one package's entry table; PackageFile.LoadTables uses this, so it is the catalog's parse path. */
    public static PackageEntry[] ParseEntryTable(ushort packageId, string packageName, ReadOnlySpan<byte> table)
    {
        if (table.Length % EntryRecordSize != 0 || table.Length / EntryRecordSize > MaximumEntryIndex + 1)
            throw new InvalidDataException($"Invalid entry table size {table.Length} for {packageName}.");
        var entries = new PackageEntry[table.Length / EntryRecordSize];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = ParseEntryRecord(packageId, packageName, i, table.Slice(i * EntryRecordSize, EntryRecordSize));
        return entries;
    }

    /** Reference +0x00, TypeInfo +0x04, BlockInfo +0x08: start block bits 0-13, offset bits 14-27 (x16), size bits 28-63. */
    public static PackageEntry ParseEntryRecord(ushort packageId, string packageName, int entryIndex, ReadOnlySpan<byte> record)
    {
        if (record.Length < EntryRecordSize) throw new InvalidDataException("Truncated entry record.");
        uint reference = BinaryPrimitives.ReadUInt32LittleEndian(record);
        uint typeInfo = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
        ulong blockInfo = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        return new PackageEntry(Compose(packageId, entryIndex), reference, (int)((typeInfo >> 9) & 127),
            (int)((typeInfo >> 6) & 7), checked((int)(blockInfo >> 28)), packageName, packageId, entryIndex)
        {
            TypeInfo = typeInfo,
            StartBlock = (int)(blockInfo & 0x3FFF),
            StartOffset = (int)(((blockInfo >> 14) & 0x3FFF) << 4)
        };
    }

    /** The inverse of ParseEntryRecord, for authored tables and checks. */
    public static void WriteEntryRecord(Span<byte> record, uint reference, uint typeInfo, int startBlock, int startOffset, long size)
    {
        if (startBlock is < 0 or > 0x3FFF || startOffset < 0 || (startOffset & 15) != 0 || startOffset >> 4 > 0x3FFF
            || size < 0 || size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(startBlock), "Entry placement does not fit the record.");
        BinaryPrimitives.WriteUInt32LittleEndian(record, reference);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], typeInfo);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..],
            ((ulong)(uint)size << 28) | ((ulong)(uint)(startOffset >> 4) << 14) | (uint)startBlock);
    }
}

/** Locator half of PackageCatalog: package id and entry index of an installed tag. */
public sealed partial class PackageCatalog
{
    /** Solforge Phase 07: the package id and entry index an installed tag hash names (§5.4). */
    public bool TryLocate(uint tagHash, out ushort packageId, out int entryIndex)
    {
        if (TryGetEntry(tagHash, out var entry) && TagHeaders.TryDecompose(tagHash, out packageId, out entryIndex)
            && entry.PackageId == packageId && entry.EntryIndex == entryIndex)
            return true;
        packageId = 0;
        entryIndex = 0;
        return false;
    }
}
