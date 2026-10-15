using System.Buffers.Binary;

namespace SunriseStudio.Packages;

/** Metadata comes from the header, including packages whose names carry no numeric id. */
internal sealed class PackageFile
{
    internal string Path { get; }
    internal string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    internal ushort Id { get; }
    internal ushort Patch { get; }
    internal uint Build { get; }
    internal ulong Group { get; }
    internal int Variant { get; }
    internal int EntryCount { get; }
    internal int BlockCount { get; }
    internal long EntryOffset { get; }
    internal long BlockOffset { get; private set; }
    internal long MiscOffset { get; }
    internal PackageEntry[] Entries { get; private set; } = [];
    internal BlockRecord[] Blocks { get; private set; } = [];

    /** The 0x180 header bytes, kept so the metadata index (Solforge Phase 05) can store them. */
    internal byte[] Header { get; }

    internal PackageFile(string path) : this(path, ReadAt(path, 0, 0x180)) { }

    /** Variant 1 sizes the entry region with its own count, not the logical entry count. */
    internal PackageFile(string path, byte[] header)
    {
        Path = path;
        Header = header;
        if (header.Length != 0x180) throw new InvalidDataException($"Package header size mismatch: {path}");
        if (U16(header, 0) != 38 || U16(header, 2) != 2)
            throw new InvalidDataException($"Unsupported package format: {path}");
        Id = U16(header, 4);
        Patch = U16(header, 0x20);
        Build = U32(header, 0x18);
        Group = U64(header, 8);
        Variant = header[0x1A];
        EntryCount = checked((int)U32(header, 0xB4));
        BlockCount = checked((int)U32(header, 0xD0));
        MiscOffset = U32(header, 0xF0);
        if (EntryCount is < 1 or > 8192 || BlockCount is < 1 or > 16384)
            throw new InvalidDataException($"Invalid package table counts: {path}");
        if (Variant == 0)
        {
            EntryOffset = U32(header, 0xB8);
            BlockOffset = U32(header, 0xD4);
        }
        else if (Variant == 1)
        {
            EntryOffset = U32(header, 0x110) + 96L;
        }
        else
            throw new InvalidDataException($"Unsupported package variant: {Variant}");
    }

    internal void LoadTables()
    {
        var (entries, blocks) = ReadTables();
        LoadTables(entries, blocks);
    }

    /** Solforge Phase 05: parses tables the metadata index stored, with the block offset ReadTables found. */
    internal void LoadTables(byte[] entries, byte[] blocks, long blockOffset)
    {
        BlockOffset = blockOffset;
        LoadTables(entries, blocks);
    }

    /** Validates variant 1 regions, fixes BlockOffset and returns both tables as stored in the file. */
    internal (byte[] Entries, byte[] Blocks) ReadTables()
    {
        if (Variant == 1)
        {
            byte[] prefix = ReadAt(Path, EntryOffset - 20, 20);
            ulong physicalCount = U64(prefix, 4);
            if (U32(prefix, 0) != 0x80809FBD || U32(prefix, 12) != 0x80809EF3
                || physicalCount < (ulong)EntryCount || physicalCount > 8192)
                throw new InvalidDataException($"Invalid entry region: {Path}");
            BlockOffset = checked(EntryOffset + (long)physicalCount * 16 + 32);
            prefix = ReadAt(Path, BlockOffset - 20, 20);
            if (U32(prefix, 0) != 0x80809FBD || U32(prefix, 12) != 0x80809EEE
                || U64(prefix, 4) < (ulong)BlockCount || U64(prefix, 4) > 16384)
                throw new InvalidDataException($"Invalid block region: {Path}");
        }
        return (ReadAt(Path, EntryOffset, checked(EntryCount * 16)), ReadAt(Path, BlockOffset, checked(BlockCount * 48)));
    }

    /** Tag hashes use addition because the package bits overlap the fixed tag base. */
    internal void LoadTables(byte[] entries, byte[] blocks)
    {
        if (entries.Length != checked(EntryCount * 16) || blocks.Length != checked(BlockCount * 48))
            throw new InvalidDataException($"Package table size mismatch: {Path}");
        Entries = TagHeaders.ParseEntryTable(Id, Name, entries);
        Blocks = new BlockRecord[BlockCount];
        for (int i = 0; i < BlockCount; i++)
        {
            int offset = i * 48;
            Blocks[i] = new BlockRecord(U32(blocks, offset),
                checked((int)U32(blocks, offset + 4)), U16(blocks, offset + 8),
                U16(blocks, offset + 10), blocks.AsSpan(offset + 32, 16).ToArray());
        }
    }

    /** Every requested byte must exist; a short read cannot become a valid tag. A device error reopens and retries. */
    internal static byte[] ReadAt(string path, long offset, int count)
    {
        long started = 0;
        for (int attempt = 0; ; attempt++)
        {
            try { return ReadOnce(path, offset, count); }
            catch (IOException error) when (PackageIo.IsDeviceError(error))
            { if (!PackageIo.Backoff(ref started, attempt)) throw new PackageDeviceException(path, error); }
        }
    }

    private static byte[] ReadOnce(string path, long offset, int count)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = RandomAccess.GetLength(file);
        if (offset < 0 || count < 0 || offset > length - count)
            throw new InvalidDataException($"Package range exceeds file: {path}");
        byte[] result = new byte[count];
        int read = 0;
        while (read < count)
        {
            int received = RandomAccess.Read(file, result.AsSpan(read), offset + read);
            if (received == 0) throw new EndOfStreamException(path);
            read += received;
        }
        return result;
    }

    internal static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    internal static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal static ulong U64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    internal static long I64(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
}

internal sealed record BlockRecord(uint Offset, int Size, ushort Patch, ushort Flags, byte[] Tag);
