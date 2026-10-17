using SunriseStudio.Projects;

/** T1.5 field-level compare/revert with base (TagBaseDiff) and T1.4 item reference walks (ItemReferenceScan); no game install. */
internal static class ItemSourceChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        CompareWithBase();
        RevertField();
        ReferenceScan();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("item sources: " + message);
    }

    private static void CompareWithBase()
    {
        byte[] baseBytes = new byte[32], authored = new byte[36];
        BitConverter.GetBytes(1.5f).CopyTo(baseBytes, 8);
        BitConverter.GetBytes(2.25f).CopyTo(authored, 8);
        baseBytes[20] = 1; authored[20] = 9; authored[21] = 9;   // undecoded bytes
        authored[33] = 7;                                      // grown tail
        var fields = new List<TagFieldSpan> { new("Stats / Range", 8, 4, "f32", "decoder:Investment"), new("Flags", 0, 4, "hash", "decoder:Investment") };
        var comparison = TagBaseDiff.Compare(new ProjectTagDiff(0x80A1B2C3, baseBytes, authored), fields);
        Require(comparison.Fields.Count == 1 && comparison.Fields[0].Field.Name == "Stats / Range"
            && comparison.Fields[0].Base == "1.5" && comparison.Fields[0].Authored == "2.25", "a decoded field shows both values");
        Require(comparison.Ranges.Count == 2 && comparison.Ranges[0] is { Offset: 20, Length: 2, Base: "0100", Authored: "0909" },
            "undecoded bytes merge into one range: " + string.Join(";", comparison.Ranges));
        Require(comparison.Ranges[1] is { Offset: 32, Length: 4, Base: null, Authored: "00070000" }, "a size change is a tail range with no base side");
        var one = TagBaseDiff.Compare(new ProjectTagDiff(1, baseBytes, authored), fields, "stats / range");
        Require(one.Fields.Count == 1 && one.Ranges.Count == 0, "field narrows to one named field, case-insensitive");
        var range = TagBaseDiff.Compare(new ProjectTagDiff(1, baseBytes, authored), fields, "0x14+2");
        Require(range.Fields.Count == 0 && range.Ranges.Count == 1 && range.Ranges[0].Offset == 20, "a byte range narrows to overlapping ranges");
        Require(TagBaseDiff.Find(fields, "0x8")?.Name == "Stats / Range" && TagBaseDiff.Find(fields, "nope") is null
            && TagBaseDiff.Find(fields, "0x40+8") is { Offset: 0x40, Size: 8, Source: "range" }, "fields resolve by name, offset or range");
        Require(TagBaseDiff.Compare(new ProjectTagDiff(1, baseBytes, (byte[])baseBytes.Clone()), fields) is { Fields.Count: 0, Ranges.Count: 0 }
            && new ProjectTagDiff(1, baseBytes, (byte[])baseBytes.Clone()).Identical, "identical tags report nothing");
    }

    private static void RevertField()
    {
        byte[] baseBytes = [1, 2, 3, 4, 5, 6, 7, 8], authored = [1, 9, 3, 4, 5, 9, 9, 8];
        var reverted = TagBaseDiff.RevertField(baseBytes, authored, new("x", 4, 4, "u32", "decoder"));
        Require(reverted.SequenceEqual(new byte[] { 1, 9, 3, 4, 5, 6, 7, 8 }), "reverting a field keeps the other edits");
        Require(authored[5] == 9, "revert does not touch the authored array");
        bool refused = false;
        try { TagBaseDiff.RevertField(baseBytes, [1, 2], new("x", 4, 4, "u32", "decoder")); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "a field past the end of either side is refused");
    }

    private static void ReferenceScan()
    {
        // activity A -> B -> item tag; activity C -> D (holds the item hash); E -> item, but E is not under any root.
        const uint A = 0x80800001, B = 0x80800002, C = 0x80800003, D = 0x80800004, E = 0x80800005, Item = 0x80800010, Display = 0x80800011, Hash = 0xDEADBEEF;
        var edges = new Dictionary<uint, uint[]> { [A] = [B], [B] = [Item], [C] = [D], [E] = [Item], [Item] = [Display] };
        byte[] holder = new byte[16];
        BitConverter.GetBytes(Hash).CopyTo(holder, 8);
        var result = ItemReferenceScan.Scan([A, C], tag => edges.GetValueOrDefault(tag, []), tag => tag == D ? holder : null,
            new HashSet<uint> { Item, Display }, Hash);
        Require(result.Hits.Count == 2, "hits: " + string.Join(";", result.Hits));
        Require(result.Hits.Any(hit => hit is { Tag: B, Via: A, Hash: false }), "a tag reference is found with its activity root");
        Require(result.Hits.Any(hit => hit is { Tag: D, Via: C, Hash: true, Offset: 8 }), "a hash reference is found at its offset");
        Require(!result.Truncated, "a small walk is not truncated");
        var bounded = ItemReferenceScan.Scan([A, C], tag => edges.GetValueOrDefault(tag, []), _ => null, new HashSet<uint> { Item }, 0, maximumNodes: 2);
        Require(bounded.Truncated && bounded.Hits.Count == 0, "the node bound stops the walk and says so");
        var manifest = new DependencyManifest("p", [new(E, "changed", "Investment", [Item]), new(B, "changed", "Investment", [A])], []);
        Require(ItemReferenceScan.ProjectReferences(manifest, new HashSet<uint> { Item }).Select(node => node.Tag).SequenceEqual([E]),
            "project edits that reference the item come from the dependency manifest");
    }
}
