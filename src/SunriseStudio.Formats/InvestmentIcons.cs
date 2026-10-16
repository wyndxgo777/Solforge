using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** A display icon: its container and bitmap tags and the decoded texture. */
public sealed record InvestmentIcon(uint ContainerTag, uint BitmapTag, TextureData Texture);

/** Icon reading. */
public sealed partial class InvestmentCatalog
{
    /** Display icon indices select shared rows; their hashes need not match the item hash. */
    public InvestmentIcon? ReadIcon(InvestmentItem item, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (item.DisplayTag == 0 || !catalog.ContainsTag(sources.Icons)) return null;
        RequireClass(catalog, item.DisplayTag, DisplayClass);
        var display = new TagReader(catalog.Read(item.DisplayTag));
        if (!display.Has(128, 2)) throw new InvalidDataException("Item icon index is truncated.");
        ushort index = display.U16(128);
        return index == ushort.MaxValue ? null : ReadIcon(index, token);
    }

    /** Reads a perk's icon; null when it has none or the icon table is missing. */
    public InvestmentIcon? ReadPerkIcon(int perk, CancellationToken token = default) =>
        perk >= 0 && perk < PerkDefinitions.Count && PerkDefinitions[perk].IconIndex != ushort.MaxValue
        && catalog.ContainsTag(sources.Icons) ? ReadIcon(PerkDefinitions[perk].IconIndex, token) : null;

    /** Icon rows are shared by items and perks; a row names a container, which names a bitmap. */
    private InvestmentIcon? ReadIcon(int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireClass(catalog, sources.Icons, 0x80802951);
        var icons = new TagReader(catalog.Read(sources.Icons));
        var rows = icons.Array(8, stride: 24);
        if (index >= rows.Count) throw new InvalidDataException("Icon index is outside its table.");
        uint containerTag = icons.U32(rows.Data + index * 24 + 16);
        RequireClass(catalog, containerTag, 0x80804A53);
        var container = new TagReader(catalog.Read(containerTag));
        if (!container.Has(0, 128)) throw new InvalidDataException("Icon container is truncated.");
        uint bitmapTag = container.U32(20);
        if (bitmapTag is 0 or uint.MaxValue) return null;
        RequireClass(catalog, bitmapTag, 0x80804A69);
        uint textureTag = CuiBitmap.ReadTexture(catalog.Read(bitmapTag));
        token.ThrowIfCancellationRequested();
        return new(containerTag, bitmapTag, new TextureDataReader(catalog).Read(textureTag));
    }
}
