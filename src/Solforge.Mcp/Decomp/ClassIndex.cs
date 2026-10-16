/**
 * File: C++ class layer over the image: MSVC RTTI classes, bases and vtables with the virtual functions in each slot.
 * Built from BinaryImage.cs and DecompIndex.cs; serves get_class and get_vtables.
 */
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Solforge.Mcp.Decomp;

/** A base class from MSVC RTTI (the class hierarchy descriptor's base class array, the class itself left out). Depth 1 is a direct base. */
public sealed record ClassBase(string Name, uint TypeDescriptor, int Depth, int Mdisp, int Pdisp, int Vdisp, uint Attributes);

/** One vtable of a class: its RVA, the complete object locator before it (0 without RTTI) and the subobject offset it serves. */
public sealed record ClassVTable(uint Rva, uint Locator, int Offset, uint[] Entries);

/**
 * A C++ class seen in the image: from RTTI (name, bases, every vtable) or, without RTTI, one vtable named vtable_RVA.
 * Constructors are the decompiled functions that reference a vtable (they store it into the object); fields are merged
 * from the this-pointer (param_1) accesses of the class's own virtual methods and constructors; tag classes are the Tiger
 * class hashes those functions compare against.
 */
public sealed record ClassInfo(string Name, string? Mangled, uint TypeDescriptor, uint Attributes, ClassBase[] Bases, ClassVTable[] VTables,
    uint[] Constructors, InferredField[] Fields, uint[] TagClasses)
{
    public bool Rtti => Mangled is not null;
}

/** Who a virtual function belongs to: the class that introduces it at a slot (the most-base class holding it), and how many vtables hold it. */
public sealed record VirtualOwner(string Class, int Slot, int Offset, int Tables, bool Shared)
{
    public string Name => Offset == 0 ? $"{Class}::vf{Slot}" : $"{Class}[+0x{Offset:X}]::vf{Slot}";
}

/**
 * The class layer over the memory image: MSVC x64 RTTI (type descriptors ".?AV...", complete object locators with a
 * self-RVA, class hierarchy descriptors and base class arrays) plus the image's pointer-run vtables split where the
 * decompiled code references a table start. Virtual functions get Class::vfN names (user names still win). Built once
 * per image and decomp stamp and cached next to the decomp index.
 */
public sealed class ClassIndex
{
    public const int Version = 1;
    private const int MaxMethodsInferred = 64, MaxConstructors = 32;

    public IReadOnlyList<ClassInfo> Classes { get; }
    public IReadOnlyDictionary<uint, VirtualOwner> Virtuals { get; }
    private readonly Dictionary<uint, (ClassInfo Class, ClassVTable Table)> byVTable = [];
    private readonly Dictionary<uint, ClassInfo> byDescriptor = [];
    private readonly Dictionary<uint, List<ClassInfo>> derived = [];

    /** Builds lookup tables (by vtable, by descriptor, derived classes) from the class list. */
    public ClassIndex(IReadOnlyList<ClassInfo> classes, IReadOnlyDictionary<uint, VirtualOwner> virtuals)
    {
        Classes = classes; Virtuals = virtuals;
        foreach (var info in classes)
        {
            foreach (var table in info.VTables) byVTable.TryAdd(table.Rva, (info, table));
            if (info.Rtti) byDescriptor.TryAdd(info.TypeDescriptor, info);
        }
        foreach (var info in classes)
            foreach (var parent in info.Bases)
            {
                if (!derived.TryGetValue(parent.TypeDescriptor, out var list)) derived[parent.TypeDescriptor] = list = [];
                list.Add(info);
            }
    }

    public int RttiClasses => Classes.Count(info => info.Rtti);
    public int VTableCount => byVTable.Count;
    public IEnumerable<ClassVTable> VTables => byVTable.Values.Select(entry => entry.Table).OrderBy(table => table.Rva);

    /** The class and vtable starting at an RVA, or null. */
    public (ClassInfo Class, ClassVTable Table)? VTableAt(uint rva) => byVTable.TryGetValue(rva, out var entry) ? entry : null;
    /** The class whose RTTI descriptor sits at an RVA, or null. */
    public ClassInfo? ByDescriptor(uint rva) => byDescriptor.GetValueOrDefault(rva);
    /** Classes that directly derive from a class. */
    public IReadOnlyList<ClassInfo> DerivedFrom(ClassInfo info) => info.Rtti && derived.TryGetValue(info.TypeDescriptor, out var list) ? list : [];

