using System.Security.Cryptography;

namespace SunriseStudio.Packages;

/** One package file's SHA-1 audit; BadBlocks lists block indices whose stored bytes do not match their record. */
public sealed record PackageDigestReport(string Path, bool MiscValid, bool MetadataValid, int BlocksChecked,
    int BlocksSkipped, IReadOnlyList<int> BadBlocks)
{
    public bool Valid => MiscValid && MetadataValid && BadBlocks.Count == 0;
}

/**
 * Solforge Phase 09: verifies the SHA-1 digests PackageWriter writes (Studio.md §5.1, §5.3): the misc region
 * at header 0xF8, the metadata region at 0x118, and each block's stored bytes at record +0x0C. Blocks whose
 * record names another patch generation are checked in that sibling file when it exists, else skipped.
 */
public static class PackageDigests
{
    public static PackageDigestReport Verify(string packagePath, bool ownBlocksOnly = true)
    {
        string path = Path.GetFullPath(packagePath);
        byte[] header = PackageFile.ReadAt(path, 0, 0x180);
        var file = new PackageFile(path, header);
        byte[] misc = PackageFile.ReadAt(path, PackageFile.U32(header, 0xF0), checked((int)PackageFile.U32(header, 0xF4)));
        byte[] metadata = PackageFile.ReadAt(path, PackageFile.U32(header, 0x110), checked((int)PackageFile.U32(header, 0x114)));
        bool miscValid = SHA1.HashData(misc).AsSpan().SequenceEqual(header.AsSpan(0xF8, 20));
        bool metadataValid = SHA1.HashData(metadata).AsSpan().SequenceEqual(header.AsSpan(0x118, 20));
        byte[] blocks = file.ReadTables().Blocks;
        string stem = file.Name.Contains('_') ? file.Name[..file.Name.LastIndexOf('_')] : file.Name;
        string directory = Path.GetDirectoryName(path)!;
        var bad = new List<int>();
        int checkedCount = 0, skipped = 0;
        for (int i = 0; i < file.BlockCount; i++)
        {
            int row = i * 48;
            ushort patch = PackageFile.U16(blocks, row + 8);
            string source = patch == file.Patch ? path : Path.Combine(directory, $"{stem}_{patch}.pkg");
            if (patch != file.Patch && (ownBlocksOnly || !File.Exists(source))) { skipped++; continue; }
            byte[] stored = PackageFile.ReadAt(source, PackageFile.U32(blocks, row), checked((int)PackageFile.U32(blocks, row + 4)));
            if (!SHA1.HashData(stored).AsSpan().SequenceEqual(blocks.AsSpan(row + 12, 20))) bad.Add(i);
            checkedCount++;
        }
        return new PackageDigestReport(path, miscValid, metadataValid, checkedCount, skipped, bad);
    }
}
