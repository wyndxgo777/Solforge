using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Decomp;

/** A string found in the image's data sections. */
public sealed record ImageString(uint Rva, string Text, bool Wide);

/** A run of three or more pointers to code in a data section: a C++ vtable (or a function-pointer table). */
public sealed record VTable(uint Rva, uint[] Entries);

/** A PE section of the memory image. */
public sealed record ImageSection(string Name, uint Rva, uint Size, bool Executable);

/**
 * The dumped memory image (RVA = file offset). Reads the PE section table when the dump starts with one, finds strings
 * (ASCII and UTF-16, five characters or more) and vtables (pointer runs into code, using image.json's load base) in the
 * non-code sections. Both scans run once, on first use.
 */
public sealed class BinaryImage
{
    private readonly byte[] bytes;
    private readonly Func<uint, bool> isFunctionStart;
    private IReadOnlyList<ImageString>? strings;
    private IReadOnlyList<VTable>? vtables;
    private Dictionary<uint, ImageString>? stringAt;
    private Dictionary<uint, List<VTable>>? vtablesWith;
    private readonly object scanLock = new();

    public ulong Base { get; }
    public IReadOnlyList<ImageSection> Sections { get; }
    public long Length => bytes.LongLength;

    private BinaryImage(byte[] bytes, ulong imageBase, IReadOnlyList<ImageSection> sections, Func<uint, bool> isFunctionStart)
    {
        this.bytes = bytes; Base = imageBase; Sections = sections; this.isFunctionStart = isFunctionStart;
    }

    public static BinaryImage Open(string path, string? metadata, Func<uint, bool> isFunctionStart) =>
        FromBytes(File.ReadAllBytes(path), metadata is not null && File.Exists(metadata) ? File.ReadAllText(metadata) : null, isFunctionStart);

