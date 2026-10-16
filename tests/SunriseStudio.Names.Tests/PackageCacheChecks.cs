using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Packages;

/** Checks the package read cache: replaced packages never return stale bytes and overlay and original reads stay independent. */
internal static class PackageCacheChecks
{
    /** Replacing a package must become visible after invalidation without changing returned buffers. */
    internal static void Synthetic(PackageCatalog catalog, uint tag)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) throw new InvalidDataException("Cache fixture is missing.");
        byte[] before = catalog.ReadOriginal(tag);
        string path = Path.Combine(catalog.PackageDirectory, entry.PackageName + ".pkg");
        byte[] file = File.ReadAllBytes(path);
        var placement = Placement(file, entry.EntryIndex);
        int offset = checked((int)U32(file, placement.BlockRow) + placement.Offset);
        file[offset] ^= 0x5A;
        int blockOffset = checked((int)U32(file, placement.BlockRow));
        int blockSize = checked((int)U32(file, placement.BlockRow + 4));
        SHA1.HashData(file.AsSpan(blockOffset, blockSize)).CopyTo(file, placement.BlockRow + 12);
        int metadata = checked((int)U32(file, 0x110)), metadataSize = checked((int)U32(file, 0x114));
        SHA1.HashData(file.AsSpan(metadata, metadataSize)).CopyTo(file, 0x118);
        string replacement = path + ".replacement";
        File.WriteAllBytes(path, file);
        catalog.InvalidateReadCache();
        File.WriteAllBytes(replacement, file);
        File.Move(replacement, path, true);
        byte[] after = catalog.ReadOriginal(tag);
        if (after[0] != (byte)(before[0] ^ 0x5A) || after.Length != before.Length
            || !after.AsSpan(1).SequenceEqual(before.AsSpan(1)))
            throw new InvalidDataException("Read cache returned the replaced package's old bytes.");
        long reads = catalog.ReadStatistics.BlockReads;
        catalog.ReadOverride = requested => requested == tag ? new byte[] { 9, 8, 7 } : null;
        if (!catalog.Read(tag).SequenceEqual(new byte[] { 9, 8, 7 }) || !catalog.ReadOriginal(tag).SequenceEqual(after)
            || catalog.ReadStatistics.BlockReads != reads)
            throw new InvalidDataException("Cache reuse changed overlay or original reads.");
        catalog.ReadOverride = null;
        Console.WriteLine("package_cache replacement=visible returned_bytes=isolated overlay=independent");
    }

    /** Ciphertext corruption in a temporary owned fixture must still fail authentication. */
    internal static void Owned(PackageCatalog original)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "sunrise-cache-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var entry = original.Entries.First(x => x.PackageId == 0x01CE && x.Size > 0);
            byte[] expected = original.ReadOriginal(entry.TagHash);
            string source = Path.Combine(original.PackageDirectory, entry.PackageName + ".pkg");
            byte[] current = File.ReadAllBytes(source);
            var placement = Placement(current, entry.EntryIndex);
            if ((U16(current, placement.BlockRow + 10) & 2) == 0)
                throw new InvalidDataException("Owned authentication fixture is not encrypted.");
            string packageRoot = Path.Combine(temporary, "packages");
            Directory.CreateDirectory(packageRoot);
            foreach (string file in PackageWriter.GetRequiredPredecessors(source).Append(source).Distinct())
                File.Copy(file, Path.Combine(packageRoot, Path.GetFileName(file)));
            string codec = Path.Combine("bin", "x64", "oo2core_3_win64.dll");
            Directory.CreateDirectory(Path.Combine(temporary, "bin", "x64"));
            File.Copy(Path.Combine(original.GameRoot, codec), Path.Combine(temporary, codec));
            using var catalog = PackageCatalog.Open(temporary, original.KeySource);
            if (!catalog.ReadOriginal(entry.TagHash).SequenceEqual(expected))
                throw new InvalidDataException("Pooled decoding changed the owned fixture bytes.");
            ushort patch = U16(current, placement.BlockRow + 8);
            string stem = entry.PackageName[..entry.PackageName.LastIndexOf('_')];
            string owner = Path.Combine(packageRoot, $"{stem}_{patch}.pkg");
            byte[] corrupt = File.ReadAllBytes(owner);
            corrupt[checked((int)U32(current, placement.BlockRow))] ^= 1;
            File.WriteAllBytes(owner, corrupt);
            catalog.InvalidateReadCache();
            try { catalog.ReadOriginal(entry.TagHash); }
            catch (InvalidDataException error) when (error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal))
            {
                Console.WriteLine("package_cache owned_decode=equal ciphertext_change=rejected");
                return;
            }
            throw new InvalidDataException("Corrupted package data bypassed authentication after cache invalidation.");
        }
        finally
        {
            if (Directory.Exists(temporary) && Path.GetFullPath(temporary)
                .StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(temporary, true);
        }
    }

    private static (int BlockRow, int Offset) Placement(byte[] file, int entry)
    {
        int entries = file[0x1A] == 1 ? checked((int)U32(file, 0x110) + 96) : checked((int)U32(file, 0xB8));
        int blocks = file[0x1A] == 1 ? checked(entries + (int)U64(file, entries - 16) * 16 + 32) : checked((int)U32(file, 0xD4));
        ulong value = U64(file, entries + entry * 16 + 8);
        return (checked(blocks + (int)(value & 0x3FFF) * 48), (int)((value >> 14) & 0x3FFF) * 16);
    }
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
}
