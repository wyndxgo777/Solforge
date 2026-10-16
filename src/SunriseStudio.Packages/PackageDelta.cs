using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Packages;

/** Why a delta row overrides the source entry table. */
[Flags]
public enum PackageEntryOverride : uint
{
    None = 0,
    /** The row keeps its source index and points at new blocks. */
    Replaced = 1,
    /** The row is new, after the source's last entry. */
    Appended = 2,
}

/** One entry-table redirection: the source row (zero when appended) and the row the next patch uses. */
public sealed record PackageEntryRedirect(int EntryIndex, uint TagHash, PackageEntryOverride Flags, uint Reference,
    uint TypeInfo, ulong OldBlockInfo, ulong NewBlockInfo)
{
    public int StartBlock => (int)(NewBlockInfo & 0x3FFF);
    public int StartOffset => (int)(((NewBlockInfo >> 14) & 0x3FFF) << 4);
    public int Size => checked((int)(NewBlockInfo >> 28));
}

/** A §5.3 block record whose bytes live in the delta file: Offset is a delta file offset, Patch the target patch. */
public sealed record PackageDeltaBlock(int Index, uint Offset, int Size, ushort Patch, ushort Flags, byte[] Sha1);

/**
 * Solforge Phase 08: a non-destructive block allocator. It reads a source package (header and tables only,
 * shared read), places replaced and appended entries in new raw blocks after the source's last block, and
 * writes them to a project delta file with the entry-table redirections the next patch generation needs.
 * The source package is never opened for writing.
 */
public sealed class PackageBlockAllocator
{
    public const int MaximumEntries = TagHeaders.MaximumEntryIndex + 1, MaximumBlocks = 16384, MaximumPatch = 255;
    private readonly PackageFile source;
    private readonly byte[] sourceEntries;
    private readonly SortedDictionary<int, byte[]> replacements = [];
    private readonly List<(uint Reference, uint TypeInfo, byte[] Data)> appended = [];

    public string SourcePath => source.Path;
    public ushort PackageId => source.Id;
    public ushort SourcePatch => source.Patch;
    public ushort TargetPatch => checked((ushort)(source.Patch + 1));
    public int SourceEntryCount => source.EntryCount;
    public int SourceBlockCount => source.BlockCount;
    public int EntryCount => source.EntryCount + appended.Count;

    private PackageBlockAllocator(PackageFile source, byte[] sourceEntries)
    {
        this.source = source;
        this.sourceEntries = sourceEntries;
    }

    /** Opens a source package to patch. */
    public static PackageBlockAllocator Open(string sourcePackagePath)
    {
        var file = new PackageFile(Path.GetFullPath(sourcePackagePath));
        var (entries, blocks) = file.ReadTables();
        file.LoadTables(entries, blocks);
        if (file.Patch >= MaximumPatch)
            throw new InvalidDataException($"{file.Name} is at patch {file.Patch}; a delta needs a patch below {MaximumPatch}.");
        return new PackageBlockAllocator(file, entries);
    }

    /** Throws when a package would exceed 8192 entries or 16384 blocks (Studio.md Phase 08 limits). */
    public static void CheckLimits(int entryCount, int blockCount)
    {
        if (entryCount is < 1 or > MaximumEntries)
            throw new InvalidDataException($"A package holds 1 to {MaximumEntries} entries, not {entryCount}.");
        if (blockCount is < 1 or > MaximumBlocks)
            throw new InvalidDataException($"A package holds 1 to {MaximumBlocks} blocks, not {blockCount}.");
    }

    /** New authored packages take ids 0x0100-0x0CFF (PackageWriter.FirstNewPackageId..LastNewPackageId). */
    public static void CheckNewPackageId(int packageId)
    {
        if (!PackageWriter.IsNewPackageId(packageId))
            throw new InvalidDataException($"Package id 0x{packageId:X4} is outside {PackageWriter.NewPackageIdRange}.");
    }

