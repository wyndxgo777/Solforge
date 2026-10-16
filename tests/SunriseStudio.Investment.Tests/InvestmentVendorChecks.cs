using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks vendor cost expressions: no-op edits keep bytes, scoped edits do not leak through aliased arrays, and relocation keeps shared targets. */
internal static class InvestmentVendorChecks
{
    /** Aliased costs and their expression streams stay unchanged outside the selected sale. */
    public static void Run(InvestmentCatalog catalog, PackageCatalog packages, uint tag, string root)
    {
        byte[] bytes = new byte[1536]; Q(bytes, 0, bytes.Length);
        Array(bytes, 48, 128, 2, 0x80807861);
        const int first = 144, second = 328, cost = 576;
        foreach (int sale in new[] { first, second })
        {
            S(bytes, sale + 70, 0); U(bytes, sale + 100, uint.MaxValue);
            Q(bytes, sale + 32, 1); Q(bytes, sale + 40, 560 - sale - 40);
        }
        U(bytes, 556, 0x80809FBD); Q(bytes, 560, 1); Q(bytes, 568, 0x80807865);
        S(bytes, cost, 1); U(bytes, cost + 4, 250); U(bytes, cost + 40, 100000);
        Array(bytes, cost + 8, 800, 1, 0x80807D31); U(bytes, 816, 77);
        Array(bytes, cost + 24, 848, 1, 0x80807D31); U(bytes, 864, 91);
        var before = catalog.ReadVendor(tag, bytes);
        var owner = before.Sales[0]; var payment = owner.Costs.Single();
        Check(payment.Conditional, "Expression fixture was not recognized.");
        Check(catalog.EditCost(tag, bytes, owner, payment, payment.ItemIndex, payment.Quantity).SequenceEqual(bytes),
            "Cost no-op changed bytes.");
        Reject(() => catalog.Edit(tag, bytes, payment.QuantityField, "300"));
        byte[] changed = catalog.EditCost(tag, bytes, owner, payment, 0, 325);
        var edited = catalog.ReadVendor(tag, changed);
        Check(edited.Sales[0].Costs.Single() is { ItemIndex: 0, Quantity: 325 }
            && edited.Sales[1].Costs.Single().Quantity == 250, "Scoped cost edit leaked through an aliased array.");
        Preserved(bytes, changed);
        var moved = edited.Sales[0].Costs.Single();
        Check(Target(changed, moved.ItemField.Offset + 16) == 800 && Target(changed, moved.ItemField.Offset + 32) == 848,
            "Cost relocation lost shared expression targets.");
        byte[] duplicated = catalog.DuplicateCost(tag, changed, edited.Sales[0], moved);
        var doubled = catalog.ReadVendor(tag, duplicated);
        Check(doubled.Sales[0].Costs.Count == 2 && doubled.Sales[0].Costs.All(row => row.Quantity == 325)
            && doubled.Sales[1].Costs.Count == 1, "Cost duplication changed another sale.");
        foreach (var row in doubled.Sales[0].Costs)
            Check(Target(duplicated, row.ItemField.Offset + 16) == 800 && Target(duplicated, row.ItemField.Offset + 32) == 848,
                "Duplicated cost lost a native expression.");
        byte[] removed = catalog.RemoveCost(tag, duplicated, doubled.Sales[0], doubled.Sales[0].Costs[0]);
        var once = catalog.ReadVendor(tag, removed);
        removed = catalog.RemoveCost(tag, removed, once.Sales[0], once.Sales[0].Costs[0]);
        var empty = catalog.ReadVendor(tag, removed);
        Check(empty.Sales[0].Costs.Count == 0 && empty.Sales[1].Costs.Single().Quantity == 250 && empty.Diagnostics.Count == 0,
            "Last cost removal changed another sale or broke the empty descriptor.");
        Reject(() => catalog.RemoveCost(tag, changed, owner, payment));
        Reject(() => catalog.EditCost(tag, bytes, owner, payment, ushort.MaxValue, 1));
        Reject(() => catalog.EditCost(tag, bytes, owner with { OwnerTag = tag + 1 }, payment, 0, 1));
        var delta = BinaryDelta.Create(bytes, removed);
        Check(delta.Apply(bytes).SequenceEqual(removed) && delta.Operations.All(row => row.Kind != DeltaKind.Add),
            "Vendor delta copied original-backed literal bytes or failed reconstruction.");
        var project = StudioProject.Create(Path.Combine(root, "vendor-cost-project"), "Vendor costs");
        project.ApplyEdit(tag, removed, packages);
        Check(StudioProject.Open(project.DirectoryPath).ReadAsset(tag, packages).SequenceEqual(removed),
            "Vendor cost delta did not reopen.");
        Console.WriteLine("investment_costs alias=isolated duplicate=pass remove=pass expressions=preserved stale=rejected project=delta");
    }

    private static void Preserved(byte[] before, byte[] after)
    {
        for (int at = 0; at < before.Length; at++)
            if (at is not (>= 0 and < 8 or >= 176 and < 192))
                Check(before[at] == after[at], "Cost edit changed an unrelated source byte.");
    }

    private static int Target(byte[] bytes, int pointer) => checked(pointer + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pointer)));
    private static void Array(byte[] bytes, int field, int header, int count, uint type)
    { Q(bytes, field, count); Q(bytes, field + 8, header - field - 8); U(bytes, header - 4, 0x80809FBD); Q(bytes, header, count); Q(bytes, header + 8, type); }
    private static void Q(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void S(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid vendor edit was accepted."); }
}