    /** metadataJson is image.json ({"base": "0x7FF6..."}); without it the PE header's ImageBase is used. */
    public static BinaryImage FromBytes(byte[] bytes, string? metadataJson, Func<uint, bool> isFunctionStart)
    {
        ulong imageBase = 0;
        if (metadataJson is not null)
        {
            try
            {
                if (JsonNode.Parse(metadataJson)?["base"]?.ToString() is { } text
                    && ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed))
                    imageBase = parsed;
            }
            catch (System.Text.Json.JsonException) { }
        }
        var sections = new List<ImageSection>();
        if (bytes.Length > 0x400 && bytes[0] == 'M' && bytes[1] == 'Z')
        {
            int pe = BitConverter.ToInt32(bytes, 0x3C);
            if (pe > 0 && pe + 24 < bytes.Length && bytes[pe] == 'P' && bytes[pe + 1] == 'E')
            {
                int count = BitConverter.ToUInt16(bytes, pe + 6), optional = BitConverter.ToUInt16(bytes, pe + 20);
                if (imageBase == 0 && optional >= 32 && BitConverter.ToUInt16(bytes, pe + 24) == 0x20B) imageBase = BitConverter.ToUInt64(bytes, pe + 24 + 24);
                int table = pe + 24 + optional;
                for (int i = 0; i < count && table + i * 40 + 40 <= bytes.Length; i++)
                {
                    int at = table + i * 40;
                    string name = Encoding.ASCII.GetString(bytes, at, 8).TrimEnd('\0');
                    uint size = BitConverter.ToUInt32(bytes, at + 8), rva = BitConverter.ToUInt32(bytes, at + 12);
                    uint flags = BitConverter.ToUInt32(bytes, at + 36);
                    sections.Add(new ImageSection(name, rva, size, (flags & 0x20000000) != 0 || (flags & 0x20) != 0));
                }
            }
        }
        if (sections.Count == 0) sections.Add(new ImageSection("image", 0, (uint)bytes.Length, false));
        return new BinaryImage(bytes, imageBase, sections, isFunctionStart);
    }

    private IEnumerable<(int Start, int End)> DataRanges() => Sections.Where(section => !section.Executable)
        .Select(section => ((int)Math.Min(section.Rva, (uint)bytes.Length), (int)Math.Min((long)section.Rva + section.Size, bytes.Length)));

    private bool InCode(uint rva) => isFunctionStart(rva)
        || Sections.Any(section => section.Executable && rva >= section.Rva && rva < section.Rva + section.Size);

    /** True when an RVA is a known function start or inside an executable section. */
    public bool IsCode(uint rva) => InCode(rva);

    /** The whole image (RVA = offset), for scanners such as the RTTI pass in ClassIndex. */
    public ReadOnlySpan<byte> Bytes => bytes;

    public IReadOnlyList<ImageString> Strings { get { Scan(); return strings!; } }
    public IReadOnlyList<VTable> VTables { get { Scan(); return vtables!; } }

    public ImageString? StringAt(uint rva) { Scan(); return stringAt!.GetValueOrDefault(rva); }
    public IReadOnlyList<VTable> VTablesWith(uint function) { Scan(); return vtablesWith!.TryGetValue(function, out var list) ? list : []; }
    public VTable? VTableAt(uint rva) { Scan(); return vtables!.FirstOrDefault(table => table.Rva == rva); }

    /** Reads raw bytes at an RVA (clamped to the image). */
    public byte[] Read(uint rva, int count)
    {
        if (rva >= bytes.Length) return [];
        count = (int)Math.Min(count, bytes.Length - rva);
        return bytes.AsSpan((int)rva, Math.Max(0, count)).ToArray();
    }

    private void Scan()
    {
        lock (scanLock)
        {
            if (strings is not null) return;
            var found = new List<ImageString>();
            var tables = new List<VTable>();
            foreach (var (start, end) in DataRanges())
            {
                ScanAscii(start, end, found);
                ScanWide(start, end, found);
                ScanTables(start, end, tables);
            }
            found.Sort((a, b) => a.Rva.CompareTo(b.Rva));
            var at = new Dictionary<uint, ImageString>();
            foreach (var item in found) at.TryAdd(item.Rva, item);
            var with = new Dictionary<uint, List<VTable>>();
            foreach (var table in tables)
                foreach (uint entry in table.Entries.Distinct())
                {
                    if (!with.TryGetValue(entry, out var list)) with[entry] = list = [];
                    list.Add(table);
                }
            stringAt = at; vtablesWith = with; vtables = tables; strings = found;
        }
    }

    private static bool Printable(byte c) => c is >= 0x20 and < 0x7F or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private void ScanAscii(int start, int end, List<ImageString> found)
    {
        int run = start;
        for (int i = start; i < end; i++)
        {
            if (Printable(bytes[i])) continue;
            if (bytes[i] == 0 && i - run >= 5) found.Add(new ImageString((uint)run, Encoding.ASCII.GetString(bytes, run, Math.Min(i - run, 4096)), false));
            run = i + 1;
        }
    }

    private void ScanWide(int start, int end, List<ImageString> found)
    {
        start += start & 1;
        int run = start;
        for (int i = start; i + 1 < end; i += 2)
        {
            if (bytes[i + 1] == 0 && Printable(bytes[i])) continue;
            if (bytes[i] == 0 && bytes[i + 1] == 0 && (i - run) / 2 >= 5)
                found.Add(new ImageString((uint)run, Encoding.Unicode.GetString(bytes, run, Math.Min(i - run, 8192)), true));
            run = i + 2;
        }
    }

    private void ScanTables(int start, int end, List<VTable> tables)
    {
        if (Base == 0) return;
        start = (start + 7) & ~7;
        var run = new List<uint>();
        int runStart = start;
        for (int i = start; i + 8 <= end; i += 8)
        {
            ulong value = BitConverter.ToUInt64(bytes, i);
            bool code = value > Base && value - Base < uint.MaxValue && InCode((uint)(value - Base));
            if (code)
            {
                if (run.Count == 0) runStart = i;
                run.Add((uint)(value - Base));
                continue;
            }
            Flush();
        }
        Flush();
        void Flush()
        {
            if (run.Count >= 3 && run.Count(isFunctionStart) >= 2) tables.Add(new VTable((uint)runStart, [.. run]));
            run.Clear();
        }
    }
}