    /** The vtables (with their class and slot) that hold a function. */
    public IEnumerable<(ClassInfo Class, ClassVTable Table, int Slot)> Holding(uint function) =>
        Classes.SelectMany(info => info.VTables.SelectMany(table => table.Entries.Select((entry, slot) => (entry, slot))
            .Where(item => item.entry == function).Select(item => (info, table, item.slot))));

    /** Classes by exact name (or vtable_RVA), then by name substring, shortest first. */
    public IEnumerable<ClassInfo> Search(string query)
    {
        query = query.Trim();
        var exact = Classes.Where(info => string.Equals(info.Name, query, StringComparison.OrdinalIgnoreCase) || string.Equals(info.Mangled, query, StringComparison.Ordinal));
        var partial = Classes.Where(info => info.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(info => info.Name.Length).ThenBy(info => info.Name, StringComparer.Ordinal);
        return exact.Concat(partial).Distinct();
    }

    // ---- build ----

    /** One RTTI class straight from the image, before methods and fields are attached. */
    public sealed record RttiType(uint TypeDescriptor, string Mangled, uint Hierarchy, uint Attributes, ClassBase[] Bases, List<ClassVTable> VTables);

    /** Scans the image for MSVC x64 RTTI. Every class with a type descriptor is returned, with or without a vtable. */
    public static IReadOnlyList<RttiType> ScanRtti(BinaryImage image)
    {
        var bytes = image.Bytes;
        ulong imageBase = image.Base;
        if (imageBase == 0) return [];
        int length = bytes.Length;
        var data = image.Sections.Where(section => !section.Executable)
            .Select(section => ((int)Math.Min(section.Rva, (uint)length), (int)Math.Min((long)section.Rva + section.Size, length))).ToList();

        // Type descriptors: { void* pVFTable (type_info's vftable); void* spare; char name[] = ".?AV..." }.
        var names = new Dictionary<uint, string>();
        ReadOnlySpan<byte> marker = ".?A"u8;
        foreach (var (start, end) in data)
        {
            int at = start;
            while (at < end)
            {
                int found = bytes[at..end].IndexOf(marker);
                if (found < 0) break;
                int text = at + found;
                at = text + 1;
                if (text - start < 16 || (text - 16) % 8 != 0 || text + 4 >= end || bytes[text + 3] is not ((byte)'V' or (byte)'U' or (byte)'T')) continue;
                ulong vftable = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(text - 16)..]);
                if (vftable <= imageBase || vftable - imageBase >= (ulong)bytes.Length) continue;
                int terminator = bytes[text..Math.Min(end, text + 2048)].IndexOf((byte)0);
                if (terminator < 5) continue;
                var name = bytes.Slice(text, terminator);
                bool printable = true;
                foreach (byte c in name) if (c is < 0x21 or >= 0x7F) { printable = false; break; }
                if (printable) names[(uint)(text - 16)] = Encoding.ASCII.GetString(name);
            }
        }
        if (names.Count == 0) return [];