    /** Replaces one entry's data; the entry must already exist in the source. */
    public void Replace(int entryIndex, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if ((uint)entryIndex >= (uint)source.EntryCount)
            throw new InvalidDataException($"Entry {entryIndex} is outside {source.Name}.");
        replacements[entryIndex] = data;
    }

    /** @return The appended entry's tag hash. */
    public uint Append(uint reference, uint typeInfo, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        CheckLimits(EntryCount + 1, source.BlockCount);
        appended.Add((reference, typeInfo, data));
        return TagHeaders.Compose(source.Id, EntryCount - 1);
    }

    /** Places every edit in new blocks and writes the delta file atomically; the source is only read. */
    public PackageDelta Write(string deltaPath)
    {
        string output = Path.GetFullPath(deltaPath);
        if (output.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A package delta cannot replace its source.");
        if (replacements.Count == 0 && appended.Count == 0)
            throw new InvalidDataException("A package delta needs at least one edit.");
        var payloads = replacements.Values.Concat(appended.Select(x => x.Data)).ToArray();
        var (placements, blocks) = PackageWriter.Place(payloads, source.BlockCount);
        CheckLimits(EntryCount, source.BlockCount + blocks.Count);
        // Only the last block can be partly used; store it trimmed.
        long position = 0;
        foreach (byte[] payload in payloads) position = ((position + 15) & ~15L) + payload.Length;
        int lastUsed = blocks.Count == 0 ? 0 : (int)(position - (long)(blocks.Count - 1) * 0x40000);

        var redirects = new List<PackageEntryRedirect>();
        int row = 0;
        foreach (var (index, _) in replacements)
        {
            int at = index * TagHeaders.EntryRecordSize;
            redirects.Add(new(index, TagHeaders.Compose(source.Id, index), PackageEntryOverride.Replaced,
                BinaryPrimitives.ReadUInt32LittleEndian(sourceEntries.AsSpan(at)),
                BinaryPrimitives.ReadUInt32LittleEndian(sourceEntries.AsSpan(at + 4)),
                BinaryPrimitives.ReadUInt64LittleEndian(sourceEntries.AsSpan(at + 8)), placements[row++]));
        }
        for (int i = 0; i < appended.Count; i++)
        {
            int index = source.EntryCount + i;
            redirects.Add(new(index, TagHeaders.Compose(source.Id, index), PackageEntryOverride.Appended,
                appended[i].Reference, appended[i].TypeInfo, 0, placements[row++]));
        }

        int blockTable = PackageDelta.HeaderSize + redirects.Count * PackageDelta.RedirectSize;
        long dataStart = (blockTable + (long)blocks.Count * PackageDelta.BlockRecordSize + 15) & ~15L;
        var records = new List<PackageDeltaBlock>();
        long offset = dataStart;
        for (int i = 0; i < blocks.Count; i++)
        {
            int size = i == blocks.Count - 1 ? lastUsed : blocks[i].Length;
            records.Add(new(source.BlockCount + i, checked((uint)offset), size, TargetPatch, 0,
                SHA1.HashData(blocks[i].AsSpan(0, size))));
            offset = (offset + size + 15) & ~15L;
        }
        var delta = new PackageDelta(source.Id, source.Patch, TargetPatch, source.EntryCount, source.BlockCount,
            source.Header.AsSpan(0x118, 20).ToArray(), new FileInfo(source.Path).Length, redirects, records);
        byte[] bytes = new byte[checked((int)offset)];
        delta.WriteTables(bytes);
        for (int i = 0; i < blocks.Count; i++)
            blocks[i].AsSpan(0, records[i].Size).CopyTo(bytes.AsSpan((int)records[i].Offset));
        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = output + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, output, overwrite: true);
        return PackageDelta.Read(output);
    }
}

/**
 * A project delta file ("SFPD" v1): header, 32-byte redirect rows, 48-byte §5.3 block records, then raw blocks.
 * Header: magic, version, package id, source/target patch, source entry/block counts, row and block counts,
 * the source's metadata SHA-1 (header 0x118) and length, which bind the delta to the package it was made from.
 */
