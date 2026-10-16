using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Layouts follow the installed declaration tables and the native format translation table. */
internal sealed class NativeLayoutCatalog
{
    private readonly Dictionary<int, NativeInputElement[]> declarations = [];
    private readonly Dictionary<int, uint> sources = [];
    private readonly HashSet<int> conflicts = [];

    // The native builder indexes these semantic names with the first byte of each element.
    private static readonly string[] Semantics = ["POSITION", "BLENDWEIGHT", "BLENDINDICES", "NORMAL", "PSIZE", "TEXCOORD", "TANGENT", "BINORMAL", "COLOR", "TEXCOORD"];
    // Native table rows contain DXGI format, byte width, and the fallback format identifier.
    private static readonly int[] Formats = [0, 41, 16, 6, 2, 28, 30, 38, 14, 12, 37, 13, 34, 10, 32, 31, 25, 24, 43, 18, 4, 42, 17, 3, 59, 61, 0, 0, 0, 0, 0, 29, 26, 13, 0];
    private static readonly int[] Widths = [0, 4, 8, 12, 16, 4, 4, 4, 8, 8, 4, 8, 4, 8, 4, 4, 4, 4, 4, 8, 16, 4, 8, 16, 2, 1, 4, 4, 4, 4, 4, 4, 4, 8, 4];
    private static readonly int[] Fallbacks = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 11, 8, 11, 0, 0, 0, 0, 0, 0, 7, 0, 16, 17, 8, 11, 11, 27, 13, 0, 11];

    /** Equivalent bootstrap/global copies agree; conflicting declaration ids are refused. */
    public NativeLayoutCatalog(PackageCatalog catalog)
    {
        TagData Read(uint tag, uint expected)
        {
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != expected)
                throw new InvalidDataException($"Layout dependency 0x{tag:X8} has the wrong class.");
            return new TagData(catalog.Read(tag));
        }
        foreach (var root in catalog.Entries.Where(e => e.Reference == 0x80806CB1))
        {
            var global = Read(root.TagHash, 0x80806CB1);
            var resource = Read(global.U32(8), 0x808072A6);
            var pool = Read(resource.U32(12), 0x808072AD);
            var poolRows = pool.Rows(8, 0x808072AF, 16);
            var declarationTag = resource.U32(40);
            var table = Read(declarationTag, 0x808072A9);
            foreach (var row in table.Rows(8, 0x808072AC, 28))
            {
                var id = table.U16(row);
                var elements = new List<NativeInputElement>();
                for (var slot = 0; slot < 4; ++slot)
                {
                    var group = table.I32(row + 8 + slot * 4);
                    if (group == -1) continue;
                    if ((uint)group >= poolRows.Length) throw new InvalidDataException("Layout stream group index is invalid.");
                    var offset = 0;
                    var instance = table.U8(row + 2 + slot);
                    if (instance > 1) throw new InvalidDataException("Layout instance classification is invalid.");
                    foreach (var element in pool.Rows(poolRows[group], 0x808072B2, 3))
                    {
                        var semantic = pool.U8(element);
                        var format = (int)pool.U8(element + 2);
                        if (semantic >= Semantics.Length || format <= 0 || format >= Formats.Length)
                            throw new InvalidDataException("Native layout element code is invalid.");
                        var visits = 0;
                        while (Formats[format] == 0)
                        {
                            format = Fallbacks[format];
                            if (format <= 0 || format >= Formats.Length || ++visits > Formats.Length)
                                throw new InvalidDataException("Native layout format fallback does not terminate.");
                        }
                        elements.Add(new NativeInputElement(Semantics[semantic], pool.U8(element + 1), slot,
                            offset, (NativeVertexFormat)Formats[format], instance != 0, instance));
                        offset += Widths[format];
                    }
                }
                var decoded = elements.ToArray();
                if (declarations.TryGetValue(id, out var previous) && !previous.SequenceEqual(decoded)) conflicts.Add(id);
                else
                {
                    declarations[id] = decoded;
                    sources[id] = declarationTag;
                }
            }
        }
    }

    /** Looks up a vertex layout by id; false when unknown or declared inconsistently. */
    public bool TryGet(int id, out NativeInputElement[] elements, out uint source)
    {
        elements = [];
        source = 0;
        if (conflicts.Contains(id) || !declarations.TryGetValue(id, out var result)) return false;
        elements = result;
        source = sources[id];
        return true;
    }
}
