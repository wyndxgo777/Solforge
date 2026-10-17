/**
 * File: Compact in-memory index of every retail tag (8.6M rows): reference/class, file type, subtype and size by tag,
 * O(1) through the tag arithmetic. Built once from the extraction's tags.tsv + tags_extra.tsv and cached as a binary
 * file (about 90 MB) that loads in a second or two.
 */
using System.Globalization;
using System.Text;

namespace Solforge.Retail;

/** One index row. Reference is the class hash for file types 8 and 16, the data tag for buffer headers (type 32). */
public readonly record struct RetailEntry(uint Tag, uint Reference, byte Type, byte Subtype, uint Size, string Package)
{
    /** True when Reference is a struct class (file types 8 and 16). */
    public bool IsStruct => Type is 8 or 16;
    /** The class hash for struct tags, 0 otherwise. */
    public uint Class => IsStruct ? Reference : 0;
}

/** The whole retail tag index; thread-safe for reads once built. */
public sealed class RetailIndex
{
    private const uint Magic = 0x58495253; // "SRIX"
    private const int Version = 1;

    private sealed class Table(string name, int count)
    {
        public readonly string Name = name;
        public readonly uint[] Reference = new uint[count];
        public readonly byte[] Type = new byte[count];
        public readonly byte[] Subtype = new byte[count];
        public readonly uint[] Size = new uint[count];
        /** 1 when the row came from the TSVs (a tag with a gap before it has 0). */
        public readonly byte[] Present = new byte[count];
    }

    private readonly Dictionary<ushort, Table> tables;
    private readonly Lazy<Dictionary<uint, int>> classCounts;

    private RetailIndex(Dictionary<ushort, Table> tables)
    {
        this.tables = tables;
        Count = tables.Values.Sum(table => table.Present.Count(flag => flag != 0));
        classCounts = new(CountClasses);
    }

    /** Rows indexed. */
    public long Count { get; }
    /** Package ids, sorted. */
    public IEnumerable<ushort> Packages => tables.Keys.Order();
    /** The .pkg file name of a package id, or null. */
    public string? PackageName(ushort package) => tables.TryGetValue(package, out var table) ? table.Name : null;
    /** Entries in a package (the highest index + 1). */
    public int PackageCount(ushort package) => tables.TryGetValue(package, out var table) ? table.Reference.Length : 0;

    /** The row of a tag, or null when the tag is not in the index. */
    public RetailEntry? Find(uint tag)
    {
        if (!RetailTag.Plausible(tag) || !tables.TryGetValue(RetailTag.Package(tag), out var table)) return null;
        int index = RetailTag.Index(tag);
        if (index >= table.Reference.Length || table.Present[index] == 0) return null;
        return new RetailEntry(tag, table.Reference[index], table.Type[index], table.Subtype[index], table.Size[index], table.Name);
    }

    /** Whether the tag is indexed. */
    public bool Contains(uint tag) => Find(tag) is not null;

    /** The class of a struct tag (types 8 and 16), else 0. */
    public uint ClassOf(uint tag) => Find(tag) is { IsStruct: true } entry ? entry.Reference : 0;

    /** Top-level tags per struct class (types 8 and 16). */
    public IReadOnlyDictionary<uint, int> ClassCounts => classCounts.Value;

