using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** A cost to write into a new sale: source cost index, item and quantity. */
public sealed record InvestmentVendorCostDraft(int SourceIndex, ushort Item, uint Quantity);
/** A sale to write into a vendor: source sale index, item, category and its costs. */
public sealed record InvestmentVendorSaleDraft(int SourceIndex, ushort Item, int Category,
    IReadOnlyList<InvestmentVendorCostDraft> Costs);

/** Vendor stock editing. */
public sealed partial class InvestmentCatalog
{
    private const uint CostSelectorClass = 0x808028B2;
    /** Selected sale policies retain their nested data; the paired rows and each payment array detach together. */
    public IReadOnlyDictionary<uint, byte[]> RewriteVendorStock(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, IReadOnlyList<InvestmentVendorSaleDraft> drafts)
    {
        RequireClass(catalog, tag, VendorClass);
        RequireClass(catalog, displayTag, VendorDisplayClass);
        if (FindVendor(tag)?.DisplayTag != displayTag || drafts.Count > MaximumAuthoredRows)
            throw new InvalidDataException("The vendor stock does not fit its paired definition.");
        var before = ReadVendor(tag, current);
        if (before.Diagnostics.Count != 0) throw new InvalidDataException(string.Join("\n", before.Diagnostics));
        var game = new TagReader(current); var text = new TagReader(display);
        if (game.U64(0) != (ulong)current.Length || text.U64(0) != (ulong)display.Length)
            throw new InvalidDataException("Vendor payload size header is invalid.");
        var rows = game.Array(48, SaleRowClass, SaleStride);
        var twins = text.Array(48, SaleDisplayRowClass, SaleDisplayStride);
        int categories = game.Array(32, 0x80807860, 24).Count;
        if (rows.Count != twins.Count) throw new InvalidDataException("Vendor display sales are unpaired.");
        InvestmentVendorAuthoring.ValidateSaleOrder(current, rows.Count, drafts.Select(draft => draft.SourceIndex).ToArray());
        bool relocate = drafts.Count != rows.Count || drafts.Where((draft, index) => draft.SourceIndex != index).Any();
        foreach (var draft in drafts)
        {
            if (draft is null || (uint)draft.SourceIndex >= rows.Count || draft.Item >= Items.Count
                || draft.Category < -1 || draft.Category >= categories || draft.Costs is null
                || draft.Costs.Count > InvestmentVendorAuthoring.MaximumCosts)
                throw new InvalidDataException("Choose valid sale, category and cost rows.");
            var costs = before.Sales[draft.SourceIndex].Costs;
            int sourceRow = rows.Data + draft.SourceIndex * SaleStride;
            if (relocate && (game.U64(sourceRow + 48) != 0 || game.U64(sourceRow + 56) != 0))
                throw new NotSupportedException("The selected sale has an additional typed field that cannot yet be relocated.");
            if (text.Array(twins.Data + draft.SourceIndex * SaleDisplayStride + 32, CostSelectorClass, 2).Count != costs.Count)
                throw new InvalidDataException("Cost display selectors are unpaired with the gameplay prices.");
            if (draft.Costs.Any(cost => cost is null || cost.Item >= Items.Count || cost.SourceIndex < -1
                || cost.SourceIndex >= costs.Count))
                throw new InvalidDataException("A payment does not belong to its selected sale policy.");
        }
        if (drafts.Count == before.Sales.Count && drafts.Select((draft, index) =>
            draft.SourceIndex == index && draft.Item == before.Sales[index].ItemIndex
            && draft.Category == before.Sales[index].Category
            && draft.Costs.Count == before.Sales[index].Costs.Count
            && draft.Costs.Select((cost, slot) => cost.SourceIndex == slot
                && cost.Item == before.Sales[index].Costs[slot].ItemIndex
                && cost.Quantity == before.Sales[index].Costs[slot].Quantity).All(value => value)).All(value => value))
            return new Dictionary<uint, byte[]>();

        byte[] gameplay = relocate ? SelectedRows(current, game, rows.Data, SaleStride, SaleRowClass, SaleRowArrays, drafts)
            : current.ToArray();
        byte[] presentation = relocate ? SelectedRows(display, text, twins.Data, SaleDisplayStride, SaleDisplayRowClass,
            SaleDisplayArrays, drafts) : display.ToArray();
        int first = new TagReader(gameplay).Array(48, SaleRowClass, SaleStride).Data;
        int cursor = gameplay.Length, length = cursor;
        foreach (var draft in drafts.Where(draft => draft.Costs.Count != 0))
            length = checked(((length + 11) & ~7) + 16 + draft.Costs.Count * CostStride);
        Array.Resize(ref gameplay, length); Q(gameplay, 0, length);
        int displayCursor = presentation.Length, displayLength = displayCursor;
        foreach (var draft in drafts.Where(draft => draft.Costs.Count != 0))
            displayLength = checked(((displayLength + 11) & ~7) + 16 + draft.Costs.Count * 2);
        Array.Resize(ref presentation, displayLength); Q(presentation, 0, displayLength);
        int displayFirst = new TagReader(presentation).Array(48, SaleDisplayRowClass, SaleDisplayStride).Data;
        for (int i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i]; int sale = first + i * SaleStride;
            var selectors = text.Array(twins.Data + draft.SourceIndex * SaleDisplayStride + 32, CostSelectorClass, 2);
            WriteCostSelectors(presentation, displayFirst + i * SaleDisplayStride, ref displayCursor,
                draft.Costs.Select(cost => cost.SourceIndex < 0 ? ushort.MaxValue : text.U16(selectors.Data + cost.SourceIndex * 2)).ToArray());
            BinaryPrimitives.WriteUInt16LittleEndian(gameplay.AsSpan(sale + 70), draft.Item);
            BinaryPrimitives.WriteInt32LittleEndian(gameplay.AsSpan(sale + 100), draft.Category);
            var costs = before.Sales[draft.SourceIndex].Costs;
            int header = checked((cursor + 11) & ~7), start = checked(header + 16);
            Q(gameplay, sale + 32, draft.Costs.Count);
            Q(gameplay, sale + 40, draft.Costs.Count == 0 ? 0 : header - sale - 40);
            if (draft.Costs.Count == 0) continue;
            cursor = checked(start + draft.Costs.Count * CostStride);
            U(gameplay, header - 4, 0x80809FBD); Q(gameplay, header, draft.Costs.Count);
            Q(gameplay, header + 8, CostRowClass);
            for (int k = 0; k < draft.Costs.Count; k++)
            {
                var cost = draft.Costs[k]; int target = start + k * CostStride;
                if (cost.SourceIndex >= 0)
                {
                    int source = costs[cost.SourceIndex].ItemField.Offset;
                    current.Span.Slice(source, CostStride).CopyTo(gameplay.AsSpan(target));
                    foreach (int field in new[] { 8, 24 })
                    {
                        game.Array(source + field, 0x80807D31, 8);
                        int address = game.Relative(source + field + 8);
                        Q(gameplay, target + field + 8, address < 0 ? 0 : address - target - field - 8);
                    }
                }
                else U(gameplay, target + 40, 100000);
                BinaryPrimitives.WriteUInt16LittleEndian(gameplay.AsSpan(target), cost.Item);
                U(gameplay, target + 4, cost.Quantity);
            }
        }
        return new Dictionary<uint, byte[]> { [tag] = gameplay, [displayTag] = presentation };
    }

    /** A failed-payment ordinal indexes the paired display selector without a native bounds check. */
    public IReadOnlyDictionary<uint, byte[]> DuplicateCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost) =>
        ChangeCostPair(tag, current, displayTag, display, sale, cost, false);

    /** Removes a cost together with its paired entry in the vendor display; returns the changed tags by tag hash. */
    public IReadOnlyDictionary<uint, byte[]> RemoveCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost) =>
        ChangeCostPair(tag, current, displayTag, display, sale, cost, true);

    private IReadOnlyDictionary<uint, byte[]> ChangeCostPair(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale, InvestmentCost cost, bool remove)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        int position = sale.Costs.ToList().IndexOf(cost);
        if (position < 0) throw new InvalidDataException("The payment does not belong to this sale.");
        var text = new TagReader(display);
        int row = text.Array(48, SaleDisplayRowClass, SaleDisplayStride).Data + sale.Index * SaleDisplayStride;
        var source = text.Array(row + 32, CostSelectorClass, 2);
        if (source.Count != sale.Costs.Count) throw new InvalidDataException("Cost display selectors are unpaired.");
        var selectors = Enumerable.Range(0, source.Count).Select(index => text.U16(source.Data + index * 2)).ToList();
        if (remove) selectors.RemoveAt(position);
        else selectors.Add(selectors[position]);
        if (selectors.Count > InvestmentVendorAuthoring.MaximumCosts)
            throw new InvalidDataException("The runtime supports at most four payment items per sale.");
        byte[] game = remove ? RemoveCost(tag, current, sale, cost) : DuplicateCost(tag, current, sale, cost);
        int cursor = display.Length;
        int length = selectors.Count == 0 ? cursor : checked(((cursor + 11) & ~7) + 16 + selectors.Count * 2);
        byte[] presentation = new byte[length]; display.Span.CopyTo(presentation);
        Q(presentation, 0, length);
        WriteCostSelectors(presentation, row, ref cursor, selectors);
        return new Dictionary<uint, byte[]> { [tag] = game, [displayTag] = presentation };
    }

    private static void WriteCostSelectors(byte[] bytes, int sale, ref int cursor, IReadOnlyList<ushort> values)
    {
        Q(bytes, sale + 32, values.Count);
        if (values.Count == 0) { Q(bytes, sale + 40, 0); return; }
        int header = checked((cursor + 11) & ~7), start = checked(header + 16);
        Q(bytes, sale + 40, header - sale - 40);
        U(bytes, header - 4, 0x80809FBD); Q(bytes, header, values.Count); Q(bytes, header + 8, CostSelectorClass);
        for (int index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(start + index * 2), values[index]);
        cursor = checked(start + values.Count * 2);
    }

    private static byte[] SelectedRows(ReadOnlyMemory<byte> current, TagReader reader, int sourceRows,
        int stride, uint rowClass, int[] arrays, IReadOnlyList<InvestmentVendorSaleDraft> drafts)
    {
        int header = checked((current.Length + 11) & ~7), start = checked(header + 16);
        byte[] result = new byte[checked(start + drafts.Count * stride)]; current.Span.CopyTo(result);
        Q(result, 0, result.Length); Q(result, 48, drafts.Count);
        Q(result, 56, drafts.Count == 0 ? 0 : header - 56);
        if (drafts.Count == 0) return result;
        U(result, header - 4, 0x80809FBD); Q(result, header, drafts.Count); Q(result, header + 8, rowClass);
        for (int index = 0; index < drafts.Count; index++)
        {
            int source = sourceRows + drafts[index].SourceIndex * stride, target = start + index * stride;
            current.Span.Slice(source, stride).CopyTo(result.AsSpan(target));
            foreach (int field in arrays)
            {
                reader.Array(source + field);
                int address = reader.Relative(source + field + 8);
                Q(result, target + field + 8, address < 0 ? 0 : address - target - field - 8);
            }
        }
        return result;
    }

    private static void Q(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void U(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
