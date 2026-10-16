using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Counts of vendors, sales and categories a vendor table currently holds. */
public sealed record InvestmentVendorBudget(int Vendors, long Sales, long Categories);
/** A vendor interaction tag: the vendor hash it targets, where that hash is stored, and the SHA-256 of the source bytes. */
public sealed record InvestmentVendorInteraction(uint Tag, uint VendorHash, int HashOffset, string SourceSha256);

/** Derived vendors retain base policy fields while their indexed identity and display text are authored. */
public static class InvestmentVendorAuthoring
{
    public const uint DisplayClass = InvestmentTags.VendorDisplayClass, IndexClass = 0x8080784A, DisplayIndexClass = 0x808058E8;
    public const uint IndexRowClass = 0x8080784E, DisplayIndexRowClass = 0x808058EC;
    public const uint InteractionPrimaryClass = 0x808063B0, InteractionResourceClass = 0x808063B1;
    /** Native hash scans increment a signed 16-bit cursor, so 32768 rows would wrap. */
    public const int MaximumVendors = short.MaxValue;
    /** These aggregate limits match the runtime catalog's bounded row banks. */
    public const int MaximumSales = 16384, MaximumCategories = 4096, MaximumCosts = 4;
    public const int NameOffset = 64, SubtitleOffset = 72, IconOffset = 88;
    private const uint EmptyHash = 0x811C9DC5, ConfigClass = 0x80809C36;

    /** Both indices must retain one unique hash at each matching native row position. */
    public static InvestmentVendorBudget ReadBudget(PackageCatalog catalog, InvestmentSources? sources = null,
        CancellationToken token = default)
    {
        sources ??= new();
        RequireClass(catalog, sources.Vendors, IndexClass);
        RequireClass(catalog, sources.VendorDisplays, DisplayIndexClass);
        var gameplay = new TagReader(catalog.Read(sources.Vendors));
        var display = new TagReader(catalog.Read(sources.VendorDisplays));
        var definitions = gameplay.Array(8, IndexRowClass, InvestmentItemClone.IndexStride);
        var displays = display.Array(8, DisplayIndexRowClass, InvestmentItemClone.IndexStride);
        if (definitions.Count != displays.Count || definitions.Count > MaximumVendors)
            throw new InvalidDataException("Vendor index counts do not fit their paired native range.");
        HashSet<uint> seen = [];
        long sales = 0, categories = 0;
        for (int i = 0; i < definitions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int at = definitions.Data + i * InvestmentItemClone.IndexStride;
            int other = displays.Data + i * InvestmentItemClone.IndexStride;
            uint hash = gameplay.U32(at), tag = gameplay.U32(at + 16);
            if (hash is 0 or uint.MaxValue or EmptyHash || !seen.Add(hash) || display.U32(other) != hash)
                throw new InvalidDataException("Vendor index identities are missing, ambiguous or unpaired.");
            RequireClass(catalog, tag, InvestmentCatalog.VendorClass);
            RequireClass(catalog, display.U32(other + 16), DisplayClass);
            var definition = Definition(catalog.Read(tag), hash);
            sales += definition.Array(48, 0x80807861, 184).Count;
            categories += definition.Array(32, 0x80807860, 24).Count;
        }
        return new(definitions.Count, sales, categories);
    }

    /** Throws if adding the given vendors, sales or categories would exceed the table limits. */
    public static void ValidateBudget(InvestmentVendorBudget current, int addedVendors, int addedSales, int addedCategories)
    {
        if (addedVendors < 0 || current.Vendors + (long)addedVendors > MaximumVendors)
            throw new InvalidDataException($"The native vendor index supports at most {MaximumVendors:N0} rows.");
        if (current.Sales + addedSales is < 0 or > MaximumSales)
            throw new InvalidDataException($"The runtime catalog supports at most {MaximumSales:N0} vendor sale rows in total.");
        if (current.Categories + addedCategories is < 0 or > MaximumCategories)
            throw new InvalidDataException($"The runtime catalog supports at most {MaximumCategories:N0} vendor category rows in total.");
    }

    /** Unknown non-stock policies may retain sale ordinals, so only empty-policy roots permit shifts. */
    public static void ValidateSaleOrder(ReadOnlyMemory<byte> bytes, int sourceCount, IReadOnlyList<int> sourceIndices)
    {
        bool shifts = sourceIndices.Count < sourceCount
            || Enumerable.Range(0, Math.Min(sourceCount, sourceIndices.Count)).Any(index => sourceIndices[index] != index);
        if (!shifts) return;
        var data = new TagReader(bytes);
        if (new[] { 64, 80, 96 }.Any(field => data.U64(field) != 0))
            throw new NotSupportedException("This base has additional policies whose sale references are not decoded. Keep its sale order or choose a base without those policies.");
    }

