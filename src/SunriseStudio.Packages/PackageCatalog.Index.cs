using System.Security.Cryptography;
using System.Text;

namespace SunriseStudio.Packages;

// Solforge Phase 05. A warm open of an unchanged installation reads one index file instead of every
// package header, table and misc region. The index is keyed by each file's name, length and write
// time, so any package change (or a different file set) is a miss that rebuilds it on the cold path.
public sealed partial class PackageCatalog
{
    /** Rows CollectNameRows found in the misc regions, in discovery order so ApplyNameRows is reproducible. */
    internal sealed record NameRows(List<NamedTag> Rows, Dictionary<ulong, HashSet<uint>> WideCandidates,
        HashSet<ulong> Claims);

    /** True when the metadata came from the index; false after a cold open or with the disk cache off. */
    public bool OpenedFromIndex { get; private set; }
    /** This installation's index file, beside the data cache, or null when the disk cache is disabled. */
    public string? IndexPath { get; private set; }

    private string? ResolveIndexPath()
    {
        if (!DiskCache.Enabled) return null;
        string parent = Path.GetDirectoryName(DiskCache.DirectoryPath) ?? DiskCache.DirectoryPath;
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameRoot.ToUpperInvariant())))[..16];
        return Path.Combine(parent, "package-index-v1", name + ".bin");
    }

    private static void AddNameRow(List<NamedTag> rows, HashSet<NamedTag> seen, NamedTag row)
    {
        if (seen.Add(row)) rows.Add(row);
    }
}

/** Serialized headers, current-patch tables and name rows; a stale or damaged file is a miss, never an error. */
internal sealed class PackageIndex
{
    private static readonly byte[] Magic = "SFPKIDX2"u8.ToArray(), Trailer = "SFPKEND1"u8.ToArray();
    private sealed record Record(byte[] Header, long BlockOffset, byte[]? Entries, byte[]? Blocks);
    private readonly Dictionary<string, Record> records = new(StringComparer.OrdinalIgnoreCase);

    internal PackageCatalog.NameRows? Names { get; private set; }

