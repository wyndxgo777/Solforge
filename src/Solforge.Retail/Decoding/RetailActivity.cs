/**
 * File: Retail activities (SActivity 0x80808E8E) and destinations (SDestination 0x80808E8B). Layouts follow alkahest
 * and were checked on schism (destination 0x80A7E28D, freeroam activity 0x80E5A897): destination WideHash, the phase
 * list with bubble names and map references (to SBubbleParent), and entity links -> 0x80808E89 -> 0x80808EBE ->
 * 0x80808943 resources -> 0x80809B06 entity resources.
 */
namespace Solforge.Retail.Decoding;

/** One entity link of a phase: the resource-list tag and the entity resources it reaches (0x80809B06 tags). */
public sealed record RetailEntityLink(uint PhaseHash, uint Phase2Hash, uint LinkTag, IReadOnlyList<uint> EntityResources);

/** One phase of an activity (alkahest SUnk80808924): a bubble with its map references and entity links. */
public sealed record RetailActivityPhase(int Index, uint LocationHash, uint ActivityHash, uint BubbleHash, IReadOnlyList<uint> BubbleParents,
    int UnresolvedMaps, IReadOnlyList<RetailEntityLink> Entities);

/** A decoded activity. Names come from the destination's activity list and the lookup cache's named tags. */
public sealed record RetailActivityInfo(uint Tag, string? Name, uint LocationHash, uint ActivityHash, uint? Destination,
    uint? AmbientActivity, IReadOnlyList<RetailActivityPhase> Phases, IReadOnlyList<string> Notes);

/** One row of a destination's activity list. Tag is found through the named tags (null when unnamed there). */
public sealed record RetailDestinationActivity(uint Hash, string Name, uint? Tag);

/** A decoded destination. */
public sealed record RetailDestinationInfo(uint Tag, string? Name, uint LocationHash, uint? StringContainer, uint? Events, uint? Patrols,
    IReadOnlyList<RetailDestinationActivity> Activities, IReadOnlyList<string> Notes);

/** Decodes activities and destinations. */
public static class RetailActivity
{
    public const uint ActivityClass = 0x80808E8E, DestinationClass = 0x80808E8B;
    private const uint PhaseClass = 0x80808924, LinkClass = 0x80808948, DestinationActivityClass = 0x8080892E;

    /** Decodes an activity tag; null with problem when it has no payload or its arrays do not decode. */
    public static RetailActivityInfo? ReadActivity(RetailSource source, uint tag, out string? problem, bool entities = true)
    {
        problem = null;
        if (source.Read(tag) is not { } bytes) { problem = $"{RetailTag.Hex(tag)} has no payload."; return null; }
        if (bytes.Length < 0x88) { problem = $"{RetailTag.Hex(tag)} is {bytes.Length} bytes; an SActivity is 0x134."; return null; }
        var notes = new List<string>();
        uint? destination = source.Wide(bytes, 0x20), ambient = source.Wide(bytes, 0x78);
        if (destination is null && !RetailBytes.Wide(bytes, 0x20).IsNone) notes.Add("The destination's 64-bit hash is not in the lookup cache.");
        if (!RetailBytes.TryArray(bytes, 0x50, out var phases) || phases.Count > 0 && phases.Class != PhaseClass)
        { problem = "The phase array at +0x50 does not decode as 0x80808924 (is this an SActivity?)."; return null; }
        var list = new List<RetailActivityPhase>(phases.Count);
        for (int i = 0; i < phases.Count; i++)
        {
            int at = phases.Data + i * 0x48;
            var maps = new List<uint>();
            int unresolved = 0;
            if (RetailBytes.TryArray(bytes, at + 0x28, out var refs))
                for (int k = 0; k < refs.Count; k++)
                {
                    if (source.Wide(bytes, refs.Data + k * 16) is { } parent) maps.Add(parent);
                    else if (!RetailBytes.Wide(bytes, refs.Data + k * 16).IsNone) unresolved++;
                }
            var links = new List<RetailEntityLink>();
            if (RetailBytes.TryArray(bytes, at + 0x18, out var linkArray) && (linkArray.Count == 0 || linkArray.Class == LinkClass))
                for (int k = 0; k < linkArray.Count; k++)
                {
                    int link = linkArray.Data + k * 0x18;
                    uint linkTag = RetailBytes.U32(bytes, link + 0x14);
                    links.Add(new RetailEntityLink(RetailBytes.U32(bytes, link + 0x0C), RetailBytes.U32(bytes, link + 0x10), linkTag,
                        entities ? EntityResources(source, linkTag) : []));
                }
            list.Add(new RetailActivityPhase(i, RetailBytes.U32(bytes, at), RetailBytes.U32(bytes, at + 4), RetailBytes.U32(bytes, at + 8),
                maps, unresolved, links));
        }
        if (list.Sum(phase => phase.UnresolvedMaps) is > 0 and var missing) notes.Add($"{missing} map references have 64-bit hashes the lookup cache lacks.");
        return new RetailActivityInfo(tag, source.NameOf(tag), RetailBytes.U32(bytes, 0x08), RetailBytes.U32(bytes, 0x0C), destination, ambient, list, notes);
    }