    /** Only the verified primary identity changes; the distinct secondary key and base policies remain. */
    public static (byte[] Definition, byte[] Display) Derive(ReadOnlyMemory<byte> definition,
        ReadOnlyMemory<byte> display, uint sourceHash, uint hash)
    {
        Definition(definition, sourceHash);
        Display(display, sourceHash);
        if (hash is 0 or uint.MaxValue or EmptyHash || hash == sourceHash)
            throw new InvalidDataException("Choose a new nonempty vendor identity.");
        byte[] nextDefinition = definition.ToArray(), nextDisplay = display.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(nextDefinition.AsSpan(8), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(nextDisplay.AsSpan(8), hash);
        return (nextDefinition, nextDisplay);
    }

    /** Display strings preserve the adjacent opaque 16 bits and replace only bank and hash. */
    public static byte[] SetText(ReadOnlyMemory<byte> display, uint identity, int offset, int bank, uint hash)
    {
        Display(display, identity);
        if (offset is not (NameOffset or SubtitleOffset) || bank is < 0 or > short.MaxValue)
            throw new InvalidDataException("The vendor string target is outside its native range.");
        byte[] result = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset), checked((ushort)bank));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4), hash);
        return result;
    }

    /** Reads the (bank, hash) name or subtitle reference of the display with the given identity; other offsets throw. */
    public static (ushort Bank, uint Hash) ReadText(ReadOnlyMemory<byte> display, uint identity, int offset)
    {
        var data = Display(display, identity);
        if (offset is not (NameOffset or SubtitleOffset)) throw new ArgumentOutOfRangeException(nameof(offset));
        return (data.U16(offset), data.U32(offset + 4));
    }

    /** Returns the display with its icon copied from another vendor's display. */
    public static byte[] CopyIcon(ReadOnlyMemory<byte> display, uint identity,
        ReadOnlyMemory<byte> source, uint sourceIdentity)
    {
        Display(display, identity);
        ushort value = Display(source, sourceIdentity).U16(IconOffset);
        byte[] result = display.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(IconOffset), value);
        return result;
    }

    /** Reciprocal config links prove which resource the native activation handler reads. */
    public static InvestmentVendorInteraction ReadInteraction(PackageCatalog catalog, uint tag, ReadOnlyMemory<byte> bytes)
    {
        RequireClass(catalog, tag, ConfigClass);
        var data = new TagReader(bytes);
        if (!data.Has(0, 32) || data.U64(0) != (ulong)bytes.Length)
            throw new InvalidDataException("Interaction config size is invalid.");
        int primary = data.Relative(16), resource = data.Relative(24);
        if (primary < 4 || resource < 4 || !data.Has(primary, 16) || !data.Has(resource, 760)
            || data.U32(primary - 4) != InteractionPrimaryClass || data.U32(resource - 4) != InteractionResourceClass
            || data.U32(primary) != tag || data.U32(primary + 4) != InteractionResourceClass
            || data.U64(primary + 8) != (ulong)resource || data.U32(resource) != tag
            || data.U32(resource + 4) != InteractionPrimaryClass || data.U64(resource + 8) != (ulong)primary)
            throw new InvalidDataException("Choose a verified vendor interaction config.");
        int offset = checked(resource + 752);
        return new(tag, data.U32(offset), offset, Convert.ToHexString(SHA256.HashData(bytes.Span)));
    }

    /** Returns the interaction bytes with the vendor hash replaced; throws if the interaction changed or the hash is empty. */
    public static byte[] RetargetInteraction(PackageCatalog catalog, ReadOnlyMemory<byte> bytes,
        InvestmentVendorInteraction target, uint hash)
    {
        if (ReadInteraction(catalog, target.Tag, bytes) != target)
            throw new InvalidDataException("The interaction changed. Choose it again.");
        if (hash is 0 or uint.MaxValue or EmptyHash) throw new InvalidDataException("Vendor identity is empty.");
        byte[] result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(target.HashOffset), hash);
        return result;
    }

    private static TagReader Definition(ReadOnlyMemory<byte> bytes, uint identity)
    {
        var data = new TagReader(bytes);
        if (!data.Has(0, 120) || data.U64(0) != (ulong)bytes.Length || data.U32(8) != identity)
            throw new InvalidDataException("Vendor definition identity or size differs from its index.");
        return data;
    }

    private static TagReader Display(ReadOnlyMemory<byte> bytes, uint identity)
    {
        var data = new TagReader(bytes);
        if (!data.Has(0, 216) || data.U64(0) != (ulong)bytes.Length || data.U32(8) != identity)
            throw new InvalidDataException("Vendor display identity or size differs from its index.");
        return data;
    }

    private static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException("Vendor asset has an unexpected native class.");
    }
}