public sealed class PackageDelta
{
    public const uint Magic = 0x44504653; // "SFPD"
    public const int Version = 1, HeaderSize = 0x40, RedirectSize = 32, BlockRecordSize = 48;
    private byte[] file = [];

    public ushort PackageId { get; }
    public ushort SourcePatch { get; }
    public ushort TargetPatch { get; }
    public int SourceEntryCount { get; }
    public int SourceBlockCount { get; }
    public byte[] SourceMetadataSha1 { get; }
    public long SourceLength { get; }
    public IReadOnlyList<PackageEntryRedirect> Redirects { get; }
    public IReadOnlyList<PackageDeltaBlock> Blocks { get; }
    public int EntryCount => SourceEntryCount + Redirects.Count(x => (x.Flags & PackageEntryOverride.Appended) != 0);
    public int BlockCount => SourceBlockCount + Blocks.Count;

    internal PackageDelta(ushort packageId, ushort sourcePatch, ushort targetPatch, int sourceEntryCount,
        int sourceBlockCount, byte[] sourceMetadataSha1, long sourceLength, IReadOnlyList<PackageEntryRedirect> redirects,
        IReadOnlyList<PackageDeltaBlock> blocks)
    {
        PackageId = packageId; SourcePatch = sourcePatch; TargetPatch = targetPatch;
        SourceEntryCount = sourceEntryCount; SourceBlockCount = sourceBlockCount;
        SourceMetadataSha1 = sourceMetadataSha1; SourceLength = sourceLength;
        Redirects = redirects; Blocks = blocks;
    }

