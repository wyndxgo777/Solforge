using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Vendor sale and cost editing. */
public sealed partial class InvestmentCatalog
{
    private const uint SaleRowClass = 0x80807861, CostRowClass = 0x80807865;
    private const uint VendorDisplayClass = 0x808058EE, SaleDisplayRowClass = 0x808058FA;
    /** Native gameplay sale and cost rows have fixed strides. */
    private const int SaleStride = 184, CostStride = 48;
    /** Display sale rows pair with gameplay sale rows by index and are 128 bytes. */
    private const int SaleDisplayStride = 128;
    /** Array descriptors inside one row; every other slot is plain data. */
    private static readonly int[] SaleRowArrays = [8, 32, 80, 120, 136, 160], SaleDisplayArrays = [0, 16, 32, 48, 64];
    private enum CostOperation { Duplicate, Remove, Edit }

    /** The copy is appended to both halves, so no existing sale index moves. */
    public IReadOnlyDictionary<uint, byte[]> DuplicateSale(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        return new Dictionary<uint, byte[]>
        {
            [tag] = RewriteRows(current, 48, SaleRowClass, SaleStride, SaleRowArrays, sale.Index, false),
            [displayTag] = RewriteRows(display, 48, SaleDisplayRowClass, SaleDisplayStride, SaleDisplayArrays, sale.Index, false)
        };
    }

    /** Both halves drop the same index, so later sales shift together. */
    public IReadOnlyDictionary<uint, byte[]> RemoveSale(uint tag, ReadOnlyMemory<byte> current,
        uint displayTag, ReadOnlyMemory<byte> display, InvestmentSale sale)
    {
        VerifySaleDisplay(tag, current, displayTag, display, sale);
        int count = new TagReader(current).Array(48, SaleRowClass, SaleStride).Count;
        InvestmentVendorAuthoring.ValidateSaleOrder(current, count, Enumerable.Range(0, count).Where(index => index != sale.Index).ToArray());
        return new Dictionary<uint, byte[]>
        {
            [tag] = RewriteRows(current, 48, SaleRowClass, SaleStride, SaleRowArrays, sale.Index, true),
            [displayTag] = RewriteRows(display, 48, SaleDisplayRowClass, SaleDisplayStride, SaleDisplayArrays, sale.Index, true)
        };
    }

    private void VerifySaleDisplay(uint tag, ReadOnlyMemory<byte> current, uint displayTag, ReadOnlyMemory<byte> display,
        InvestmentSale sale)
    {
        VerifySale(tag, current, sale);
        if (displayTag == 0 || FindVendor(tag)?.DisplayTag != displayTag)
            throw new InvalidDataException("This vendor has no paired display definition.");
        RequireClass(catalog, displayTag, VendorDisplayClass);
        var reader = new TagReader(display);
        if (!reader.Has(0, 64) || reader.U64(0) != (ulong)display.Length)
            throw new InvalidDataException("Vendor display size header is invalid.");
        int gameplay = new TagReader(current).Array(48, SaleRowClass, SaleStride).Count;
        if (reader.Array(48, SaleDisplayRowClass, SaleDisplayStride).Count != gameplay)
            throw new InvalidDataException("Display sale rows do not match the gameplay sale rows.");
    }

    /**
     * Rewrites one top-level row array into an appended block with a row copied or removed.
     * @param current Blob whose size header and array field are rewritten.
     * @param field Offset of the row array descriptor.
     * @param rowClass Element class of the row array.
     * @param stride Row size in bytes.
     * @param arrays Offsets of the array descriptors inside one row; each is rebased on copy.
     * @param index Row to copy to the end, or to remove.
     * @param remove True removes the row; false appends a copy of it.
     * @return The rewritten blob; nested array data stays at its original address.
     */
    private static byte[] RewriteRows(ReadOnlyMemory<byte> current, int field, uint rowClass, int stride,
        int[] arrays, int index, bool remove)
    {
        var reader = new TagReader(current);
        var rows = reader.Array(field, rowClass, stride);
        if ((uint)index >= rows.Count) throw new InvalidDataException("The row is outside its array.");
        int count = checked(rows.Count + (remove ? -1 : 1));
        if (count > MaximumAuthoredRows) throw new InvalidDataException($"The editor supports up to {MaximumAuthoredRows} rows.");
        int header = checked((current.Length + 11) & ~7), start = checked(header + 16);
        byte[] output = new byte[checked(start + count * stride)]; current.Span.CopyTo(output);
        Q(0, output.Length); Q(field, count); Q(field + 8, count == 0 ? 0 : header - field - 8);
        if (count == 0) return output;
        U(header - 4, 0x80809FBD); Q(header, count); Q(header + 8, rowClass);
        int target = start;
        for (int row = 0; row < rows.Count; row++)
        {
            if (remove && row == index) continue;
            Copy(rows.Data + row * stride, target); target += stride;
        }
        if (!remove) Copy(rows.Data + index * stride, target);
        return output;

        void Copy(int from, int to)
        {
            if (rowClass == SaleRowClass && (reader.U64(from + 48) != 0 || reader.U64(from + 56) != 0))
                throw new NotSupportedException("The sale's additional typed field cannot yet be relocated.");
            current.Span.Slice(from, stride).CopyTo(output.AsSpan(to));
            foreach (int descriptor in arrays)
            {
                reader.Array(from + descriptor);
                int address = reader.Relative(from + descriptor + 8);
                Q(to + descriptor + 8, address < 0 ? 0 : address - to - descriptor - 8);
            }
        }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
    }

