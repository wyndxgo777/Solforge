using System.Buffers.Binary;
using static SunriseStudio.Packages.PackageFile;

namespace SunriseStudio.Packages;

/** Root tag with the manifest tag that lists its entries. */
public sealed record PackageRootManifest(uint RootTag, uint ManifestTag);

/** Manifest half of PackageWriter: root manifest rows for new packages. */
public static partial class PackageWriter
{
    private const uint RootManifestRowClass = 0x80809A13;

    /** Manifest-root entries resolve through the package metadata's sorted tag pairs. */
    public static IReadOnlyList<PackageRootManifest> ReadRootManifests(string packagePath)
    {
        byte[] header = ReadAt(packagePath, 0, HeaderSize);
        if (header[0x1A] != 1) throw new NotSupportedException("Root manifests need pre-BL package metadata.");
        byte[] metadata = ReadAt(packagePath, U32(header, 0x110), checked((int)U32(header, 0x114)));
        if (metadata.Length < 0x60) throw new InvalidDataException("Package metadata is truncated.");
        ulong count = U64(metadata, 0x30);
        if (count == 0) return [];
        long array = checked(0x38 + BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(0x38)));
        if (count > 1_048_576 || array < 4 || array > metadata.Length - 16
            || U32(metadata, (int)array - 4) != ArrayMarker || U64(metadata, (int)array) < count
            || U32(metadata, (int)array + 8) != RootManifestRowClass
            || count * 8 > (ulong)(metadata.Length - array - 16))
            throw new InvalidDataException("Package root manifest pairs are invalid.");
        var rows = new List<PackageRootManifest>();
        for (int i = 0; i < (int)count; i++)
        {
            int at = checked((int)array + 16 + i * 8);
            var row = new PackageRootManifest(U32(metadata, at), U32(metadata, at + 4));
            if (rows.Count > 0 && rows[^1].RootTag > row.RootTag)
                throw new InvalidDataException("Package root manifest pairs must be sorted.");
            if (rows.Count > 0 && rows[^1].RootTag == row.RootTag)
            {
                if (rows[^1].ManifestTag != row.ManifestTag)
                    throw new InvalidDataException("A package root has conflicting manifest targets.");
                continue;
            }
            rows.Add(row);
        }
        return rows;
    }

    /** Every new root keeps its root flag and must have exactly one authored manifest. */
    private static byte[] AppendRootManifestMap(byte[] metadata, ushort package,
        IReadOnlyList<AuthoredPackageEntry> entries)
    {
        uint Tag(int index) => checked(0x80800000u + ((uint)package << 13) + (uint)index);
        var roots = Enumerable.Range(0, entries.Count)
            .Where(index => (entries[index].TypeInfo & 0xF000) == 0x2000).Select(Tag).ToHashSet();
        var manifests = new SortedDictionary<uint, uint>();
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.Reference != PackageManifest.ClassId) continue;
            byte[] data = entry.Data;
            var manifest = PackageManifest.Read(data);
            if (data.Length < 48 || U64(data, 0) != (ulong)data.Length || U32(data, 8) != Tag(index))
                throw new InvalidDataException("An authored manifest has an invalid size or identity.");
            uint root = U32(data, 12);
            if (!roots.Contains(root) || !manifests.TryAdd(root, Tag(index)))
                throw new InvalidDataException("Each authored manifest needs one unique root in its package.");
            if (!manifest.Lists(root) || !manifest.Lists(Tag(index)))
                throw new InvalidDataException("A native manifest must include its root and its own entry.");
        }
        if (!roots.SetEquals(manifests.Keys))
            throw new InvalidDataException("A new manifest-root entry has no authored dependency manifest.");
        if (roots.Count == 0) return metadata;
        int dataStart = checked(((metadata.Length + 15) & ~15) + 32);
        byte[] output = new byte[checked(dataStart + manifests.Count * 8)];
        metadata.CopyTo(output, 0);
        ArrayPrefix(output, dataStart, manifests.Count, RootManifestRowClass);
        Put64(output, 0x30, (ulong)manifests.Count);
        Put64(output, 0x38, (ulong)(dataStart - 0x48));
        int rowAt = dataStart;
        foreach (var (root, manifest) in manifests)
        { Put32(output, rowAt, root); Put32(output, rowAt + 4, manifest); rowAt += 8; }
        Put64(output, 0, (ulong)output.Length);
        return output;
    }
}