        // Complete object locators: { u32 signature = 1; u32 offset; u32 cdOffset; rva pTypeDescriptor; rva pClassDescriptor; rva pSelf }.
        var locators = new Dictionary<ulong, (uint Rva, int Offset, uint Type, uint Hierarchy)>();
        foreach (var (start, end) in data)
            for (int at = (start + 3) & ~3; at + 24 <= end; at += 4)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 20)..]) != (uint)at) continue;
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 12)..]);
                if (!names.ContainsKey(type)) continue;
                locators[imageBase + (uint)at] = ((uint)at, BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4)..]), type, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 16)..]));
            }

        // Vtables: the qword before a vtable points at its locator.
        var tables = new Dictionary<uint, List<ClassVTable>>();
        var hierarchies = new Dictionary<uint, uint>();
        if (locators.Count > 0)
            foreach (var (start, end) in data)
                for (int at = (start + 7) & ~7; at + 16 <= end; at += 8)
                {
                    if (!locators.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]), out var locator)) continue;
                    var entries = new List<uint>();
                    for (int slot = at + 8; slot + 8 <= end && entries.Count < 4096; slot += 8)
                    {
                        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[slot..]);
                        if (value <= imageBase || value - imageBase >= uint.MaxValue || !image.IsCode((uint)(value - imageBase))) break;
                        entries.Add((uint)(value - imageBase));
                    }
                    if (!tables.TryGetValue(locator.Type, out var list)) tables[locator.Type] = list = [];
                    list.Add(new ClassVTable((uint)at + 8, locator.Rva, locator.Offset, [.. entries]));
                    hierarchies.TryAdd(locator.Type, locator.Hierarchy);
                }

        var result = new List<RttiType>();
        foreach (var (type, mangled) in names.OrderBy(pair => pair.Key))
        {
            uint hierarchy = hierarchies.GetValueOrDefault(type);
            uint attributes = 0;
            ClassBase[] bases = [];
            if (hierarchy != 0 && hierarchy + 16 <= bytes.Length) (attributes, bases) = Hierarchy(bytes, hierarchy, names);
            var list = tables.GetValueOrDefault(type) ?? new List<ClassVTable>();
            list.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Rva.CompareTo(b.Rva));
            result.Add(new RttiType(type, mangled, hierarchy, attributes, bases, list));
        }
        return result;
    }

    /** Class hierarchy descriptor { u32 signature; u32 attributes; u32 numBaseClasses; rva pBaseClassArray } and its base class descriptors. */
    private static (uint Attributes, ClassBase[] Bases) Hierarchy(ReadOnlySpan<byte> bytes, uint at, Dictionary<uint, string> names)
    {
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(at + 4)..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(at + 8)..]);
        uint array = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(at + 12)..]);
        if (count is 0 or > 1024 || (long)array + count * 4 > bytes.Length) return (attributes, Array.Empty<ClassBase>());
        // Base class descriptor { rva pTypeDescriptor; u32 numContainedBases; i32 mdisp, pdisp, vdisp; u32 attributes; rva pClassDescriptor }.
        var raw = new List<(uint Type, int Contained, int Mdisp, int Pdisp, int Vdisp, uint Attributes)>();
        for (uint i = 0; i < count; i++)
        {
            uint descriptor = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(array + i * 4)..]);
            if ((long)descriptor + 24 > bytes.Length) return (attributes, Array.Empty<ClassBase>());
            var d = bytes[(int)descriptor..];
            raw.Add((BinaryPrimitives.ReadUInt32LittleEndian(d), BinaryPrimitives.ReadInt32LittleEndian(d[4..]), BinaryPrimitives.ReadInt32LittleEndian(d[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[12..]), BinaryPrimitives.ReadInt32LittleEndian(d[16..]), BinaryPrimitives.ReadUInt32LittleEndian(d[20..])));
        }
        // The array is the hierarchy in pre-order with the class itself first; numContainedBases gives each subtree's size.
        var depth = new int[raw.Count];
        var stack = new Stack<(int Depth, int End)>();
        stack.Push((0, raw.Count - 1));
        for (int i = 1; i < raw.Count; i++)
        {
            while (stack.Count > 1 && i > stack.Peek().End) stack.Pop();
            depth[i] = stack.Peek().Depth + 1;
            stack.Push((depth[i], i + Math.Max(0, raw[i].Contained)));
        }
        var bases = new List<ClassBase>();
        for (int i = 1; i < raw.Count; i++)
        {
            var item = raw[i];
            string name = names.TryGetValue(item.Type, out var mangled) ? RttiNames.Demangle(mangled) : $"type_{item.Type:X8}";
            bases.Add(new ClassBase(name, item.Type, depth[i], item.Mdisp, item.Pdisp, item.Vdisp, item.Attributes));
        }
        return (attributes, bases.ToArray());
    }

    /** Builds the class index: RTTI classes, then the image's other vtables (split at referenced starts), virtual owners, constructors and fields. */
    public static ClassIndex Build(BinaryImage image, DecompIndex index, CancellationToken cancel = default)
    {
        var rtti = ScanRtti(image);
        var tables = new List<(string Name, string? Mangled, uint Type, uint Attributes, ClassBase[] Bases, ClassVTable[] VTables)>();
        var claimed = new HashSet<uint>();
        foreach (var type in rtti)
        {
            tables.Add((RttiNames.Demangle(type.Mangled), type.Mangled, type.TypeDescriptor, type.Attributes, type.Bases, type.VTables.ToArray()));
            foreach (var table in type.VTables) claimed.Add(table.Rva);
        }
        foreach (var run in image.VTables)
        {
            cancel.ThrowIfCancellationRequested();
            // A pointer run can hold several adjacent vtables; cut it wherever the decompiled code references a slot (a constructor storing it).
            var cuts = new List<int> { 0 };
            for (int slot = 1; slot < run.Entries.Length; slot++)
            {
                uint at = run.Rva + (uint)slot * 8;
                if (claimed.Contains(at) || index.UsersOf(at).Any()) cuts.Add(slot);
            }
            for (int i = 0; i < cuts.Count; i++)
            {
                int from = cuts[i], to = i + 1 < cuts.Count ? cuts[i + 1] : run.Entries.Length;
                uint rva = run.Rva + (uint)from * 8;
                if (claimed.Contains(rva)) continue;
                claimed.Add(rva);
                tables.Add(($"vtable_{rva:X8}", null, 0u, 0u, Array.Empty<ClassBase>(), new[] { new ClassVTable(rva, 0, 0, run.Entries[from..to]) }));
            }
        }

        // Owners: for each function, the vtables holding it. The owner is the most-base class (fewest bases, then shortest table);
        // when the holders disagree on the slot, or RTTI says they are unrelated, it is a shared stub (purecall, empty body).
        var holders = new Dictionary<uint, List<(int Class, int Table, int Slot)>>();
        for (int c = 0; c < tables.Count; c++)
            for (int t = 0; t < tables[c].VTables.Length; t++)
            {
                var entries = tables[c].VTables[t].Entries;
                for (int slot = 0; slot < entries.Length; slot++)
                {
                    if (!holders.TryGetValue(entries[slot], out var list)) holders[entries[slot]] = list = [];
                    list.Add((c, t, slot));
                }
            }
        var virtuals = new Dictionary<uint, VirtualOwner>();
        var owned = new List<uint>[tables.Count];
        for (int c = 0; c < tables.Count; c++) owned[c] = [];
        foreach (var (function, list) in holders)
        {
            var best = list.OrderBy(h => tables[h.Class].Bases.Length).ThenBy(h => tables[h.Class].VTables[h.Table].Offset)
                .ThenBy(h => tables[h.Class].VTables[h.Table].Entries.Length).ThenBy(h => tables[h.Class].VTables[h.Table].Rva).First();
            var owner = tables[best.Class];
            bool shared = list.Any(h => h.Slot != best.Slot) || list.Any(h => h.Class != best.Class && owner.Mangled is not null
                && tables[h.Class].Mangled is not null && !tables[h.Class].Bases.Any(b => b.TypeDescriptor == owner.Type));
            virtuals[function] = new VirtualOwner(owner.Name, best.Slot, owner.VTables[best.Table].Offset, list.Count, shared);
            if (!shared && owner.VTables[best.Table].Offset == 0) owned[best.Class].Add(function);
        }

        var classes = new List<ClassInfo>(tables.Count);
        for (int c = 0; c < tables.Count; c++)
        {
            cancel.ThrowIfCancellationRequested();
            var item = tables[c];
            var constructors = item.VTables.SelectMany(table => index.UsersOf(table.Rva)).Select(f => f.Rva).Distinct().Take(MaxConstructors).ToArray();
            var sources = owned[c].Take(MaxMethodsInferred).Concat(constructors).Distinct().Select(index.Get).OfType<DecompFunction>().ToList();
            var fields = new Dictionary<int, (Dictionary<string, int> Types, int Uses)>();
            var tagClasses = new SortedSet<uint>();
            foreach (var function in sources)
            {
                foreach (uint type in index.TagClassesOf(function)) tagClasses.Add(type);
                string source;
                try { source = index.Source(function); } catch (IOException) { continue; }
                foreach (var field in StructLayouts.Infer(source, "param_1"))
                {
                    if (!fields.TryGetValue(field.Offset, out var entry)) fields[field.Offset] = entry = (new Dictionary<string, int>(), 0);
                    foreach (string type in field.Type.Split(" | ")) entry.Types[type] = entry.Types.GetValueOrDefault(type) + field.Uses;
                    fields[field.Offset] = (entry.Types, entry.Uses + field.Uses);
                }
            }
            var merged = fields.OrderBy(pair => pair.Key).Select(pair => new InferredField(pair.Key,
                string.Join(" | ", pair.Value.Types.OrderByDescending(type => type.Value).ThenBy(type => type.Key, StringComparer.Ordinal).Select(type => type.Key)), pair.Value.Uses)).ToArray();
            classes.Add(new ClassInfo(item.Name, item.Mangled, item.Type, item.Attributes, item.Bases, item.VTables, constructors, merged, [.. tagClasses]));
        }
        return new ClassIndex(classes, virtuals);
    }

    // ---- cache ----

    /** Writes the index to a cache file (through a temp file) tagged with a stamp. */
    public void Write(string path, string stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temporary)))
        {
            writer.Write(stamp);
            writer.Write(Classes.Count);
            foreach (var info in Classes)
            {
                writer.Write(info.Name); writer.Write(info.Mangled is not null); if (info.Mangled is not null) writer.Write(info.Mangled);
                writer.Write(info.TypeDescriptor); writer.Write(info.Attributes);
                writer.Write(info.Bases.Length);
                foreach (var item in info.Bases)
                {
                    writer.Write(item.Name); writer.Write(item.TypeDescriptor); writer.Write(item.Depth);
                    writer.Write(item.Mdisp); writer.Write(item.Pdisp); writer.Write(item.Vdisp); writer.Write(item.Attributes);
                }
                writer.Write(info.VTables.Length);
                foreach (var table in info.VTables) { writer.Write(table.Rva); writer.Write(table.Locator); writer.Write(table.Offset); Array(table.Entries); }
                Array(info.Constructors);
                writer.Write(info.Fields.Length);
                foreach (var field in info.Fields) { writer.Write(field.Offset); writer.Write(field.Type); writer.Write(field.Uses); }
                Array(info.TagClasses);
            }
            writer.Write(Virtuals.Count);
            foreach (var (function, owner) in Virtuals)
            {
                writer.Write(function); writer.Write(owner.Class); writer.Write(owner.Slot); writer.Write(owner.Offset); writer.Write(owner.Tables); writer.Write(owner.Shared);
            }
            void Array(uint[] values) { writer.Write(values.Length); foreach (uint value in values) writer.Write(value); }
        }
        File.Move(temporary, path, overwrite: true);
    }

    /** Reads a cached index when the stamp matches; null when missing, stale or unreadable. */
    public static ClassIndex? TryRead(string path, string stamp)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadString() != stamp) return null;
            int count = reader.ReadInt32();
            var classes = new List<ClassInfo>(count);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                string? mangled = reader.ReadBoolean() ? reader.ReadString() : null;
                uint type = reader.ReadUInt32(), attributes = reader.ReadUInt32();
                var bases = new ClassBase[reader.ReadInt32()];
                for (int b = 0; b < bases.Length; b++)
                    bases[b] = new ClassBase(reader.ReadString(), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32());
                var vtables = new ClassVTable[reader.ReadInt32()];
                for (int t = 0; t < vtables.Length; t++) vtables[t] = new ClassVTable(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32(), Array());
                uint[] constructors = Array();
                var fields = new InferredField[reader.ReadInt32()];
                for (int f = 0; f < fields.Length; f++) fields[f] = new InferredField(reader.ReadInt32(), reader.ReadString(), reader.ReadInt32());
                classes.Add(new ClassInfo(name, mangled, type, attributes, bases, vtables, constructors, fields, Array()));
            }
            int virtualCount = reader.ReadInt32();
            var virtuals = new Dictionary<uint, VirtualOwner>(virtualCount);
            for (int i = 0; i < virtualCount; i++)
                virtuals[reader.ReadUInt32()] = new VirtualOwner(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean());
            return new ClassIndex(classes, virtuals);
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
}