    /** Every row matching the filters, in tag order; a null filter matches all. */
    public IEnumerable<RetailEntry> Search(uint? reference = null, int? type = null, int? subtype = null, string? package = null)
    {
        foreach (var (id, table) in tables.OrderBy(pair => pair.Key))
        {
            if (package is { Length: > 0 } && !table.Name.Contains(package, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(id.ToString("x4", CultureInfo.InvariantCulture), package, StringComparison.OrdinalIgnoreCase)) continue;
            for (int i = 0; i < table.Reference.Length; i++)
            {
                if (table.Present[i] == 0) continue;
                if (reference is { } r && table.Reference[i] != r) continue;
                if (type is { } t && table.Type[i] != t) continue;
                if (subtype is { } s && table.Subtype[i] != s) continue;
                yield return new RetailEntry(RetailTag.Make(id, i), table.Reference[i], table.Type[i], table.Subtype[i], table.Size[i], table.Name);
            }
        }
    }

    private Dictionary<uint, int> CountClasses()
    {
        var counts = new Dictionary<uint, int>();
        foreach (var table in tables.Values)
            for (int i = 0; i < table.Reference.Length; i++)
                if (table.Present[i] != 0 && table.Type[i] is 8 or 16)
                    counts[table.Reference[i]] = counts.GetValueOrDefault(table.Reference[i]) + 1;
        return counts;
    }

    /** The TSVs of an extraction root that exist (index\tags.tsv, index\tags_extra.tsv). */
    public static IReadOnlyList<string> SourceFiles(string root) =>
        new[] { Path.Combine(root, "index", "tags.tsv"), Path.Combine(root, "index", "tags_extra.tsv") }.Where(File.Exists).ToList();

    /**
     * Loads the cached index when it matches the TSVs (same sizes and times), else builds it from them and writes the
     * cache (best effort: an unwritable cache folder only costs the rebuild next time). progress gets rows parsed.
     */
    public static RetailIndex Open(string root, string cacheFolder, Action<long>? progress = null)
    {
        var sources = SourceFiles(root);
        if (sources.Count == 0) throw new FileNotFoundException($"No index\\tags.tsv under {root}.");
        string signature = Signature(sources);
        string cache = Path.Combine(cacheFolder, "retail_index_" + RetailOptions.Build + ".bin");
        if (File.Exists(cache))
        {
            try { if (Load(cache, signature) is { } loaded) return loaded; }
            catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException or UnauthorizedAccessException) { }
        }
        var index = Build(sources, progress);
        try { Directory.CreateDirectory(cacheFolder); index.Save(cache, signature); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return index;
    }

    /** Builds an index from TSV files with the columns tag, reference, file_type, subtype, size, package. */
    public static RetailIndex Build(IEnumerable<string> files, Action<long>? progress = null)
    {
        var rows = new Dictionary<ushort, List<(int Index, uint Reference, byte Type, byte Subtype, uint Size)>>();
        var names = new Dictionary<ushort, string>();
        long parsed = 0;
        foreach (string file in files)
        {
            using var reader = new StreamReader(file, Encoding.UTF8, false, 1 << 20);
            string? line = reader.ReadLine(); // header
            while ((line = reader.ReadLine()) is not null)
            {
                if (!TryParse(line.AsSpan().TrimEnd('\r'), out uint tag, out uint reference, out byte type, out byte subtype, out uint size, out var package)) continue;
                ushort id = RetailTag.Package(tag);
                if (!rows.TryGetValue(id, out var list)) { rows[id] = list = []; names[id] = package.ToString(); }
                list.Add((RetailTag.Index(tag), reference, type, subtype, size));
                if (++parsed % 500_000 == 0) progress?.Invoke(parsed);
            }
        }
        var tables = new Dictionary<ushort, Table>();
        foreach (var (id, list) in rows)
        {
            var table = new Table(names[id], list.Max(row => row.Index) + 1);
            foreach (var row in list)
            {
                table.Reference[row.Index] = row.Reference; table.Type[row.Index] = row.Type; table.Subtype[row.Index] = row.Subtype;
                table.Size[row.Index] = row.Size; table.Present[row.Index] = 1;
            }
            tables[id] = table;
        }
        progress?.Invoke(parsed);
        return new RetailIndex(tables);
    }

    private static bool TryParse(ReadOnlySpan<char> line, out uint tag, out uint reference, out byte type, out byte subtype, out uint size, out ReadOnlySpan<char> package)
    {
        tag = reference = size = 0; type = subtype = 0; package = default;
        Span<Range> parts = stackalloc Range[7];
        if (line.Split(parts, '\t') < 6) return false;
        return uint.TryParse(line[parts[0]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out tag) && RetailTag.Plausible(tag)
            && uint.TryParse(line[parts[1]], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out reference)
            && byte.TryParse(line[parts[2]], NumberStyles.Integer, CultureInfo.InvariantCulture, out type)
            && byte.TryParse(line[parts[3]], NumberStyles.Integer, CultureInfo.InvariantCulture, out subtype)
            && uint.TryParse(line[parts[4]], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
            && (package = line[parts[5]].Trim()).Length > 0;
    }

    private static string Signature(IEnumerable<string> files) =>
        string.Join("|", files.Select(file => { var info = new FileInfo(file); return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"; }));

    private void Save(string path, string signature)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(signature); writer.Write(tables.Count);
            foreach (var (id, table) in tables)
            {
                writer.Write(id); writer.Write(table.Name); writer.Write(table.Reference.Length);
                foreach (uint value in table.Reference) writer.Write(value);
                writer.Write(table.Type); writer.Write(table.Subtype);
                foreach (uint value in table.Size) writer.Write(value);
                writer.Write(table.Present);
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    private static RetailIndex? Load(string path, string signature)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version || reader.ReadString() != signature) return null;
        int count = reader.ReadInt32();
        var tables = new Dictionary<ushort, Table>(count);
        for (int t = 0; t < count; t++)
        {
            ushort id = reader.ReadUInt16();
            string name = reader.ReadString();
            int rows = reader.ReadInt32();
            if (rows is < 0 or > 8192) throw new InvalidDataException("Bad retail index cache.");
            var table = new Table(name, rows);
            for (int i = 0; i < rows; i++) table.Reference[i] = reader.ReadUInt32();
            Fill(reader, table.Type); Fill(reader, table.Subtype);
            for (int i = 0; i < rows; i++) table.Size[i] = reader.ReadUInt32();
            Fill(reader, table.Present);
            tables[id] = table;
        }
        return new RetailIndex(tables);
    }

    private static void Fill(BinaryReader reader, byte[] target)
    {
        int read = 0;
        while (read < target.Length)
        {
            int got = reader.Read(target, read, target.Length - read);
            if (got <= 0) throw new EndOfStreamException();
            read += got;
        }
    }
}
