namespace SunriseStudio.Formats;

/** Views investment items, displays and tables; editable fields keep their offsets. */
public sealed class InvestmentInspector : IAssetInspector
{
    public string Id => "sunrise.investment";
    public string DisplayName => "Investment";
    /** True for the item, display, vendor and related investment classes (0x80807BEA, 0x80805CE1, 0x8080306D, 0x80807D84). */
    public bool CanInspect(AssetInspectionContext context) =>
        context.ClassHash is 0x80807BEA or 0x80805CE1 or 0x8080306D or 0x80807D84;

    /** Only fields already decoded for the installed build receive semantic labels. */
    public AssetInspection Inspect(AssetInspectionContext context)
    {
        if (context.ClassHash == 0x80807BEA && context.TagHash != 0x81327CCB)
            return Item(context);
        var r = new TagReader(context.Data);
        List<AssetNode> sections = [];
        if (context.ClassHash == 0x80805CE1 && r.Has(132, 28))
            sections.Add(new("Display strings", [r.UInt("Name bank", 132),
                r.UInt("Name hash", 136, true, true), r.UInt("Type bank", 144),
                r.UInt("Type hash", 148, true, true), r.UInt("Flavor bank", 152),
                r.UInt("Flavor hash", 156, true, true)]));
        if (context.TagHash == 0x81327CCB)
        {
            var array = r.Array(8, stride: 24);
            List<AssetNode> rows = [];
            for (int i = 0; i < array.Count; i++)
            {
                int p = array.Data + i * 24;
                rows.Add(new($"Item {i}", [r.UInt("Definition hash", p, hex: true),
                    r.UInt("Definition tag", p + 16, hex: true)]));
            }
            sections.Add(new("Items", [], rows));
        }
        if (context.ClassHash == 0x8080306D)
        {
            var array = r.Array(8, 0x80803475, 184);
            List<AssetNode> rows = [];
            for (int i = 0; i < array.Count; i++)
            {
                int p = array.Data + i * 184;
                rows.Add(new($"Collectible {i}", [r.UInt("Raw identifier", p, hex: true),
                    new("Expression offsets", "+48, +64, +80, +96, +112", p)]));
            }
            sections.Add(new("Collections", [], rows));
        }
        return new("Investment", context.Name, sections, [], []);
    }

    /** Sockets, perks and stats keep their authored index spaces. */
    private static AssetInspection Item(AssetInspectionContext context)
    {
        var r = new TagReader(context.Data);
        if (!r.Has(0, 185)) throw new InvalidDataException("Truncated item definition.");
        List<AssetNode> sections = [new("Item", [r.Int("Maximum stack", 180, true),
            r.Byte("Inventory bucket", 184, true)])];
        int sockets = r.Relative(104);
        if (sockets >= 0)
        {
            var array = r.Array(sockets, 0x808077C4, 80);
            List<AssetNode> rows = [];
            for (int i = 0; i < array.Count; i++)
            {
                int p = array.Data + i * 80;
                rows.Add(new($"Socket {i}", [r.UShort("Socket type", p, true),
                    r.UShort("Initial plug item", p + 2, true)]));
            }
            sections.Add(new("Sockets", [], rows));
        }
        int stats = r.Relative(112);
        if (stats >= 0)
        {
            var array = r.Array(stats, stride: 40);
            List<AssetNode> rows = [];
            for (int i = 0; i < array.Count; i++)
            {
                int p = array.Data + i * 40;
                rows.Add(new($"Stat {r.U8(p)}", [r.Byte("Stat index", p),
                    r.Int("Value", p + 4, true)]));
            }
            sections.Add(new("Stats", [], rows));
            var perks = r.Array(stats + 16, 0x808077BC, 24);
            List<AssetNode> perkRows = [];
            for (int i = 0; i < perks.Count; i++)
                perkRows.Add(new($"Perk {i}", [r.UShort("Sandbox perk index", perks.Data + i * 24, true)]));
            sections.Add(new("Perks", [], perkRows));
        }
        return new("Investment item", context.Name, sections, [], []);
    }
}