/** A best-effort MSVC type-name demangler for RTTI names (".?AVFoo@ns@@" is "ns::Foo"); falls back to the raw name. */
public static class RttiNames
{
    /** Demangles an MSVC RTTI type name (.?AV...) into a readable C++ name. */
    public static string Demangle(string mangled)
    {
        string body = mangled.StartsWith(".?A", StringComparison.Ordinal) && mangled.Length > 4 ? mangled[4..] : mangled;
        try
        {
            int at = 0;
            var names = new List<string>();
            string result = QualifiedName(body, ref at, names);
            return at == body.Length ? result : body;
        }
        catch (FormatException) { return body; }
        catch (ArgumentOutOfRangeException) { return body; }
        catch (IndexOutOfRangeException) { return body; }
    }

    // A qualified name is its parts innermost first, each ended by '@', the whole ended by another '@'.
    private static string QualifiedName(string text, ref int at, List<string> names)
    {
        var parts = new List<string>();
        while (text[at] != '@') parts.Add(Part(text, ref at, names));
        at++;
        if (parts.Count == 0) throw new FormatException();
        parts.Reverse();
        return string.Join("::", parts);
    }

    /** Demangler: one name part, either an inline name or a back-reference digit. */
    private static string Part(string text, ref int at, List<string> names)
    {
        char c = text[at];
        if (c is >= '0' and <= '9') { at++; return names[c - '0']; }
        if (c == '?' && text[at + 1] == '$')
        {
            at += 2;
            var scope = new List<string>();
            string name = Identifier(text, ref at, scope);
            var arguments = new List<string>();
            while (text[at] != '@') arguments.Add(Argument(text, ref at, scope));
            at++;
            string instance = $"{name}<{string.Join(",", arguments)}>";
            if (names.Count < 10) names.Add(instance);
            return instance;
        }
        return Identifier(text, ref at, names);
    }