    /** Returns null unless every file's name, length, write time and change identity match the index, in the same order.
        The change identity (CacheFileIdentity) catches same-size edits whose write time was restored. */
    internal static PackageIndex? TryLoad(string path, FileInfo[] files)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!ReadExact(reader, 8).AsSpan().SequenceEqual(Magic)) return null;
            int count = reader.ReadInt32();
            if (count != files.Length) return null;
            var index = new PackageIndex();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                long length = reader.ReadInt64(), written = reader.ReadInt64();
                string identity = reader.ReadString();
                var file = files[i];
                if (!name.Equals(file.Name, StringComparison.OrdinalIgnoreCase) || length != file.Length
                    || written != file.LastWriteTimeUtc.Ticks
                    || identity != CacheFileIdentity.Compute([file.FullName], CancellationToken.None)) return null;
                byte[] header = ReadExact(reader, 0x180);
                long blockOffset = reader.ReadInt64();
                int entryLength = reader.ReadInt32();
                byte[]? entries = entryLength < 0 ? null : ReadExact(reader, entryLength);
                int blockLength = reader.ReadInt32();
                byte[]? blocks = blockLength < 0 ? null : ReadExact(reader, blockLength);
                index.records.Add(name, new(header, blockOffset, entries, blocks));
            }
            index.Names = ReadNames(reader);
            return ReadExact(reader, 8).AsSpan().SequenceEqual(Trailer) ? index : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    /** A file the index does not know is read from disk as on a cold open. */
    internal PackageFile Open(string path) =>
        records.TryGetValue(Path.GetFileName(path), out var record) ? new PackageFile(path, record.Header) : new PackageFile(path);

    /** Loads a package's entry and block tables from the index; false when the index lacks them. */
    internal bool LoadTables(PackageFile package)
    {
        if (!records.TryGetValue(Path.GetFileName(package.Path), out var record)
            || record.Entries is null || record.Blocks is null) return false;
        package.LoadTables(record.Entries, record.Blocks, record.BlockOffset);
        return true;
    }

    /** Writes through a temporary file; a failed write leaves the previous index or none. */
    internal static void TryWrite(string path, FileInfo[] files, IReadOnlyList<PackageFile> all,
        IReadOnlyCollection<PackageFile> current, PackageCatalog.NameRows names)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var byName = all.ToDictionary(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase);
            var withTables = new HashSet<PackageFile>(current);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(files.Length);
                foreach (var file in files)
                {
                    var package = byName[file.Name];
                    writer.Write(file.Name);
                    writer.Write(file.Length);
                    writer.Write(file.LastWriteTimeUtc.Ticks);
                    writer.Write(CacheFileIdentity.Compute([file.FullName], CancellationToken.None));
                    writer.Write(package.Header);
                    if (withTables.Contains(package))
                    {
                        // Tables are re-read here rather than retained by every PackageFile after parsing.
                        var (entries, blocks) = package.ReadTables();
                        writer.Write(package.BlockOffset);
                        writer.Write(entries.Length); writer.Write(entries);
                        writer.Write(blocks.Length); writer.Write(blocks);
                    }
                    else
                    {
                        writer.Write(package.BlockOffset); writer.Write(-1); writer.Write(-1);
                    }
                }
                WriteNames(writer, names);
                writer.Write(Trailer);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void WriteNames(BinaryWriter writer, PackageCatalog.NameRows names)
    {
        writer.Write(names.Rows.Count);
        foreach (var row in names.Rows)
        {
            writer.Write(row.TagHash); writer.Write(row.ClassHash); writer.Write(row.Name);
            writer.Write(row.SourcePackage); writer.Write(row.SourcePackageId); writer.Write(row.SourcePatchId);
            writer.Write(row.SourceBuild); writer.Write(row.SourceGroup); writer.Write(row.IsCurrentSource);
        }
        writer.Write(names.WideCandidates.Count);
        foreach (var (wide, tags) in names.WideCandidates)
        {
            writer.Write(wide);
            writer.Write(tags.Count);
            foreach (uint tag in tags) writer.Write(tag);
        }
        writer.Write(names.Claims.Count);
        foreach (ulong claim in names.Claims) writer.Write(claim);
    }

    private static PackageCatalog.NameRows ReadNames(BinaryReader reader)
    {
        int rowCount = reader.ReadInt32();
        var rows = new List<NamedTag>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            uint tag = reader.ReadUInt32(), classHash = reader.ReadUInt32();
            string name = reader.ReadString();
            // Initializer members read in textual order, matching WriteNames.
            rows.Add(new NamedTag(tag, classHash, name)
            {
                SourcePackage = reader.ReadString(),
                SourcePackageId = reader.ReadUInt16(),
                SourcePatchId = reader.ReadUInt16(),
                SourceBuild = reader.ReadUInt32(),
                SourceGroup = reader.ReadUInt64(),
                IsCurrentSource = reader.ReadBoolean()
            });
        }
        int wideCount = reader.ReadInt32();
        var wide = new Dictionary<ulong, HashSet<uint>>(wideCount);
        for (int i = 0; i < wideCount; i++)
        {
            ulong key = reader.ReadUInt64();
            int tagCount = reader.ReadInt32();
            var tags = new HashSet<uint>(tagCount);
            for (int j = 0; j < tagCount; j++) tags.Add(reader.ReadUInt32());
            wide.Add(key, tags);
        }
        int claimCount = reader.ReadInt32();
        var claims = new HashSet<ulong>(claimCount);
        for (int i = 0; i < claimCount; i++) claims.Add(reader.ReadUInt64());
        return new(rows, wide, claims);
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException("The package index is truncated.");
        return bytes;
    }
}
