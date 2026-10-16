using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Packages;

/** English text of every class-0x80809A88 string container, keyed by string hash. */
public sealed partial class LocalizedStrings
{
    // Container, data, row and array marker classes of this build.
    private const uint ContainerClass = 0x80809A88;
    private const uint DataClass = 0x80809A8A;
    private const uint HashRowClass = 0x80800070;
    private const uint PartRowClass = 0x80809A90;
    private const uint CombinationRowClass = 0x80809A8E;
    private const uint ArrayMarker = 0x80809FBD;
    // A part with this variable hash holds text; any other value names a runtime variable.
    private const uint EmptyHash = 0x811C9DC5;
    private const int PartStride = 0x20;

    private readonly Dictionary<uint, string> unique = [];
    private readonly Dictionary<uint, string[]> ambiguous = [];
    private readonly Dictionary<uint, Dictionary<uint, string>> byContainer = [];
    private readonly Dictionary<uint, uint> englishData = [];
    private readonly Dictionary<uint, string> bankErrors = [];

    public int ContainerCount => byContainer.Count;
    public int FailedContainerCount { get; private set; }
    public int StringCount => unique.Count + ambiguous.Count;
    public int AmbiguousCount => ambiguous.Count;
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    private LocalizedStrings() { }

    /** Text for a hash when every container agrees on it; null when missing or ambiguous. */
    public string? Get(uint hash) => unique.GetValueOrDefault(hash);

    /** Text for a hash inside one container, as the owning tag references it. */
    public string? Get(uint containerTag, uint hash) =>
        byContainer.TryGetValue(containerTag, out var map) ? map.GetValueOrDefault(hash) : null;

    /** Every distinct text stored under a hash, across all containers. */
    public IReadOnlyList<string> Candidates(uint hash) =>
        unique.TryGetValue(hash, out var one) ? [one] : ambiguous.GetValueOrDefault(hash) ?? [];

    /** True when the tag is a localized string container. */
    public bool IsContainer(uint tag) => byContainer.ContainsKey(tag);

    /**
     * Reads the English data tag of every installed container.
     * @param catalog Installed packages.
     * @param token Cancels the scan between containers.
     * @return A read-only table; it is safe to read from any thread.
     */
    public static LocalizedStrings Load(PackageCatalog catalog, CancellationToken token = default) =>
        LoadBanks(catalog, catalog.Entries.Where(entry => entry.Reference == ContainerClass).Select(entry => entry.TagHash)
            .Concat(catalog.GetOverlayEntries().Where(row => row.Entry.Reference == ContainerClass).Select(row => row.Entry.TagHash)), token);

    /**
     * Explicit bank tags include project-added containers and read the current overlay.
     * @param catalog Installed packages and active project overlay.
     * @param containerTags Selected bank references, with at most one million input rows.
     * @param token Cancels between bank references.
     * @return A new immutable bank-local lookup, with diagnostics for unreadable banks.
     */
    public static LocalizedStrings LoadBanks(PackageCatalog catalog, IEnumerable<uint> containerTags,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new LocalizedStrings();
        var all = new Dictionary<uint, List<string>>();
        var diagnostics = new List<string>();
        var seen = new HashSet<uint>();
        int inputs = 0;
        foreach (uint tag in containerTags)
        {
            token.ThrowIfCancellationRequested();
            if (++inputs > 1_000_000) throw new InvalidDataException("Too many selected string-bank references.");
            if (tag is 0 or uint.MaxValue || !seen.Add(tag)) continue;
            try
            {
                if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ContainerClass)
                    throw new InvalidDataException("String bank has the wrong class or is missing.");
                var map = ReadContainer(catalog, tag, result.englishData);
                result.byContainer.Add(tag, map);
                foreach (var (hash, text) in map)
                {
                    if (!all.TryGetValue(hash, out var texts)) all.Add(hash, texts = []);
                    if (!texts.Contains(text, StringComparer.Ordinal)) texts.Add(text);
                }
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException
                                          or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
            {
                result.FailedContainerCount++;
                string diagnostic = $"String container 0x{tag:X8}: {error.Message}";
                diagnostics.Add(diagnostic); result.bankErrors[tag] = diagnostic;
            }
        }
        foreach (var (hash, texts) in all)
            if (texts.Count == 1) result.unique.Add(hash, texts[0]);
            else result.ambiguous.Add(hash, texts.ToArray());
        token.ThrowIfCancellationRequested();
        result.Diagnostics = diagnostics.AsReadOnly();
        return result;
    }

