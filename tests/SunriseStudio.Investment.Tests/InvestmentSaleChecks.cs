using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks vendor sale rows: duplication, removal shifting, and delta reopen. */
internal static class InvestmentSaleChecks
{
    private static readonly int[] GameplayArrays = [8, 32, 80, 120, 136, 160], DisplayArrays = [0, 16, 32, 48, 64];

    /** Installed sale rows keep every nested array target in both halves after a copy or removal. */
    public static void Owned(InvestmentCatalog catalog, PackageCatalog packages, string root)
    {
        var vendor = catalog.Vendors.First(row => row.DisplayTag != 0
            && new[] { 64, 80, 96 }.All(field => BinaryPrimitives.ReadInt64LittleEndian(packages.Read(row.Tag).AsSpan(field)) == 0)
            && catalog.ReadVendor(row.Tag, packages.Read(row.Tag)) is { Sales.Count: >= 3 } detail
            && detail.Sales.Any(sale => sale.Index > 0 && sale.Index < detail.Sales.Count - 1 && sale.Costs.Count > 0));
        byte[] gameplay = packages.Read(vendor.Tag), display = packages.Read(vendor.DisplayTag);
        var before = catalog.ReadVendor(vendor.Tag, gameplay);
        var sale = before.Sales.First(row => row.Index > 0 && row.Index < before.Sales.Count - 1 && row.Costs.Count > 0);
        int count = before.Sales.Count;

        var copied = catalog.DuplicateSale(vendor.Tag, gameplay, vendor.DisplayTag, display, sale);
        var grown = catalog.ReadVendor(vendor.Tag, copied[vendor.Tag]);
        Check(grown.Sales.Count == count + 1 && grown.Diagnostics.Count == 0, "Sale duplication changed the row count wrongly.");
        var copy = grown.Sales[^1];
        Check(copy.ItemIndex == sale.ItemIndex && copy.Category == sale.Category
            && copy.Costs.Select(row => (row.ItemIndex, row.Quantity)).SequenceEqual(sale.Costs.Select(row => (row.ItemIndex, row.Quantity))),
            "Duplicated sale lost its item, category or costs.");
        var rows = Enumerable.Range(0, count).Append(sale.Index).ToArray();
        SameTargets(gameplay, copied[vendor.Tag], 184, GameplayArrays, rows, "gameplay");
        SameTargets(display, copied[vendor.DisplayTag], 128, DisplayArrays, rows, "display");
        Preserved(gameplay, copied[vendor.Tag]); Preserved(display, copied[vendor.DisplayTag]);

        var removed = catalog.RemoveSale(vendor.Tag, gameplay, vendor.DisplayTag, display, sale);
        var shrunk = catalog.ReadVendor(vendor.Tag, removed[vendor.Tag]);
        Check(shrunk.Sales.Count == count - 1 && shrunk.Diagnostics.Count == 0
            && shrunk.Sales[sale.Index].ItemIndex == before.Sales[sale.Index + 1].ItemIndex, "Sale removal did not shift later rows.");
        rows = Enumerable.Range(0, count).Where(row => row != sale.Index).ToArray();
        SameTargets(gameplay, removed[vendor.Tag], 184, GameplayArrays, rows, "gameplay");
        SameTargets(display, removed[vendor.DisplayTag], 128, DisplayArrays, rows, "display");

        Reject(() => catalog.DuplicateSale(vendor.Tag, copied[vendor.Tag], vendor.DisplayTag, display, sale));
        Reject(() => catalog.DuplicateSale(vendor.Tag, gameplay, vendor.DisplayTag, removed[vendor.DisplayTag], sale));
        Reject(() => catalog.RemoveSale(vendor.Tag, gameplay, vendor.DisplayTag + 1, display, sale));
        var complex = catalog.FindVendor(0x81319094)!;
        byte[] complexBytes = packages.Read(complex.Tag), complexDisplay = packages.Read(complex.DisplayTag);
        var complexSale = catalog.ReadVendor(complex.Tag, complexBytes).Sales[0];
        Reject(() => catalog.RemoveSale(complex.Tag, complexBytes, complex.DisplayTag, complexDisplay, complexSale));

        var project = StudioProject.Create(Path.Combine(root, "vendor-sale-project"), "Vendor sales");
        project.ApplyEdits(copied, packages);
        Check(StudioProject.Open(project.ManifestPath) is var reopened
            && reopened.ReadAsset(vendor.Tag, packages).SequenceEqual(copied[vendor.Tag])
            && reopened.ReadAsset(vendor.DisplayTag, packages).SequenceEqual(copied[vendor.DisplayTag]),
            "Sale deltas did not reopen.");
        Check(packages.ReadOriginal(vendor.Tag).SequenceEqual(gameplay)
            && packages.ReadOriginal(vendor.DisplayTag).SequenceEqual(display), "Sale edit changed installed packages.");
        Console.WriteLine($"investment_sales vendor=0x{vendor.Tag:X8} display=0x{vendor.DisplayTag:X8} sales={count} "
            + "duplicate=pass remove=shifted targets=preserved stale=rejected project=delta");
    }

    /** Every surviving row resolves each nested array to the same address and count as its source row. */
    private static void SameTargets(byte[] before, byte[] after, int stride, int[] arrays, int[] sources, string half)
    {
        int oldRows = Rows(before), newRows = Rows(after);
        Check(Count(after) == sources.Length, $"The {half} row count is wrong.");
        for (int row = 0; row < sources.Length; row++)
            foreach (int descriptor in arrays)
                Check(Target(before, oldRows + sources[row] * stride + descriptor) == Target(after, newRows + row * stride + descriptor),
                    $"The {half} row {row} lost its array at +{descriptor}.");
    }

    /** Only the size header and the row array descriptor may change inside the original bytes. */
    private static void Preserved(byte[] before, byte[] after)
    {
        for (int at = 8; at < before.Length; at++)
            if (at is < 48 or >= 64) Check(before[at] == after[at], "Sale edit changed an unrelated source byte.");
    }

    private static int Count(byte[] bytes) => checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)));
    private static int Rows(byte[] bytes) => checked(56 + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56)) + 16);
    private static (long Count, long Address) Target(byte[] bytes, int descriptor)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(descriptor));
        long relative = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(descriptor + 8));
        return (count, relative == 0 ? -1 : descriptor + 8 + relative);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new InvalidOperationException("Invalid sale edit was accepted."); }
}