    /** 0x80808E89 link -> +0x18 0x80808EBE -> +0x08 array of 0x80808943 tags -> +0x20 entity resource tag. */
    public static IReadOnlyList<uint> EntityResources(RetailSource source, uint linkTag)
    {
        if (source.Read(linkTag) is not { } link) return [];
        if (source.Read(RetailBytes.U32(link, 0x18)) is not { } list || !RetailBytes.TryArray(list, 0x08, out var resources)) return [];
        var result = new List<uint>(resources.Count);
        for (int i = 0; i < resources.Count; i++)
            if (source.Read(RetailBytes.U32(list, resources.Data + i * 4)) is { Length: >= 0x24 } resource
                && RetailBytes.U32(resource, 0x20) is var entity && RetailTag.Plausible(entity)) result.Add(entity);
        return result;
    }

    /** Decodes a destination tag. */
    public static RetailDestinationInfo? ReadDestination(RetailSource source, uint tag, out string? problem)
    {
        problem = null;
        if (source.Read(tag) is not { } bytes) { problem = $"{RetailTag.Hex(tag)} has no payload."; return null; }
        if (bytes.Length < 0x60) { problem = $"{RetailTag.Hex(tag)} is too small for an SDestination."; return null; }
        var notes = new List<string>();
        uint? strings = source.Wide(bytes, 0x10);
        if (strings is null && !RetailBytes.Wide(bytes, 0x10).IsNone) notes.Add("The string container's 64-bit hash is not in the lookup cache.");
        var activities = new List<RetailDestinationActivity>();
        if (RetailBytes.TryArray(bytes, 0x48, out var rows) && (rows.Count == 0 || rows.Class == DestinationActivityClass))
        {
            var named = source.Lookup.Named.Where(row => row.Class == ActivityClass).GroupBy(row => row.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Tag, StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; i++)
            {
                int at = rows.Data + i * 0x18;
                string name = RetailBytes.TryRelative(bytes, at + 0x10, out int text) ? RetailBytes.CString(bytes, text) ?? "" : "";
                activities.Add(new RetailDestinationActivity(RetailBytes.U32(bytes, at), name, named.TryGetValue(name, out uint found) ? found : null));
            }
        }
        else notes.Add("The activity list at +0x48 does not decode.");
        string? destinationName = RetailBytes.TryRelative(bytes, 0x58, out int nameAt) ? RetailBytes.CString(bytes, nameAt) : null;
        static uint? Tag(uint value) => RetailTag.Plausible(value) ? value : null;
        return new RetailDestinationInfo(tag, destinationName ?? source.NameOf(tag), RetailBytes.U32(bytes, 0x08), strings,
            Tag(RetailBytes.U32(bytes, 0x20)), Tag(RetailBytes.U32(bytes, 0x24)), activities, notes);
    }

    /** Named destinations from the lookup cache (class 0x80808E8B), by name. */
    public static IReadOnlyList<RetailNamedTag> Destinations(RetailSource source) =>
        source.Lookup.Named.Where(row => row.Class == DestinationClass).DistinctBy(row => row.Tag).OrderBy(row => row.Name, StringComparer.Ordinal).ToList();

    /** Named activities from the lookup cache whose name contains query (all when empty). */
    public static IReadOnlyList<RetailNamedTag> Activities(RetailSource source, string? query) =>
        source.Lookup.Named.Where(row => row.Class == ActivityClass && (string.IsNullOrEmpty(query) || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(row => row.Tag).OrderBy(row => row.Name, StringComparer.Ordinal).ToList();
}