    /** Combination i of the data tag carries hash i of the container; English is language slot 0. */
    private static Dictionary<uint, string> ReadContainer(PackageCatalog catalog, uint tag, Dictionary<uint, uint>? dataTags = null)
    {
        var container = catalog.Read(tag);
        var hashes = Rows(container, 0x08, HashRowClass, 4);
        uint english = container.Length >= 0x1C ? U32(container, 0x18) : 0;
        if (dataTags is not null && container.Length >= 0x1C) dataTags[tag] = english;
        if (hashes.Length == 0) return [];
        if (!catalog.TryGetEntry(english, out var dataEntry) || dataEntry.Reference != DataClass)
            throw new InvalidDataException($"English data 0x{english:X8} has the wrong class.");
        var data = catalog.Read(english);
        Rows(data, 0x08, PartRowClass, PartStride);
        var combinations = Rows(data, 0x48, CombinationRowClass, 16);
        if (combinations.Length != hashes.Length)
            throw new InvalidDataException("String combination count differs from the hash count.");
        var map = new Dictionary<uint, string>(hashes.Length);
        var text = new StringBuilder();
        for (var i = 0; i < hashes.Length; i++)
        {
            var hash = U32(container, hashes[i]);
            if (hash == EmptyHash) continue;
            text.Clear();
            var row = combinations[i];
            var first = Relative(data, row);
            var count = I64(data, row + 8);
            if (count < 0 || first < 0 || first + count * PartStride > data.Length)
                throw new InvalidDataException("String combination points outside its data tag.");
            for (var p = 0; p < count; p++) AppendPart(data, first + p * PartStride, text);
            if (text.Length != 0) map.TryAdd(hash, text.ToString());
        }
        return map;
    }

    /** The cipher shift is added to each decoded code point, not to a byte. */
    private static void AppendPart(byte[] data, int part, StringBuilder text)
    {
        var variable = U32(data, part + 0x10);
        if (variable != EmptyHash) { text.Append($"<{variable:X8}>"); return; }
        var start = Relative(data, part + 8);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + 0x14));
        int shift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(part + 0x18));
        if (start < 0 || start + length > data.Length)
            throw new InvalidDataException("String part points outside its data tag.");
        var bytes = data.AsSpan(start, length);
        var offset = 0;
        while (offset < bytes.Length)
        {
            int lead = bytes[offset];
            var size = lead < 0x80 ? 1 : lead is >= 0xC0 and < 0xE0 ? 2 : lead is >= 0xE0 and < 0xF0 ? 3
                : lead is >= 0xF0 and < 0xF8 ? 4 : 0;
            var valid = size != 0 && offset + size <= bytes.Length;
            var point = size == 1 ? lead : lead & (0x7F >> size);
            for (var k = 1; valid && k < size; k++)
            {
                valid = (bytes[offset + k] & 0xC0) == 0x80;
                point = point << 6 | bytes[offset + k] & 0x3F;
            }
            point += shift;
            if (valid && point <= 0x10FFFF && point is < 0xD800 or >= 0xE000) text.Append(char.ConvertFromUtf32(point));
            else text.Append('\uFFFD');
            offset += Math.Max(size, 1);
        }
    }

    /** Tiger array links point to the repeated count, sixteen bytes before the rows. */
    private static int[] Rows(byte[] data, int descriptor, uint rowClass, int stride)
    {
        var count = I64(data, descriptor);
        var relative = I64(data, descriptor + 8);
        if (count == 0) return [];
        var header = descriptor + 8 + relative;
        if (count < 0 || count > 1_000_000 || header < 4 || header + 16 > data.Length
            || U32(data, (int)header - 4) != ArrayMarker || I64(data, (int)header) != count
            || U32(data, (int)header + 8) != rowClass || header + 16 + count * stride > data.Length)
            throw new InvalidDataException($"Array 0x{descriptor:X} does not match class 0x{rowClass:X8}.");
        return Enumerable.Range(0, (int)count).Select(i => (int)header + 16 + i * stride).ToArray();
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static long I64(byte[] data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset));
    private static int Relative(byte[] data, int offset) => checked((int)(offset + I64(data, offset)));
}
