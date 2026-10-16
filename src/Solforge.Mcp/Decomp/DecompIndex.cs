using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Solforge.Mcp.Decomp;

/** One decompiled function: where its C lives (file, byte offset, byte length) and what Ghidra printed as its signature. */
public sealed record DecompFunction(uint Rva, uint Size, string Signature, int File, long Offset, int Length);

/**
 * The decompiled-code layer over the reverse-engineering folder (04-reverse-engineering\analysis): every function in
 * decomp\all\decomp_*.c (one "// ==== RVA xxxxxxxx ====" block each) plus the single-function decomp\func_*.c files,
 * with sizes from full\functions.csv, names from NOTES.md tables, listings\*_RVA.asm and the user's solforge-names.csv,
 * direct calls, data references (DAT_/UNK_/PTR_/LAB_ names are RVAs) and Tiger tag class constants (0x8080xxxx) per
 * function. The parsed index is cached under %LOCALAPPDATA%\Solforge\decomp and rebuilt when a source file changes.
 * Thread-safe once built; build with Load (seconds the first time, a fraction of a second from the cache).
 */
public sealed class DecompIndex
{
    private const int CacheVersion = 1;
    public const string NamesFile = "solforge-names.csv";

    public string Root { get; }
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<DecompFunction> Functions => functions;
    public IReadOnlyList<uint> Failed { get; }

    private readonly DecompFunction[] functions;
    private readonly Dictionary<uint, int> byRva;
    private readonly uint[][] calls, refs, tagClasses;
    private readonly Dictionary<uint, List<int>> callers = [], users = [], classUsers = [];
    private readonly HashSet<uint> starts;
    private volatile Dictionary<uint, (string Name, string Source)> names;
    private BinaryImage? image;
    private readonly object imageLock = new();