    /** Demangler: reads an @-terminated identifier and remembers it for back-references. */
    private static string Identifier(string text, ref int at, List<string> names)
    {
        int end = text.IndexOf('@', at);
        if (end <= at) throw new FormatException();
        string name = text[at..end];
        at = end + 1;
        if (names.Count < 10) names.Add(name);
        return name;
    }

    /** Demangler: reads one type argument of a template name. */
    private static string Argument(string text, ref int at, List<string> names)
    {
        char c = text[at++];
        switch (c)
        {
            case 'V' or 'U' or 'T' or 'W': if (c == 'W') at++; return QualifiedName(text, ref at, names);
            case '$':
                char kind = text[at++];
                if (kind == '0') return Number(text, ref at).ToString(CultureInfo.InvariantCulture);
                if (kind == '$' && text[at] == 'V') { at++; return ""; }
                throw new FormatException();
            case 'P' or 'Q' or 'R' or 'S':
                if (text[at] == 'E') at++;
                if (text[at] is not ('A' or 'B' or 'C' or 'D')) throw new FormatException();
                bool constant = text[at] is 'B' or 'D';
                at++;
                string pointee = Argument(text, ref at, names);
                return (constant ? "const " : "") + pointee + (c is 'P' or 'Q' ? "*" : "&");
            case '_':
                char wide = text[at++];
                return wide switch { 'N' => "bool", 'J' => "__int64", 'K' => "unsigned __int64", 'W' => "wchar_t", _ => throw new FormatException() };
            default:
                return c switch
                {
                    'C' => "signed char", 'D' => "char", 'E' => "unsigned char", 'F' => "short", 'G' => "unsigned short", 'H' => "int", 'I' => "unsigned int",
                    'J' => "long", 'K' => "unsigned long", 'M' => "float", 'N' => "double", 'X' => "void",
                    _ => throw new FormatException()
                };
        }
    }

    // $0 numbers: '0'..'9' are 1..10; otherwise hex digits 'A'..'P' ended by '@'; a leading '?' negates.
    private static long Number(string text, ref int at)
    {
        bool negative = text[at] == '?';
        if (negative) at++;
        long value;
        if (text[at] is >= '0' and <= '9') value = text[at++] - '0' + 1;
        else
        {
            value = 0;
            while (text[at] != '@')
            {
                if (text[at] is < 'A' or > 'P') throw new FormatException();
                value = value * 16 + (text[at++] - 'A');
            }
            at++;
        }
        return negative ? -value : value;
    }
}
