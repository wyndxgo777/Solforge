using System.Globalization;
using System.Security.Cryptography;

namespace SunriseStudio.Formats;

/** Item and vendor detail reading. */
public sealed partial class InvestmentCatalog
{
    /** Detail rows keep exact byte offsets so edits never resize native arrays. */
    public InvestmentItemDetail ReadItem(uint tag, ReadOnlyMemory<byte> bytes)
    {
        var summary = FindItem(tag) ?? throw new InvalidDataException("Item is absent from the investment index.");
        if (tag != summary.Tag) throw new InvalidDataException("Open the gameplay definition to edit this item.");
        RequireClass(catalog, tag, ItemClass);
        var data = new TagReader(bytes);
        if (!data.Has(180, 8)) throw new InvalidDataException("Item definition is truncated.");
        var item = summary with { MaximumStack = data.I32(180), Bucket = data.U8(184), Tier = data.U8(186), Instanced = data.U8(187) != 0 };
        List<AssetProperty> fields = [data.Int("Maximum stack", 180, true), data.Byte("Inventory bucket", 184, true),
            data.Byte("Rarity", 186, true), data.Byte("Instanced", 187)];
        var stats = new List<InvestmentStat>(); var sockets = new List<InvestmentSocket>();
        var perks = new List<InvestmentPerk>(); var errors = new List<string>();
        void Read(Action action, string section)
        { try { action(); } catch (Exception error) when (Recoverable(error)) { errors.Add(section + ": " + error.Message); } }
        Read(() =>
        {
            int block = data.Relative(112);
            if (block < 0) return;
            var rows = data.Array(block, 0x80803033, 40);
            if (rows.Count > MaximumAuthoredRows) throw new InvalidDataException("Item declares too many stats for this editor.");
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * 40; byte index = data.U8(p);
                stats.Add(new(index, StatName(index), data.I32(p + 4), data.Int(StatName(index), p + 4, true),
                    data.U64(p + 24) != 0, data.U64(p + 8) != 0));
            }
            var list = data.Array(block + 16, 0x808077BC, 24);
            if (list.Count > MaximumAuthoredRows) throw new InvalidDataException("Item declares too many perks for this editor.");
            for (int i = 0; i < list.Count; i++)
            {
                int p = list.Data + i * 24; ushort index = data.U16(p);
                perks.Add(new(index, PerkName(index), data.UShort("Sandbox perk", p, true), data.U64(p + 8) != 0));
            }
        }, "Stats");
        Read(() =>
        {
            int block = data.Relative(104);
            if (block < 0) return;
            var rows = data.Array(block, 0x808077C4, 80);
            if (rows.Count > 64) throw new InvalidDataException("Item declares too many socket lanes.");
            TagReader? sets = catalog.ContainsTag(sources.PlugSets) ? new(catalog.Read(sources.PlugSets)) : null;
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * 80; ushort plug = data.U16(p + 2);
                var allowed = new HashSet<ushort>();
                if (plug < Items.Count) allowed.Add(plug);
                void Members(TagReader source, int descriptor)
                {
                    var members = source.Array(descriptor, stride: 32);
                    for (int row = 0; row < members.Count; row++)
                    {
                        uint index = source.U32(members.Data + row * 32);
                        if (index < Items.Count) allowed.Add((ushort)index);
                    }
                }
                Read(() => Members(data, p + 64), $"Socket {i} plug list");
                if (sets is not null)
                    Read(() =>
                    {
                        var shared = sets.Array(8, stride: 24);
                        foreach (int field in new[] { 12, 32 })
                        {
                            ushort set = data.U16(p + field);
                            if (set == ushort.MaxValue) continue;
                            if (set >= shared.Count) throw new InvalidDataException("Socket plug set is outside its index.");
                            Members(sets, shared.Data + set * 24 + 8);
                        }
                    }, $"Socket {i} shared plugs");
                sockets.Add(new(i, data.U16(p), plug, ItemName(plug), data.UShort("Socket type", p, true),
                    data.UShort("Initial plug", p + 2, true), allowed.OrderBy(index => ItemName(index), StringComparer.OrdinalIgnoreCase).ToArray()));
            }
        }, "Sockets");
        var curves = ReadDisplayCurves(item, errors);
        return new(item, stats, sockets, perks, fields, errors) { DisplayCurves = curves };
    }

    /** Conditional cost expressions remain visible and are never labelled a final price. */
    public InvestmentVendorDetail ReadVendor(uint tag, ReadOnlyMemory<byte> bytes)
    {
        var vendor = FindVendor(tag) ?? throw new InvalidDataException("Vendor is absent from its index.");
        if (vendor.Tag != tag) throw new InvalidDataException("Open the vendor gameplay definition.");
        RequireClass(catalog, tag, VendorClass);
        var data = new TagReader(bytes);
        var rows = data.Array(48, 0x80807861, 184);
        if (rows.Count > MaximumAuthoredRows) throw new InvalidDataException("Vendor declares too many sale rows.");
        string sourceHash = Convert.ToHexString(SHA256.HashData(bytes.Span));
        var sales = new List<InvestmentSale>(); var errors = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            int p = rows.Data + i * 184;
            ushort item = data.U16(p + 70); int category = data.I32(p + 100);
            var costs = new List<InvestmentCost>();
            try
            {
                var list = data.Array(p + 32, 0x80807865, 48);
                if (list.Count > MaximumAuthoredRows) throw new InvalidDataException("Sale declares too many cost rows.");
                for (int k = 0; k < list.Count; k++)
                {
                    int q = list.Data + k * 48; ushort costItem = data.U16(q);
                    bool conditional = data.U64(q + 8) != 0 || data.U64(q + 24) != 0 || data.U32(q + 40) != 100_000;
                    costs.Add(new(costItem, ItemName(costItem), data.U32(q + 4), data.UShort("Cost item", q, true),
                        data.UInt(conditional ? "Base quantity" : "Quantity", q + 4, true), conditional));
                }
            }
            catch (Exception error) when (Recoverable(error)) { errors.Add($"Sale {i}: {error.Message}"); }
            sales.Add(new(i, item, ItemName(item), category, data.UShort("Sale item", p + 70, true),
                data.Int("Category", p + 100, true), costs) { OwnerTag = tag, SourceSha256 = sourceHash });
        }
        return new(vendor, sales, data.U32(20), data.U32(24), errors);
    }

    /** Only fields re-derived from the current native layout can enter a project delta. */
    public byte[] Edit(uint tag, ReadOnlyMemory<byte> current, AssetProperty field, string value)
    {
        List<AssetProperty> allowed = [];
        if (FindItem(tag) is not null)
        {
            var detail = ReadItem(tag, current);
            allowed.AddRange(detail.Fields); allowed.AddRange(detail.Stats.Select(row => row.Field));
            allowed.AddRange(detail.Sockets.SelectMany(row => new[] { row.TypeField, row.PlugField }));
            allowed.AddRange(detail.Perks.Select(row => row.Field));
            if (detail.Perks.Any(row => row.Field.Offset == field.Offset)
                && (!ushort.TryParse(value, out ushort perk) || perk >= PerkCount))
                throw new InvalidDataException("Choose a sandbox perk from this installation.");
            if (field.Offset == 180 && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximum) || maximum < 0))
                throw new InvalidDataException("Maximum stack must be zero or greater.");
            if (field.Offset == 186 && (!byte.TryParse(value, out byte rarity) || rarity > 5 && rarity != byte.MaxValue))
                throw new InvalidDataException("Choose a rarity from None through Exotic, or Unset.");
            var socket = detail.Sockets.FirstOrDefault(row => row.PlugField.Offset == field.Offset);
            if (socket is not null)
            {
                ValidateItemIndex(value);
                ushort plug = ushort.Parse(value, CultureInfo.InvariantCulture);
                if (plug != ushort.MaxValue && socket.AllowedPlugs.Count != 0 && !socket.AllowedPlugs.Contains(plug))
                    throw new InvalidDataException("Choose a plug from this socket's declared pool.");
            }
        }
        else
        {
            var detail = ReadVendor(tag, current);
            if (detail.Sales.Sum(sale => sale.Costs.Count(cost => cost.ItemField.Offset == field.Offset
                || cost.QuantityField.Offset == field.Offset)) > 1)
                throw new InvalidDataException("Use the sale's cost editor to change shared payment rows.");
            foreach (var sale in detail.Sales)
            {
                allowed.Add(sale.ItemField); allowed.Add(sale.CategoryField);
                allowed.AddRange(sale.Costs.SelectMany(cost => new[] { cost.ItemField, cost.QuantityField }));
                if (sale.ItemField.Offset == field.Offset || sale.Costs.Any(cost => cost.ItemField.Offset == field.Offset)) ValidateItemIndex(value);
                if (sale.CategoryField.Offset == field.Offset)
                {
                    var data = new TagReader(current); int count = data.Array(32, 0x80807860, 24).Count;
                    if (!int.TryParse(value, out int category) || category < -1 || category >= count)
                        throw new InvalidDataException("Vendor category is outside the declared category rows.");
                }
            }
        }
        var verified = allowed.FirstOrDefault(candidate => candidate.Editable && candidate.Offset == field.Offset && candidate.Encoding == field.Encoding)
            ?? throw new InvalidDataException("Investment field is not editable in this layout.");
        if (verified.Value != field.Value) throw new InvalidDataException("This field changed. Reopen its editor.");
        return AssetEdit.Apply(current.Span, verified, value);
    }

    private void ValidateItemIndex(string value)
    {
        if (!ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort index)
            || index != ushort.MaxValue && index >= Items.Count)
            throw new InvalidDataException("Choose an item from this installation.");
    }
}
