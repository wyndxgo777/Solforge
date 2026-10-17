/**
 * File: Port plans. Walks a retail tag's outgoing references and marks each dependency portable, needs_converter or
 * blocked, with the reason and the converter that applies. Portable means only the array markers, element classes and
 * references need rewriting (the class map names one 86657 class of the same size); it is still unverified in game.
 */
namespace Solforge.Retail.Porting;

/** The verdict for one dependency. */
public enum RetailPortStatus { Portable, NeedsConverter, Blocked }

/** One dependency of a port plan. */
public sealed record RetailPortItem(uint Tag, int Depth, uint Parent, uint? Class, string ClassName, byte Type, byte Subtype, int Size,
    RetailPortStatus Status, string Converter, string Reason, uint? SolClass);

/** A port plan: every dependency reached and totals. */
public sealed record RetailPortPlan(uint Root, IReadOnlyList<RetailPortItem> Items, bool Truncated, IReadOnlyList<string> Notes)
{
    public int Count(RetailPortStatus status) => Items.Count(item => item.Status == status);
}

/** Builds port plans. */
public static class RetailPortPlanner
{
    /** Converters by retail class: name and what they do. */
    public static readonly IReadOnlyDictionary<uint, (string Converter, string Note)> Converters = new Dictionary<uint, (string, string)>
    {
        [0x808099EF] = ("strings", "retail_port_strings writes the English strings as Sol string-bank rows (hashes kept)."),
        [0x808099F1] = ("strings", "String data travels with its 0x808099EF container (retail_port_strings)."),
        [0x80808E8E] = ("mission", "retail_port_mission turns the activity into a Sol activity document and a mission script skeleton."),
        [0x80808E8B] = ("mission", "Destinations are read by retail_port_mission for names and activity lists."),
        [0x8080891E] = ("map_view", "Bubbles open read-only in the Retail workspace viewport; Sol map packages are not written."),
        [0x80806D44] = ("geometry", "Static meshes decode to untextured triangles; writing 86657 static meshes is not implemented."),
        [0x80806C81] = ("geometry", "Terrain decodes to untextured triangles; writing 86657 terrain is not implemented."),
        [0x8080799D] = ("investment", "Investment definitions: the retail layout is not verified; no converter yet."),
        [0x8080549F] = ("investment", "Investment data: the retail layout is not verified; no converter yet."),
    };

    /** Plans a port of root: walks references depth levels deep, at most maxTags tags. */
    public static RetailPortPlan Plan(RetailSource source, uint root, int depth = 2, int maxTags = 400, CancellationToken cancel = default)
    {
        var notes = new List<string>();
        if (source.Index is null) notes.Add("The tag index is not available (" + source.IndexError + "); classes are unknown and only 64-bit references are followed.");
        var walk = RetailReferences.Walk(source, root, depth, maxTags, cancel);
        var items = new List<RetailPortItem>(walk.Count);
        foreach (var (tag, level, parent) in walk)
        {
            var entry = source.Index?.Find(tag);
            uint? type = entry is { IsStruct: true } structEntry ? structEntry.Reference : null;
            var (status, converter, reason, sol) = Judge(source, tag, entry, type);
            items.Add(new RetailPortItem(tag, level, parent, type, type is { } known ? source.ClassName(known) : FileTypeName(entry), entry?.Type ?? 0,
                entry?.Subtype ?? 0, (int)(entry?.Size ?? 0), status, converter, reason, sol));
        }
        notes.Add("References are found by scanning aligned words for indexed tags and resolvable 64-bit hashes; a few may be coincidental.");
        return new RetailPortPlan(root, items, walk.Count >= maxTags, notes);
    }

    private static (RetailPortStatus, string, string, uint?) Judge(RetailSource source, uint tag, RetailEntry? entry, uint? type)
    {
        if (entry is null) return (RetailPortStatus.Blocked, "", "Not in the retail index.", null);
        if (entry.Value.Size > 0 && source.PayloadPath(tag) is null) return (RetailPortStatus.Blocked, "", "No payload in the dump (redacted package or media drive missing).", null);
        if (type is not { } retailClass)
            return entry.Value.Type switch
            {
                32 or 40 => (RetailPortStatus.NeedsConverter, "geometry", "Vertex/index buffer: decoded for the viewport; no 86657 buffer writer yet.", null),
                33 or 41 => (RetailPortStatus.Blocked, "", "Texture data: retail texture formats are not converted yet.", null),
                26 => (RetailPortStatus.Blocked, "", "Audio data: not converted.", null),
                _ => (RetailPortStatus.Blocked, "", $"Raw data of file type {entry.Value.Type}/{entry.Value.Subtype}: no converter.", null)
            };
        var rows = source.ClassMap.ForRetail(retailClass);
        uint? sol = source.ClassMap.SolClassOf(retailClass);
        if (Converters.TryGetValue(retailClass, out var known))
            return (RetailPortStatus.NeedsConverter, known.Converter, known.Note, sol);
        if (rows.Count == 0) return (RetailPortStatus.Blocked, "", "No 86657 class is known for this retail class.", null);
        if (sol is null) return (RetailPortStatus.Blocked, "", "The class map has no single 86657 class for it (" + rows[0].Status + ").", null);
        var row = rows.First(candidate => candidate.SolClass == sol);
        if (row.SolSize is { } solSize && row.RetailSize is { } retailSize && solSize != retailSize)
            return (RetailPortStatus.NeedsConverter, "layout", $"86657 class {RetailTag.Hex(sol.Value)} is {solSize} bytes, retail {retailSize}: fields moved.", sol);
        if (row.Status != "corroborated")
            return (RetailPortStatus.NeedsConverter, "layout", $"Mapping to {RetailTag.Hex(sol.Value)} is {row.Status}; check the layout before porting.", sol);
        return (RetailPortStatus.Portable, "marker_relink", $"Same size as 86657 {RetailTag.Hex(sol.Value)} ({row.Name}); needs the array marker rewrite and reference relinking.", sol);
    }

    private static string FileTypeName(RetailEntry? entry) => entry switch
    {
        null => "unknown",
        { Type: 32, Subtype: 4 } or { Type: 40, Subtype: 4 } => "vertex buffer header",
        { Type: 32, Subtype: 6 } or { Type: 40, Subtype: 6 } => "index buffer header",
        { Type: 32 } or { Type: 40 } => "buffer header",
        { Type: 33 } or { Type: 41 } => "texture",
        { Type: 26 } => "audio",
        { } value => $"file type {value.Type}/{value.Subtype}"
    };
}
