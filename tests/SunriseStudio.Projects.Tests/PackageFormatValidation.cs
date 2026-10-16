using System.Buffers.Binary;
using System.Security.Cryptography;

/** This validator resolves serialized relative pointers without using the production reader. */
internal static class PackageFormatValidation
{
    /** Reads the wide-hash table of a package file. */
    internal static (ulong Hash, uint Tag, uint Class)[] WideHashes(string path)
    {
        Check(path);
        byte[] misc = Region(File.ReadAllBytes(path), 0xF0);
        int start = Array(misc, 0x30, 16, 0x80809D02);
        return Enumerable.Range(0, checked((int)U64(misc, 0x30)))
            .Select(index => (U64(misc, start + index * 16), U32(misc, start + index * 16 + 8),
                U32(misc, start + index * 16 + 12))).ToArray();
    }

    /** Validates the layout of a package file. */
    internal static void Check(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        Check(U16(file, 0) == 38 && U16(file, 2) == 2 && file[0x1A] == 1, "header format");
        Check(U16(file, 4) is >= 0x100 and <= 0x0CFF, "package id");
        Check(U32(file, 0xB0) == 0x800 && U32(file, 0x164) == file.Length, "header size");
        int footer = checked((int)U32(file, 0x160));
        Check(footer % 0x800 == 0 && footer + 0x800 == file.Length && U32(file, footer) == 0xDEADBEEF, "footer");
        Check(file.AsSpan(footer + 4).IndexOfAnyExcept((byte)0) < 0, "footer padding");
        byte[] misc = Region(file, 0xF0), metadata = Region(file, 0x110);
        Check(U64(metadata, 0) == (ulong)metadata.Length && U64(metadata, 8) == 1, "metadata prefix");
        Check(U64(metadata, 0x10) == U32(file, 0xB4) && U64(metadata, 0x20) == U32(file, 0xD0), "table counts");
        int entries = Array(metadata, 0x10, 16, 0x80809EF3);
        int blocks = Array(metadata, 0x20, 48, 0x80809EEE);
        Array(metadata, 0x30, 8, 0x80809A13);
        int count = (int)U32(file, 0xD0);
        for (int i = 0; i < count; i++)
        {
            int row = blocks + i * 48;
            int offset = checked((int)U32(metadata, row)), size = checked((int)U32(metadata, row + 4));
            int patch = U16(metadata, row + 8);
            Check(size is > 0 and <= 0x40000 && patch <= U16(file, 0x20)
                && U16(metadata, row + 10) <= 15 && offset % 0x800 == 0, "block record");
            if (patch == U16(file, 0x20))
            {
                Check(offset >= 0 && offset <= footer - size, "block range");
                Check(SHA1.HashData(file.AsSpan(offset, size)).AsSpan().SequenceEqual(metadata.AsSpan(row + 12, 20)), "block digest");
            }
        }
        for (int i = 0; i < U32(file, 0xB4); i++)
        {
            ulong placement = U64(metadata, entries + i * 16 + 8);
            Check((placement >> 28) == 0 || (placement & 0x3FFF) < (ulong)count, "entry placement");
        }
        Check(misc.Length >= 0x60 && U64(misc, 0) <= (ulong)misc.Length && U64(misc, 8) == 3, "misc prefix");
        int names = Array(misc, 0x10, 16, 0x80809EEC);
        Array(misc, 0x30, 16, 0x80809D02);
        for (int i = 0; i < (int)U64(misc, 0x10); i++)
        {
            int row = names + i * 16;
            long text = checked(row + 8L + (long)U64(misc, row + 8));
            Check(text >= 0 && text < misc.Length && misc.AsSpan((int)text).IndexOf((byte)0) > 0, "named tag string");
        }
    }

    /** Validates that a patch inherits every entry of its source except the replaced ones. */
    internal static void CheckInheritance(string sourcePath, string patchPath, IReadOnlySet<int> replaced)
    {
        byte[] source = File.ReadAllBytes(sourcePath), patch = File.ReadAllBytes(patchPath);
        Check(Region(source, 0xF0).SequenceEqual(Region(patch, 0xF0)), "inherited misc");
        byte[] oldMetadata = Region(source, 0x110), metadata = Region(patch, 0x110);
        int oldEntries = Array(oldMetadata, 0x10, 16, 0x80809EF3), entries = Array(metadata, 0x10, 16, 0x80809EF3);
        int oldBlocks = Array(oldMetadata, 0x20, 48, 0x80809EEE), blocks = Array(metadata, 0x20, 48, 0x80809EEE);
        Check(oldMetadata.AsSpan(oldBlocks, (int)U32(source, 0xD0) * 48)
            .SequenceEqual(metadata.AsSpan(blocks, (int)U32(source, 0xD0) * 48)), "inherited block bytes");
        for (int i = 0; i < U32(source, 0xB4); i++)
        {
            int size = replaced.Contains(i) ? 8 : 16;
            Check(oldMetadata.AsSpan(oldEntries + i * 16, size).SequenceEqual(metadata.AsSpan(entries + i * 16, size)), "inherited entry bytes");
        }
        int pairs = Array(metadata, 0x30, 8, 0x80809A13), oldPairs = Array(oldMetadata, 0x30, 8, 0x80809A13);
        int pairBytes = checked((int)U64(oldMetadata, 0x30) * 8);
        Check(oldMetadata.AsSpan(oldPairs, pairBytes).SequenceEqual(metadata.AsSpan(pairs, pairBytes)), "inherited tag pairs");
        foreach (var (offset, length) in new[] { (6, 1), (8, 24), (0x22, 1), (0xA4, 12), (0xEC, 4), (0x168, 5) })
            Check(source.AsSpan(offset, length).SequenceEqual(patch.AsSpan(offset, length)), "header profile");
    }

    private static byte[] Region(byte[] file, int field)
    {
        int offset = checked((int)U32(file, field)), size = checked((int)U32(file, field + 4));
        byte[] region = file.AsSpan(offset, size).ToArray();
        Check(SHA1.HashData(region).AsSpan().SequenceEqual(file.AsSpan(field + 8, 20)), "region digest");
        return region;
    }

    private static int Array(byte[] region, int field, int stride, uint type)
    {
        ulong count = U64(region, field), relative = U64(region, field + 8);
        if (count == 0) { Check(relative == 0, "empty array pointer"); return 0; }
        int target = checked(field + 0x18 + (int)relative);
        Check(target >= 32 && target <= region.Length, "array pointer");
        Check(U32(region, target - 20) == 0x80809FBD && U64(region, target - 8) == type, "array type");
        ulong capacity = U64(region, target - 16);
        Check(capacity >= count && capacity <= (ulong)((region.Length - target) / stride), "array capacity");
        return target;
    }

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
    private static void Check(bool passed, string surface)
    {
        if (!passed) throw new InvalidDataException("Package format check failed: " + surface);
    }
}
