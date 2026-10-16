using System.Buffers;
using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** Link to another asset, by 32-bit tag or by a wide hash resolved through the catalog. */
internal readonly record struct IsolationReference(uint Tag, ulong Wide)
{
    /** Tag the reference points at, or null when a wide hash is not installed. */
    internal uint? Resolve(PackageCatalog catalog) => Tag == uint.MaxValue ? catalog.Resolve64(Wide) : Tag;
}
/** Decoded isolation facts for one asset: class, links, placements, maps and children. */
internal sealed record IsolationNode(uint Tag, uint Class, bool Valid, uint Link,
    InstanceIsolationScope.Placement[] Placements, uint[] Maps, IsolationReference[] Children, uint[] OwnerCandidates);

/** Facts retain typed links and offsets, never whole placement payloads or cached wide-tag resolutions. */
internal static class InstanceIsolationFacts
{
    internal static readonly HashSet<uint> Classes =
    [0x80806EF4, 0x808099D6, 0x808091DE, 0x80807DAE, 0x808091E0, 0x80808A54, 0x8080925B, 0x8080925E];

    /** Decodes one asset's bytes into an isolation node; owners selects which placements are kept. */
    internal static IsolationNode Parse(uint tag, uint type, byte[] bytes, IReadOnlySet<uint> owners)
    {
        var source = new NativeRows(bytes);
        try
        {
            uint link = 0;
            var placements = new List<InstanceIsolationScope.Placement>();
            var maps = new List<uint>();
            var children = new List<IsolationReference>();
            switch (type)
            {
                case 0x80806EF4:
                    source.Span(0x20, 2); link = source.U32(8); break;
                case 0x808099D6:
                    int[] rows = source.Rows(8, 0x808099D8, 144);
                    for (int index = 0; index < rows.Length; index++)
                        foreach (int auxiliary in InstanceIsolationScope.Chain(source, rows[index] + 0x78))
                        {
                            uint marker = source.U32(auxiliary - 4);
                            if (marker is not (0x808071B3 or 0x80807246)) continue;
                            source.Span(auxiliary, 24);
                            uint target = source.U32(auxiliary + 0x10);
                            if (marker == 0x808071B3) placements.Add(new(tag, rows[index], index, auxiliary, target));
                            else maps.Add(target);
                        }
                    break;
                case 0x808091DE: References(0x10, 0x80807D53); break;
                case 0x80807DAE: link = source.U32(8); break;
                case 0x808091E0: References(8, 0x808084C1); break;
                case 0x80808A54:
                    foreach (int row in source.Rows(0x28, 0x80808BB0, 4)) children.Add(new(source.U32(row), 0));
                    break;
                case 0x8080925B: link = source.U32(20); break;
                case 0x8080925E:
                    foreach (int field in new[] { 8, 24, 40 })
                        foreach (int row in source.Rows(field, 0x80809260, 4)) children.Add(new(source.U32(row), 0));
                    break;
                default: return new(tag, type, false, 0, [], [], [], []);
            }
            return new(tag, type, true, link, placements.ToArray(), maps.Distinct().ToArray(), children.ToArray(), []);

            void References(int field, uint element)
            {
                foreach (int row in source.Rows(field, element, 16))
                {
                    uint narrow = source.U32(row);
                    children.Add(new(narrow, narrow == uint.MaxValue ? source.U64(row + 8) : 0));
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
        {
            return new(tag, type, false, 0, [], [], [], CandidateOwners(bytes, owners));
        }
    }

    /** Even an unaligned malformed constructor must remain a candidate for scoped refusal. */
    internal static uint[] CandidateOwners(byte[] bytes, IReadOnlySet<uint> owners)
    {
        if (owners.Count == 0) return [];
        var found = new HashSet<uint>();
        var highBytes = SearchValues.Create(owners.Select(owner => (byte)(owner >> 24)).Distinct().ToArray());
        for (int at = 0; at <= bytes.Length - 4; at++)
        {
            int skip = bytes.AsSpan(at + 3).IndexOfAny(highBytes);
            if (skip < 0) break;
            at += skip;
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
            if (owners.Contains(value)) found.Add(value);
        }
        return found.Order().ToArray();
    }
}

/** Current raw changes replace original facts only within one compile. */
internal sealed class InstanceIsolationSources
{
    private readonly ObjectEditContext context;
    private readonly IReadOnlyDictionary<uint, IsolationNode>? original;
    private readonly HashSet<uint> owners;
    private readonly Dictionary<uint, IsolationNode> current = [];

    internal InstanceIsolationSources(ObjectEditContext context)
    {
        this.context = context;
        owners = context.Catalog.Entries.Where(entry => entry.Reference == 0x80806EF4).Select(entry => entry.TagHash).ToHashSet();
        original = context.IsolationSnapshot?.Nodes;
    }

    /** Nodes of every installed asset of a class. */
    internal IEnumerable<IsolationNode> OfClass(uint type) => context.Catalog.Entries
        .Where(entry => entry.Reference == type).Select(entry => Read(entry.TagHash));

    /** Node for a tag, taken from the edit layer, the original snapshot or a fresh parse. */
    internal IsolationNode Read(uint tag)
    {
        if (current.TryGetValue(tag, out var node)) return node;
        if (original is not null && !context.Options.RawChangedTags!.Contains(tag)
            && original.TryGetValue(tag, out node)) return node;
        uint type = context.Catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;
        if (!InstanceIsolationFacts.Classes.Contains(type)) return new(tag, type, false, 0, [], [], [], []);
        return current[tag] = InstanceIsolationFacts.Parse(tag, type, context.ReadLayer(tag), owners);
    }
}
