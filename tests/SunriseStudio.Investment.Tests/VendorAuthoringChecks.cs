using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

/** Checks vendor authoring edits: derived vendors, text, interaction retargeting and cost duplication affect only their own fields. */
internal static class VendorAuthoringChecks
{
    /** Builds a vendor interaction record for the tag and hash. */
    internal static byte[] Interaction(uint tag, uint hash)
    {
        byte[] bytes = new byte[1024];
        Q(bytes, 0, bytes.Length); Q(bytes, 16, 128 - 16); Q(bytes, 24, 192 - 24);
        U(bytes, 124, 0x808063B0); U(bytes, 128, tag); U(bytes, 132, 0x808063B1); Q(bytes, 136, 192);
        U(bytes, 188, 0x808063B1); U(bytes, 192, tag); U(bytes, 196, 0x808063B0); Q(bytes, 200, 128);
        U(bytes, 944, hash); U(bytes, 948, 0x811C9DC5);
        return bytes;
    }

    /** Independent byte reads check identities, paired selector ordinals and relocated expression targets. */
    internal static void Run(PackageCatalog packages, InvestmentCatalog catalog, InvestmentSources sources,
        uint tag, uint displayTag, uint interactionTag)
    {
        byte[] game = packages.Read(tag), display = packages.Read(displayTag), config = packages.Read(interactionTag);
        var summary = catalog.FindVendor(tag)!;
        var budget = InvestmentVendorAuthoring.ReadBudget(packages, sources);
        Check(budget is { Vendors: 1, Sales: 1, Categories: 0 }, "Native budget differs.");
        var derived = InvestmentVendorAuthoring.Derive(game, display, summary.Hash, 0x12345678);
        Check(U32(derived.Definition, 8) == 0x12345678 && U32(derived.Display, 8) == 0x12345678
            && U32(derived.Definition, 12) == 0x11223344 && Outside(game, derived.Definition, 8, 4),
            "Derivation changed inherited policy or missed its own identity.");
        byte[] named = display.ToArray(); S(named, 66, 0xCAFE);
        var text = InvestmentVendorAuthoring.SetText(named, summary.Hash, 64, 9, 0x87654321);
        Check(U16(text, 64) == 9 && U16(text, 66) == 0xCAFE && U32(text, 68) == 0x87654321,
            "Vendor text overwrote its adjacent opaque word.");
        Reject(() => InvestmentVendorAuthoring.SetText(display, summary.Hash, 64, 32768, 1));
        InvestmentVendorAuthoring.ValidateBudget(new(32766, 16000, 4000), 1, 384, 96);
        Reject(() => InvestmentVendorAuthoring.ValidateBudget(new(32767, 0, 0), 1, 0, 0));
        Reject(() => InvestmentVendorAuthoring.ValidateBudget(budget, 1, 16384, 0));
        var site = InvestmentVendorAuthoring.ReadInteraction(packages, interactionTag, config);
        byte[] retarget = InvestmentVendorAuthoring.RetargetInteraction(packages, config, site, 0x12345678);
        Check(site.HashOffset == 944 && U32(retarget, 944) == 0x12345678 && Outside(config, retarget, 944, 4),
            "Interaction retarget changed another field.");
        byte[] invalid = config.ToArray(); Q(invalid, 200, 129);
        Reject(() => InvestmentVendorAuthoring.ReadInteraction(packages, interactionTag, invalid));
        Reject(() => InvestmentVendorAuthoring.RetargetInteraction(packages, retarget, site, summary.Hash));
        var sale = catalog.ReadVendor(tag, game).Sales.Single();
        var pair = catalog.DuplicateCostPair(tag, game, displayTag, display, sale, sale.Costs.Single());
        Check(Selectors(pair[displayTag]).SequenceEqual(new ushort[] { 17, 17 })
            && catalog.ReadVendor(tag, pair[tag]).Sales.Single().Costs.Count == 2, "Cost duplicate lost paired selectors.");
        var edited = catalog.ReadVendor(tag, pair[tag]).Sales.Single();
        var removed = catalog.RemoveCostPair(tag, pair[tag], displayTag, pair[displayTag], edited, edited.Costs[0]);
        Check(Selectors(removed[displayTag]).SequenceEqual(new ushort[] { 17 }), "Cost removal changed selector order.");
        var drafts = new[] { new InvestmentVendorSaleDraft(0, 1, -1,
            [new(0, 0, 123), new(-1, 1, 7)]), new InvestmentVendorSaleDraft(0, 0, -1, []) };
        var stock = catalog.RewriteVendorStock(tag, game, displayTag, display, drafts);
        var result = catalog.ReadVendor(tag, stock[tag]);
        Check(result.Sales.Count == 2 && result.Sales[0].ItemIndex == 1 && result.Sales[0].Costs[0].Quantity == 123
            && result.Sales[0].Costs[1] is { Quantity: 7, Conditional: false } && result.Sales[1].Costs.Count == 0
            && Selectors(stock[displayTag]).SequenceEqual(new ushort[] { 17, ushort.MaxValue })
            && Selectors(stock[displayTag], 1).Length == 0, "Stock rewrite lost fields or cost display pairing.");
        byte[] extraPolicy = game.ToArray(); Q(extraPolicy, 80, 1);
        Reject(() => InvestmentVendorAuthoring.ValidateSaleOrder(extraPolicy, 1, []));
        InvestmentVendorAuthoring.ValidateSaleOrder(extraPolicy, 1, [0, 0]);
        byte[] extraSale = game.ToArray(); Q(extraSale, 144 + 48, 1);
        Reject(() => catalog.RewriteVendorStock(tag, extraSale, displayTag, display, drafts));
        extraSale = game.ToArray(); Q(extraSale, 144 + 56, 16);
        Reject(() => catalog.RewriteVendorStock(tag, extraSale, displayTag, display, drafts));
        Check(packages.Read(tag).SequenceEqual(game) && packages.Read(displayTag).SequenceEqual(display)
            && packages.Read(interactionTag).SequenceEqual(config), "Authoring changed source assets.");
        Console.WriteLine("vendor_authoring identity=own policy=retained text=u16 interaction=typed stock=paired selectors=paired refusals=pass");
    }

    private static ushort[] Selectors(byte[] bytes, int sale = 0)
    {
        int rows = checked(56 + (int)I64(bytes, 56) + 16), at = rows + sale * 128 + 32;
        int count = checked((int)I64(bytes, at)); if (count == 0) return [];
        int header = checked(at + 8 + (int)I64(bytes, at + 8));
        Check(U32(bytes, header - 4) == 0x80809FBD && U32(bytes, header + 8) == 0x808028B2
            && I64(bytes, header) == count, "Cost display selector header differs.");
        return Enumerable.Range(0, count).Select(index => U16(bytes, header + 16 + index * 2)).ToArray();
    }
    private static bool Outside(byte[] before, byte[] after, int offset, int count) => before.Length == after.Length
        && before.Take(offset).SequenceEqual(after.Take(offset)) && before.Skip(offset + count).SequenceEqual(after.Skip(offset + count));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    private static void Q(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void S(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new InvalidOperationException("Unsupported vendor edit accepted."); }
}