    private DecompIndex(string root, string[] files, DecompFunction[] functions, uint[][] calls, uint[][] refs, uint[][] tagClasses,
        uint[] failed, HashSet<uint> starts)
    {
        Root = root; Files = files; this.functions = functions; this.calls = calls; this.refs = refs; this.tagClasses = tagClasses;
        Failed = failed; this.starts = starts;
        byRva = new Dictionary<uint, int>(functions.Length);
        for (int index = 0; index < functions.Length; index++)
        {
            byRva[functions[index].Rva] = index;
            foreach (uint target in calls[index]) Add(callers, target, index);
            foreach (uint target in refs[index]) Add(users, target, index);
            foreach (uint type in tagClasses[index]) Add(classUsers, type, index);
        }
        names = LoadNames(root);
        static void Add(Dictionary<uint, List<int>> map, uint key, int index)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            if (list.Count == 0 || list[^1] != index) list.Add(index);
        }
    }

    // ---- discovery ----

    /** The analysis folder: SOLFORGE_DECOMP, else the first "04-reverse-engineering\analysis" (or "analysis") with decomp\all above a start folder. */
    public static string? Locate(IEnumerable<string?> starts)
    {
        if (Environment.GetEnvironmentVariable("SOLFORGE_DECOMP") is { Length: > 0 } set && Directory.Exists(set)) return Path.GetFullPath(set);
        foreach (string? start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            string? folder;
            try { folder = Directory.Exists(start) ? Path.GetFullPath(start) : null; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            for (int depth = 0; folder is not null && depth < 12; depth++, folder = Directory.GetParent(folder)?.FullName)
            {
                foreach (string candidate in new[] { Path.Combine(folder, "04-reverse-engineering", "analysis"), Path.Combine(folder, "analysis"), folder })
                    if (Directory.Exists(Path.Combine(candidate, "decomp"))) return candidate;
            }
        }
        return null;
    }

    private static readonly Dictionary<string, DecompIndex> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    /** The index for an analysis folder, built once per process (and cached on disk). */
    public static async Task<DecompIndex> LoadAsync(string root, CancellationToken cancel)
    {
        root = Path.GetFullPath(root);
        await LoadGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (Loaded.TryGetValue(root, out var ready) && ready.Stamp() == ready.stamp) return ready;
            var index = await Task.Run(() => Load(root, CachePath(root), cancel), cancel).ConfigureAwait(false);
            Loaded[root] = index;
            return index;
        }
        finally { LoadGate.Release(); }
    }

    private string stamp = "";

    private static string CachePath(string root)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        string folder = Environment.GetEnvironmentVariable("SOLFORGE_DECOMP_CACHE") is { Length: > 0 } cache ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "decomp");
        return Path.Combine(folder, $"index-{key}.bin");
    }

    private static string[] SourceFiles(string root)
    {
        var files = new List<string>();
        string all = Path.Combine(root, "decomp", "all");
        if (Directory.Exists(all)) files.AddRange(Directory.GetFiles(all, "decomp_*.c").Order(StringComparer.OrdinalIgnoreCase));
        string single = Path.Combine(root, "decomp");
        if (Directory.Exists(single)) files.AddRange(Directory.GetFiles(single, "func_*.c").Order(StringComparer.OrdinalIgnoreCase));
        return [.. files];
    }

    private static string StampOf(string root, IEnumerable<string> files)
    {
        var text = new StringBuilder().Append(CacheVersion).Append('|');
        foreach (string file in files.Append(Path.Combine(root, "full", "functions.csv")))
        {
            var info = new FileInfo(file);
            if (info.Exists) text.Append(Path.GetFileName(file)).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('|');
        }
        return text.ToString();
    }

    private string Stamp() => StampOf(Root, SourceFiles(Root));

    /** Builds (or reads from cachePath) the index for an analysis folder. */
    public static DecompIndex Load(string root, string cachePath, CancellationToken cancel = default)
    {
        root = Path.GetFullPath(root);
        string[] files = SourceFiles(root);
        string stamp = StampOf(root, files);
        var index = TryReadCache(root, cachePath, stamp, files) ?? Build(root, files, cancel);
        index.stamp = stamp;
        index.cachePath = cachePath;
        if (index.fromCache) return index;
        try { WriteCache(index, cachePath, stamp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return index;
    }

    private bool fromCache;

    private static DecompIndex Build(string root, string[] files, CancellationToken cancel)
    {
        var sizes = ReadSizes(Path.Combine(root, "full", "functions.csv"));
        var found = new Dictionary<uint, (DecompFunction Function, uint[] Calls, uint[] Refs, uint[] Classes)>();
        var failed = new List<uint>();
        ReadOnlySpan<byte> marker = "// ==== RVA "u8;
        for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            cancel.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(files[fileIndex]);
            string name = Path.GetFileName(files[fileIndex]);
            if (name.StartsWith("func_", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryHex(name.AsSpan(5, Math.Min(8, name.Length - 5)), out uint rva) || found.ContainsKey(rva)) continue;
                if (bytes.AsSpan().IndexOf("decompile failed"u8) >= 0 && bytes.AsSpan().IndexOf("{"u8) < 0) { failed.Add(rva); continue; }
                found[rva] = Parse(rva, bytes, 0, bytes.Length, fileIndex, sizes);
                continue;
            }
            var span = bytes.AsSpan();
            int at = span.IndexOf(marker);
            while (at >= 0)
            {
                int next = span[(at + marker.Length)..].IndexOf(marker);
                int end = next < 0 ? bytes.Length : at + marker.Length + next;
                if (TryHex(Encoding.ASCII.GetString(bytes, at + marker.Length, Math.Min(8, bytes.Length - at - marker.Length)), out uint rva))
                    found[rva] = Parse(rva, bytes, at, end - at, fileIndex, sizes);
                at = next < 0 ? -1 : end;
            }
        }
        string failedList = Path.Combine(root, "decomp", "all", "FAILED.txt");
        if (File.Exists(failedList))
            foreach (string line in File.ReadLines(failedList))
                if (!line.StartsWith('#') && TryHex(line.Trim(), out uint rva) && !found.ContainsKey(rva)) failed.Add(rva);
        var ordered = found.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        var startSet = new HashSet<uint>(sizes.Keys);
        startSet.UnionWith(found.Keys);
        return new DecompIndex(root, files, [.. ordered.Select(row => row.Function)], [.. ordered.Select(row => row.Calls)],
            [.. ordered.Select(row => row.Refs)], [.. ordered.Select(row => row.Classes)], [.. failed.Distinct().Order()], startSet);
    }

    private static Dictionary<uint, uint> ReadSizes(string csv)
    {
        var sizes = new Dictionary<uint, uint>();
        if (!File.Exists(csv)) return sizes;
        foreach (string line in File.ReadLines(csv))
        {
            string[] cells = line.Split(',');
            if (cells.Length >= 3 && TryHex(cells[0], out uint start) && uint.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint size))
                sizes[start] = size;
        }
        return sizes;
    }

    /** Signature, calls, data references and tag class constants of one function's C text. */
    internal static (DecompFunction Function, uint[] Calls, uint[] Refs, uint[] Classes) Parse(uint rva, byte[] bytes, int offset, int length,
        int file, IReadOnlyDictionary<uint, uint> sizes)
    {
        var body = bytes.AsSpan(offset, length);
        var calls = new SortedSet<uint>();
        var refs = new SortedSet<uint>();
        var classes = new SortedSet<uint>();
        for (int i = 0; i < body.Length; i++)
        {
            byte c = body[i];
            bool boundary = i == 0 || !IsIdent(body[i - 1]);
            if (!boundary) continue;
            if (c == '0' && i + 1 < body.Length && (body[i + 1] | 0x20) == 'x')
            {
                int j = i + 2;
                while (j < body.Length && IsHex(body[j])) j++;
                if (j - i - 2 is >= 1 and <= 8 && TryHex(Encoding.ASCII.GetString(body[(i + 2)..j]), out uint value))
                {
                    bool negative = i > 0 && body[i - 1] == '-';
                    uint type = negative ? unchecked(0u - value) : value;
                    if (IsTagClass(type)) classes.Add(type);
                }
                i = j - 1;
                continue;
            }
            if (!IsIdentStart(c)) continue;
            int end = i;
            while (end < body.Length && IsIdent(body[end])) end++;
            if (end - i >= 12)
            {
                var ident = body[i..end];
                if (ident[^9] == '_' && TryHex(Encoding.ASCII.GetString(ident[^8..]), out uint target))
                {
                    bool code = ident.StartsWith("FUN_"u8) || ident.StartsWith("func_0x"u8) || ident.StartsWith("thunk_FUN_"u8);
                    if (code) { if (target != rva) calls.Add(target); }
                    else refs.Add(target);
                }
                else if (ident.Length >= 15 && ident[^11] == '_' && ident[^10] == '0' && ident[^9] == 'x' && ident.StartsWith("func_0x"u8)
                    && TryHex(Encoding.ASCII.GetString(ident[^8..]), out uint callee) && callee != rva)
                    calls.Add(callee);
            }
            i = end - 1;
        }
        string signature = Signature(Encoding.UTF8.GetString(body[..Math.Min(body.Length, 4096)]));
        uint size = sizes.TryGetValue(rva, out uint known) ? known : 0;
        return (new DecompFunction(rva, size, signature, file, offset, length), [.. calls], [.. refs], [.. classes]);
    }

    private static string Signature(string head)
    {
        var parts = new List<string>();
        foreach (string raw in head.Split('\n'))
        {
            string line = raw.Trim();
            if (parts.Count == 0 && (line.Length == 0 || line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith('*') || !line.Contains('('))) continue;
            if (line == "{") break;
            parts.Add(line);
            if (line.Contains(')') || parts.Count >= 4) break;
        }
        return string.Join(' ', parts);
    }

    public static bool IsTagClass(uint value) => value is >= 0x80800000 and <= 0x8081FFFF;
    private static bool IsIdentStart(byte c) => c is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (byte)'_';
    private static bool IsIdent(byte c) => IsIdentStart(c) || c is >= (byte)'0' and <= (byte)'9';
    private static bool IsHex(byte c) => c is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');

    public static bool TryHex(ReadOnlySpan<char> text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && text.Length is > 0 and <= 8;
    }

    // ---- cache ----

    private static void WriteCache(DecompIndex index, string path, string stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            writer.Write(stamp);
            writer.Write(index.functions.Length);
            for (int i = 0; i < index.functions.Length; i++)
            {
                var function = index.functions[i];
                writer.Write(function.Rva); writer.Write(function.Size); writer.Write(function.Signature);
                writer.Write(function.File); writer.Write(function.Offset); writer.Write(function.Length);
                Array(index.calls[i]); Array(index.refs[i]); Array(index.tagClasses[i]);
            }
            Array([.. index.Failed]);
            Array([.. index.starts]);
            void Array(uint[] values) { writer.Write(values.Length); foreach (uint value in values) writer.Write(value); }
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static DecompIndex? TryReadCache(string root, string path, string stamp, string[] files)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadString() != stamp) return null;
            int count = reader.ReadInt32();
            var functions = new DecompFunction[count];
            var calls = new uint[count][]; var refs = new uint[count][]; var classes = new uint[count][];
            for (int i = 0; i < count; i++)
            {
                functions[i] = new DecompFunction(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadString(), reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt32());
                calls[i] = Array(); refs[i] = Array(); classes[i] = Array();
            }
            uint[] failed = Array();
            var starts = new HashSet<uint>(Array());
            return new DecompIndex(root, files, functions, calls, refs, classes, failed, starts) { fromCache = true };
            uint[] Array()
            {
                int length = reader.ReadInt32();
                var values = new uint[length];
                for (int i = 0; i < length; i++) values[i] = reader.ReadUInt32();
                return values;
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or FormatException or ArgumentException) { return null; }
    }

    // ---- names ----

    private static Dictionary<uint, (string Name, string Source)> LoadNames(string root)
    {
        var names = new Dictionary<uint, (string, string)>();
        string listings = Path.Combine(root, "listings");
        if (Directory.Exists(listings))
            foreach (string file in Directory.GetFiles(listings, "*.asm"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int cut = stem.LastIndexOf('_');
                if (cut > 0 && TryHex(stem.AsSpan(cut + 1), out uint rva)) names[rva] = (stem[..cut], "listings");
            }
        string notes = Path.Combine(root, "NOTES.md");
        if (File.Exists(notes))
            foreach (string line in File.ReadLines(notes))
            {
                // | `0xE2D610` | `state_name` | `const char* (int state)` | ... |
                string[] cells = line.Split('|');
                if (cells.Length < 4) continue;
                string first = cells[1].Trim().Trim('`'), second = cells[2].Trim().Trim('`');
                if (!first.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !TryHex(first, out uint rva)) continue;
                if (second.Length == 0 || second.StartsWith('(') || second.Contains(' ')) continue;
                names[rva] = (second, "NOTES.md");
            }
        string user = Path.Combine(root, NamesFile);
        if (File.Exists(user))
            foreach (string line in File.ReadLines(user))
            {
                int comma = line.IndexOf(',');
                if (comma > 0 && TryHex(line.AsSpan(0, comma), out uint rva) && line[(comma + 1)..].Trim() is { Length: > 0 } name) names[rva] = (name, NamesFile);
            }
        return names;
    }

    /** Re-reads names (after a rename or an edit to NOTES.md). */
    public void ReloadNames() => names = LoadNames(Root);

    /** Saves a user name for a function to solforge-names.csv in the analysis folder (later lines win). */
    public void Rename(uint rva, string name)
    {
        name = name.Replace(',', '_').Replace('\n', ' ').Replace('\r', ' ').Trim();
        File.AppendAllText(Path.Combine(Root, NamesFile), $"{rva:X8},{name}{Environment.NewLine}");
        ReloadNames();
    }

    public string? NameOf(uint rva) => names.TryGetValue(rva, out var entry) ? entry.Name
        : virtualNames.TryGetValue(rva, out var owner) ? owner.Name : null;
    public string? NameSource(uint rva) => names.TryGetValue(rva, out var entry) ? entry.Source
        : virtualNames.ContainsKey(rva) ? "class" : null;
    /** Virtual functions named Class::vfN by the class layer (only once Classes has loaded; user names win). */
    public int VirtualNameCount => virtualNames.Count;
    public string Label(uint rva) => NameOf(rva) is { } name ? $"{name} (0x{rva:X8})" : $"FUN_{rva:X8}";
    public IEnumerable<KeyValuePair<uint, (string Name, string Source)>> Names => names;

    // ---- queries ----

    public DecompFunction? Get(uint rva) => byRva.TryGetValue(rva, out int index) ? functions[index] : null;

    /** The function containing an address: exact start, else the nearest start below it whose size (when known) covers it. */
    public DecompFunction? Containing(uint rva)
    {
        if (Get(rva) is { } exact) return exact;
        int low = 0, high = functions.Length - 1, best = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (functions[middle].Rva <= rva) { best = middle; low = middle + 1; } else high = middle - 1;
        }
        if (best < 0) return null;
        var function = functions[best];
        return function.Size == 0 || rva < function.Rva + function.Size ? function : null;
    }

    /** Resolves "0xE2DBE0", "E2DBE0", "FUN_00E2DBE0" or a known name. */
    public uint? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        foreach (string prefix in new[] { "FUN_", "func_0x", "func_", "DAT_", "UNK_", "LAB_", "sub_" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && TryHex(text.AsSpan(prefix.Length), out uint prefixed)) return prefixed;
        if (TryHex(text, out uint hex) && (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.Length >= 5)) return hex;
        foreach (var (rva, entry) in names) if (string.Equals(entry.Name, text, StringComparison.OrdinalIgnoreCase)) return rva;
        foreach (var (rva, owner) in virtualNames) if (string.Equals(owner.Name, text, StringComparison.OrdinalIgnoreCase)) return rva;
        return null;
    }

    public bool IsFunctionStart(uint rva) => starts.Contains(rva);
    public IReadOnlyList<uint> CallsFrom(DecompFunction function) => calls[byRva[function.Rva]];
    public IReadOnlyList<uint> RefsFrom(DecompFunction function) => refs[byRva[function.Rva]];
    public IReadOnlyList<uint> TagClassesOf(DecompFunction function) => tagClasses[byRva[function.Rva]];
    public IEnumerable<DecompFunction> CallersOf(uint rva) => callers.TryGetValue(rva, out var list) ? list.Select(i => functions[i]) : [];
    public IEnumerable<DecompFunction> UsersOf(uint rva) => users.TryGetValue(rva, out var list) ? list.Select(i => functions[i]) : [];
    public IEnumerable<DecompFunction> UsersOfTagClass(uint type) => classUsers.TryGetValue(type, out var list) ? list.Select(i => functions[i]) : [];
    public IEnumerable<uint> TagClasses => classUsers.Keys.Order();
    public int CallerCount(uint rva) => callers.TryGetValue(rva, out var list) ? list.Count : 0;

    /** Functions whose name, RVA or signature contains the query, named ones first. */
    public IEnumerable<DecompFunction> Search(string query)
    {
        query = query.Trim();
        uint? rva = Resolve(query);
        var named = names.Where(pair => pair.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key)
            .Concat(virtualNames.Where(pair => !names.ContainsKey(pair.Key) && pair.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key))
            .Select(Get).OfType<DecompFunction>().OrderBy(function => function.Rva);
        var exact = new List<DecompFunction>();
        if (rva is { } at && Get(at) is { } hit) exact.Add(hit);
        string hex = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? query[2..] : query;
        var rest = functions.Where(function => function.Signature.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (hex.Length >= 3 && function.Rva.ToString("X8", CultureInfo.InvariantCulture).Contains(hex, StringComparison.OrdinalIgnoreCase)));
        return exact.Concat(named).Concat(rest).DistinctBy(function => function.Rva);
    }

    /** The function's decompiled C. */
    public string Source(DecompFunction function)
    {
        using var stream = new FileStream(Files[function.File], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = function.Offset;
        var buffer = new byte[function.Length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n");
    }

    /** Full-text search over every function's C (case-insensitive); yields (function, line) hits in RVA order. */
    public IEnumerable<(DecompFunction Function, string Line)> Grep(string text, CancellationToken cancel)
    {
        if (text.Length == 0) yield break;
        var needle = Encoding.UTF8.GetBytes(text.ToLowerInvariant());
        foreach (var group in functions.GroupBy(function => function.File).OrderBy(group => group.Key))
        {
            cancel.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(Files[group.Key]);
            byte[] lower = (byte[])bytes.Clone();
            for (int i = 0; i < lower.Length; i++) if (lower[i] is >= (byte)'A' and <= (byte)'Z') lower[i] |= 0x20;
            var ordered = group.OrderBy(function => function.Offset).ToArray();
            int at = 0, cursor = 0;
            while (true)
            {
                int found = lower.AsSpan(at).IndexOf(needle);
                if (found < 0) break;
                long position = at + found;
                while (cursor < ordered.Length - 1 && ordered[cursor + 1].Offset <= position) cursor++;
                var owner = ordered[cursor];
                int lineStart = Array.LastIndexOf(bytes, (byte)'\n', (int)position) + 1;
                int lineEnd = Array.IndexOf(bytes, (byte)'\n', (int)position);
                if (lineEnd < 0) lineEnd = bytes.Length;
                if (position >= owner.Offset && position < owner.Offset + owner.Length)
                    yield return (owner, Encoding.UTF8.GetString(bytes, lineStart, Math.Min(lineEnd - lineStart, 300)).Trim());
                at = lineEnd + 1;
                if (at >= lower.Length) break;
            }
        }
    }

    // ---- the binary image (strings, vtables) ----

    /** image\destiny2_image.bin as a memory image (RVA = file offset), opened on first use; null when absent. */
    public BinaryImage? Image
    {
        get
        {
            lock (imageLock)
            {
                if (image is not null) return image;
                string path = Path.Combine(Root, "image", "destiny2_image.bin");
                if (!File.Exists(path)) return null;
                return image = BinaryImage.Open(path, Path.Combine(Root, "image", "image.json"), IsFunctionStart);
            }
        }
    }

    private string cachePath = "";
    private ClassIndex? classes;
    private bool classesTried;
    private volatile Dictionary<uint, VirtualOwner> virtualNames = [];
    private readonly object classLock = new();

    /**
     * The class layer: MSVC RTTI classes and the image's other vtables, virtual-function owners (Class::vfN names),
     * constructors and fields inferred from this-pointer accesses. Built on first use from the image and cached beside the
     * decomp index (keyed on the decomp stamp and the image file); null without image\destiny2_image.bin.
     */
    public ClassIndex? Classes
    {
        get
        {
            lock (classLock)
            {
                if (classesTried) return classes;
                classesTried = true;
                var file = new FileInfo(Path.Combine(Root, "image", "destiny2_image.bin"));
                if (!file.Exists) return null;
                string classStamp = $"{stamp}|classes:{ClassIndex.Version}|{file.Length}:{file.LastWriteTimeUtc.Ticks}";
                string classCache = cachePath.Length > 0 ? Path.ChangeExtension(cachePath, null) + ".classes.bin" : "";
                classes = classCache.Length > 0 ? ClassIndex.TryRead(classCache, classStamp) : null;
                if (classes is null && Image is { } loaded)
                {
                    classes = ClassIndex.Build(loaded, this);
                    if (classCache.Length > 0)
                    {
                        try { classes.Write(classCache, classStamp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                if (classes is not null) virtualNames = classes.Virtuals.Where(pair => !pair.Value.Shared).ToDictionary(pair => pair.Key, pair => pair.Value);
                return classes;
            }
        }
    }

    /** C/C++ source folders searched for struct definitions and tag class uses (SOLFORGE_SOURCE_ROOTS, then the Sunrise client and server). */
    public IReadOnlyList<string> SourceRoots
    {
        get
        {
            var roots = new List<string>();
            if (Environment.GetEnvironmentVariable("SOLFORGE_SOURCE_ROOTS") is { Length: > 0 } set)
                roots.AddRange(set.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Directory.Exists));
            string? sol = Root;
            for (int depth = 0; sol is not null && depth < 4 && !Directory.Exists(Path.Combine(sol, "02-server")); depth++) sol = Directory.GetParent(sol)?.FullName;
            if (sol is not null)
                foreach (string relative in new[]
                {
                    Path.Combine("02-server", "solserver", "solserverbase", "project sol", "Sunrise", "Sunrise", "src"),
                    Path.Combine("02-server", "solserver", "project sol - linux server build", "sunrise-sol", "Sunrise", "src")
                })
                    if (Path.Combine(sol, relative) is var candidate && Directory.Exists(candidate)) roots.Add(candidate);
            return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }
}