    /** Returns the vendor tag with a copy of the cost added to the sale. */
    public byte[] DuplicateCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost)
        => RewriteCosts(tag, current, sale, cost, CostOperation.Duplicate, null);

    /** Returns the vendor tag with the cost removed from the sale. */
    public byte[] RemoveCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost)
        => RewriteCosts(tag, current, sale, cost, CostOperation.Remove, null);

    /** Cost edits detach their owning array so shared payment rows cannot change another sale. */
    public byte[] EditCost(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost,
        ushort item, uint quantity)
    {
        if (item >= Items.Count) throw new InvalidDataException("Choose a cost item from this installation.");
        VerifySale(tag, current, sale);
        if (!sale.Costs.Contains(cost)) throw new InvalidDataException("The cost does not belong to this sale.");
        if (item == cost.ItemIndex && quantity == cost.Quantity) return current.ToArray();
        return RewriteCosts(tag, current, sale, cost, CostOperation.Edit, (item, quantity));
    }

    /** Appended cost rows keep every expression at its original absolute target. */
    private byte[] RewriteCosts(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale, InvestmentCost cost,
        CostOperation operation, (ushort Item, uint Quantity)? values)
    {
        int owner = VerifySale(tag, current, sale);
        var reader = new TagReader(current);
        var rows = reader.Array(owner + 32, CostRowClass, CostStride);
        int source = cost.ItemField.Offset;
        if (!sale.Costs.Contains(cost) || source < rows.Data || (source - rows.Data) % CostStride != 0
            || (source - rows.Data) / CostStride >= rows.Count || cost.QuantityField.Offset != source + 4)
            throw new InvalidDataException("The cost row changed. Reopen its editor.");
        int count = checked(rows.Count + (operation == CostOperation.Duplicate ? 1 : operation == CostOperation.Remove ? -1 : 0));
        if (count > MaximumAuthoredRows) throw new InvalidDataException("Sale has too many cost rows.");
        int header = checked((current.Length + 11) & ~7), start = checked(header + 16);
        byte[] output = new byte[checked(start + count * CostStride)]; current.Span.CopyTo(output);
        Q(0, output.Length); Q(owner + 32, count); Q(owner + 40, count == 0 ? 0 : header - owner - 40);
        if (count == 0) return output;
        U(header - 4, 0x80809FBD); Q(header, count); Q(header + 8, CostRowClass);
        int target = start;
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * CostStride;
            if (operation == CostOperation.Remove && at == source) continue;
            Copy(at, target);
            if (at == source && operation == CostOperation.Edit && values is { } value)
            { BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(target), value.Item); U(target + 4, value.Quantity); }
            target += CostStride;
        }
        if (operation == CostOperation.Duplicate) Copy(source, target);
        return output;

        void Copy(int from, int to)
        {
            current.Span.Slice(from, CostStride).CopyTo(output.AsSpan(to));
            foreach (int expression in new[] { 8, 24 })
            {
                reader.Array(from + expression, 0x80807D31, 8);
                int address = reader.Relative(from + expression + 8);
                Q(to + expression + 8, address < 0 ? 0 : address - to - expression - 8);
            }
        }
        void Q(int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(at), value);
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), value);
    }

    /** Sale identities expire when any part of their owning vendor payload changes. */
    private int VerifySale(uint tag, ReadOnlyMemory<byte> current, InvestmentSale sale)
    {
        RequireClass(catalog, tag, VendorClass);
        if (FindVendor(tag)?.Tag != tag || sale.OwnerTag != tag
            || sale.SourceSha256 != Convert.ToHexString(SHA256.HashData(current.Span)))
            throw new InvalidDataException("The vendor changed. Reopen its editor.");
        var reader = new TagReader(current);
        if (!reader.Has(0, 120) || reader.U64(0) != (ulong)current.Length)
            throw new InvalidDataException("Vendor size header is invalid.");
        var rows = reader.Array(48, SaleRowClass, SaleStride);
        if ((uint)sale.Index >= rows.Count || rows.Count > MaximumAuthoredRows)
            throw new InvalidDataException("The sale is outside its vendor.");
        int owner = rows.Data + sale.Index * SaleStride;
        if (sale.ItemField.Offset != owner + 70 || sale.CategoryField.Offset != owner + 100)
            throw new InvalidDataException("The sale does not match its native row.");
        return owner;
    }
}
