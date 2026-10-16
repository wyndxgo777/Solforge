namespace SunriseStudio.Formats;

/** One entry of the installed label table: a single label with its bit, or a group with its mask. */
public sealed record PerkLabel(uint Hash, string Name, int Bit, bool Group);

/** The installed label table (tag 0x80C70CA1): label hashes, whose index is their bit, and label groups. */
public sealed class PerkLabelTable
{
    /** Tag of the installed label table. */
    public const uint Tag = 0x80C70CA1;
    // +8 is the u32 label hash array; +24 holds 44-byte groups {u32 hash, 40-byte mask}.
    private const uint HashClass = 0x80800070, GroupClass = 0x808094BE;
    // A compiled label set is 320 bits.
    public const int MaskBytes = 40;

    /** FNV-1 names that match label table rows. */
    private static readonly string[] KnownNames =
    [
        "attached", "charged", "projectile", "bfg", "push", "sparrow", "tiger", "hunter", "primary", "special", "titan",
        "warlock", "female", "male", "ghost", "collision", "heal", "slow", "blink", "boost", "jump", "base", "overload",
        "pierce", "stagger", "precision", "player"
    ];

    private readonly Dictionary<uint, byte[]> masks = [];

    /** The table the compiler uses; set from the census result and cleared when an install opens. */
    public static PerkLabelTable? Installed { get; set; }

    public IReadOnlyList<PerkLabel> Entries { get; }

    public PerkLabelTable(IReadOnlyList<uint> labels, IReadOnlyList<(uint Hash, byte[] Mask)> groups)
    {
        var names = KnownNames.ToDictionary(InvestmentItemClone.Fnv1, name => name);
        var entries = new List<PerkLabel>();
        for (int bit = 0; bit < Math.Min(labels.Count, MaskBytes * 8); bit++)
        {
            byte[] mask = new byte[MaskBytes];
            mask[bit / 8] = (byte)(1 << bit % 8);
            if (masks.TryAdd(labels[bit], mask))
                entries.Add(new(labels[bit], names.GetValueOrDefault(labels[bit]) ?? $"0x{labels[bit]:X8}", bit, false));
        }
        foreach (var (hash, mask) in groups)
            if (mask.Length == MaskBytes && masks.TryAdd(hash, (byte[])mask.Clone()))
                entries.Add(new(hash, "group " + (names.GetValueOrDefault(hash) ?? $"0x{hash:X8}"), -1, true));
        Entries = entries;
    }

    /** Reads the installed table tag; null when the arrays are missing or malformed. */
    public static PerkLabelTable? Parse(ReadOnlyMemory<byte> bytes)
    {
        var data = new TagReader(bytes);
        if (!data.TryArray(8, HashClass, 4, out var hashes) || !data.TryArray(24, GroupClass, 44, out var groups)) return null;
        return new(
            [.. Enumerable.Range(0, hashes.Count).Select(i => data.U32(hashes.Data + i * 4))],
            [.. Enumerable.Range(0, groups.Count).Select(i => groups.Data + i * 44)
                .Select(row => (data.U32(row), bytes.Span.Slice(row + 4, MaskBytes).ToArray()))]);
    }

    /** True when the label or group hash is in the table. */
    public bool Contains(uint hash) => masks.ContainsKey(hash);

    /** ORs a label's or group's mask into the 40-byte set; false for a hash the table lacks. */
    public bool AddTo(uint hash, byte[] set)
    {
        if (!masks.TryGetValue(hash, out var mask)) return false;
        for (int i = 0; i < MaskBytes; i++) set[i] |= mask[i];
        return true;
    }

    /** Display name of a label hash, or a marked unknown hex value. */
    public string NameOf(uint hash) => Entries.FirstOrDefault(entry => entry.Hash == hash)?.Name ?? $"0x{hash:X8} (unknown)";
}