    /** Writes the delta header and tables into the buffer. */
    internal void WriteTables(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], PackageId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], SourcePatch);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[10..], TargetPatch);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], SourceEntryCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], SourceBlockCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], Redirects.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], Blocks.Count);
        SourceMetadataSha1.CopyTo(bytes[28..]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], SourceLength);
        for (int i = 0; i < Redirects.Count; i++)
        {
            var row = bytes.Slice(HeaderSize + i * RedirectSize, RedirectSize);
            var redirect = Redirects[i];
            BinaryPrimitives.WriteInt32LittleEndian(row, redirect.EntryIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], (uint)redirect.Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], redirect.Reference);
            BinaryPrimitives.WriteUInt32LittleEndian(row[12..], redirect.TypeInfo);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], redirect.OldBlockInfo);
            BinaryPrimitives.WriteUInt64LittleEndian(row[24..], redirect.NewBlockInfo);
        }
        int table = HeaderSize + Redirects.Count * RedirectSize;
        for (int i = 0; i < Blocks.Count; i++)
        {
            var slot = bytes.Slice(table + i * BlockRecordSize, BlockRecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, Blocks[i].Offset);
            BinaryPrimitives.WriteInt32LittleEndian(slot[4..], Blocks[i].Size);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[8..], Blocks[i].Patch);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[10..], Blocks[i].Flags);
            Blocks[i].Sha1.CopyTo(slot[12..]);
        }
    }

    /** Reads and validates a delta, including every block's SHA-1. */
    public static PackageDelta Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != Version)
            throw new InvalidDataException($"Not a package delta: {path}");
        int redirectCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        if (redirectCount is < 0 or > PackageBlockAllocator.MaximumEntries || blockCount is < 0 or > PackageBlockAllocator.MaximumBlocks
            || HeaderSize + (long)redirectCount * RedirectSize + (long)blockCount * BlockRecordSize > bytes.Length)
            throw new InvalidDataException($"Package delta tables exceed the file: {path}");
        ushort packageId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        var redirects = new PackageEntryRedirect[redirectCount];
        for (int i = 0; i < redirectCount; i++)
        {
            var row = bytes.AsSpan(HeaderSize + i * RedirectSize, RedirectSize);
            int index = BinaryPrimitives.ReadInt32LittleEndian(row);
            redirects[i] = new(index, TagHeaders.Compose(packageId, index),
                (PackageEntryOverride)BinaryPrimitives.ReadUInt32LittleEndian(row[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]), BinaryPrimitives.ReadUInt32LittleEndian(row[12..]),
                BinaryPrimitives.ReadUInt64LittleEndian(row[16..]), BinaryPrimitives.ReadUInt64LittleEndian(row[24..]));
        }
        int sourceBlockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        int table = HeaderSize + redirectCount * RedirectSize;
        var blocks = new PackageDeltaBlock[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            var slot = bytes.AsSpan(table + i * BlockRecordSize, BlockRecordSize);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(slot);
            int size = BinaryPrimitives.ReadInt32LittleEndian(slot[4..]);
            byte[] sha1 = slot.Slice(12, 20).ToArray();
            if (size is <= 0 or > 0x40000 || offset > bytes.Length - (long)size
                || !SHA1.HashData(bytes.AsSpan((int)offset, size)).AsSpan().SequenceEqual(sha1))
                throw new InvalidDataException($"Package delta block {i} is damaged: {path}");
            blocks[i] = new(sourceBlockCount + i, offset, size, BinaryPrimitives.ReadUInt16LittleEndian(slot[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(slot[10..]), sha1);
        }
        return new PackageDelta(packageId, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)),
            sourceBlockCount, bytes.AsSpan(28, 20).ToArray(), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)),
            redirects, blocks) { file = bytes };
    }

    /** True while the source still has the length and metadata digest the delta was made from. */
    public bool Matches(string sourcePackagePath)
    {
        var header = PackageFile.ReadAt(sourcePackagePath, 0, 0x180);
        return new FileInfo(sourcePackagePath).Length == SourceLength
            && header.AsSpan(0x118, 20).SequenceEqual(SourceMetadataSha1)
            && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) == PackageId
            && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x20)) == SourcePatch;
    }

    /** The bytes of a replaced or appended entry, read from the delta's own blocks. */
    public byte[] ReadEntry(int entryIndex)
    {
        var redirect = Redirects.FirstOrDefault(x => x.EntryIndex == entryIndex)
            ?? throw new KeyNotFoundException($"Entry {entryIndex} is not in the delta.");
        byte[] result = new byte[redirect.Size];
        int copied = 0, skip = redirect.StartOffset;
        for (int block = redirect.StartBlock - SourceBlockCount; copied < result.Length; block++, skip = 0)
        {
            if ((uint)block >= (uint)Blocks.Count) throw new InvalidDataException($"Entry {entryIndex} leaves the delta blocks.");
            var slot = Blocks[block];
            int count = Math.Min(slot.Size - skip, result.Length - copied);
            if (count <= 0) throw new InvalidDataException($"Entry {entryIndex} exceeds its delta block.");
            file.AsSpan((int)slot.Offset + skip, count).CopyTo(result.AsSpan(copied));
            copied += count;
        }
        return result;
    }

    /** The next patch's entry table: the source rows with each redirect applied and appended rows added. */
    public byte[] BuildEntryTable(string sourcePackagePath)
    {
        if (!Matches(sourcePackagePath))
            throw new InvalidDataException("The source package changed since the delta was made.");
        var source = new PackageFile(Path.GetFullPath(sourcePackagePath));
        byte[] entries = source.ReadTables().Entries;
        byte[] table = new byte[checked(EntryCount * TagHeaders.EntryRecordSize)];
        entries.CopyTo(table, 0);
        foreach (var redirect in Redirects)
        {
            var row = table.AsSpan(redirect.EntryIndex * TagHeaders.EntryRecordSize, TagHeaders.EntryRecordSize);
            TagHeaders.WriteEntryRecord(row, redirect.Reference, redirect.TypeInfo, redirect.StartBlock,
                redirect.StartOffset, redirect.Size);
        }
        return table;
    }
}
