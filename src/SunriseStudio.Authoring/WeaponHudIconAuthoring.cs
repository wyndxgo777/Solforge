using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Compiled weapon HUD icon: glyph hash and its texture, bitmap and config assets. */
public sealed record PreparedWeaponHudIcon(uint GlyphHash, uint TextureTag, uint BitmapTag, uint ConfigTag,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes)
{
    /** Project assets rewritten in place, by asset id. */
    public IReadOnlyDictionary<string, byte[]> AddedChanges { get; init; } = new Dictionary<string, byte[]>();
}

/** A custom HUD silhouette owns its bitmap and weapon config while retaining the shared library. */
public static class WeaponHudIconAuthoring
{
    private const string ConfigHash = "FBAD5533519458B46788CA953D0DF4415523579CFCD2D3CDF20893DD5CF82C4C";

    /** The gameplay root must belong to this project or the same pending transaction. */
    public static PreparedWeaponHudIcon Prepare(StudioProject project, PackageCatalog catalog, uint gameplayRootTag,
        RgbaImage image, string name, ushort packageId, int firstEntry,
        IReadOnlyList<ProjectAssetAddition>? plannedAssets = null)
    {
        image.Validate();
        if (image.Width != WeaponHudIconEdits.Width || image.Height != WeaponHudIconEdits.Height)
            throw new InvalidDataException("The HUD silhouette must be 137 x 76 pixels.");
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId)
            || firstEntry < 0 || firstEntry > 8188)
            throw new InvalidDataException("The HUD silhouette needs four free native entries.");
        uint Tag(int index) => checked(0x80800000u + ((uint)packageId << 13) + (uint)(firstEntry + index));
        if (Enumerable.Range(0, 4).Any(index => catalog.ContainsTag(Tag(index))))
            throw new InvalidDataException("A HUD silhouette destination is already used.");
        if (!Owned(gameplayRootTag) || Require(gameplayRootTag, 0x80809C0F).PackageId != packageId)
            throw new InvalidDataException("Create a project-owned gameplay root before assigning its HUD silhouette.");
        byte[] root = catalog.Read(gameplayRootTag);
        uint[] configs = WeaponConfigEdits.ConstructionConfigs(root)
            .Where(tag => tag == WeaponHudIconEdits.ConfigTemplate || Origin(tag) == WeaponHudIconEdits.ConfigTemplate).ToArray();
        if (configs.Length != 1) throw new InvalidDataException("The gameplay root has no unique checked HUD config.");
        uint sourceConfig = configs[0];
        var configEntry = Require(sourceConfig, 0x80809C36);
        byte[] config = catalog.Read(sourceConfig);
        byte[] normalized = WeaponHudIconEdits.RetagConfig(config, sourceConfig, WeaponHudIconEdits.ConfigTemplate);
        normalized = WeaponHudIconEdits.SetGlyph(normalized, WeaponHudIconEdits.TemplateGlyph);
        if (Convert.ToHexString(SHA256.HashData(normalized)) != ConfigHash)
            throw new NotSupportedException("The HUD config differs from its checked source profile.");
        Require(WeaponHudIconEdits.LibraryTag, WeaponHudIconEdits.LibraryClass);
        byte[] library = catalog.Read(WeaponHudIconEdits.LibraryTag);
        // A weapon that already owns its HUD set gets new pixels in that set.
        if (Owned(sourceConfig) && WeaponHudIconEdits.GlyphBitmap(library, WeaponHudIconEdits.ReadGlyph(config)) is { } ownBitmap
            && Owned(ownBitmap))
        {
            uint header = CuiBitmap.ReadGroups(catalog.Read(ownBitmap))[0][0];
            if (!Owned(header) || !catalog.TryGetEntry(header, out var headerEntry) || !Owned(headerEntry.Reference))
                throw new InvalidDataException("The weapon's HUD bitmap does not name its own texture.");
            var (headerBytes, payload) = TextureEdits.CreateRgbaTexture(image, catalog.Read(WeaponHudIconEdits.TextureTemplate));
            return new(WeaponHudIconEdits.ReadGlyph(config), header, ownBitmap, sourceConfig, [], new Dictionary<uint, byte[]>())
            {
                AddedChanges = new Dictionary<string, byte[]> { [Id(header)] = headerBytes, [Id(headerEntry.Reference)] = payload }
            };
        }
        uint hash = InvestmentItemClone.NewDefinitionHash($"sunrise:hud:{project.Name}:{name}:{gameplayRootTag:X8}",
            WeaponHudIconEdits.GlyphHashes(library).ToHashSet());
        var texture = TextureAssetImporter.PrepareRgba(catalog, image, name + ".hud", packageId,
            firstEntry, WeaponHudIconEdits.TextureTemplate, false);
        var bitmapEntry = Require(WeaponHudIconEdits.BitmapTemplate, 0x80804A69);
        var assets = texture.Assets.ToList();
        assets.Add(new(name + ".hud.bitmap", InvestmentIconEdits.SetStaticTexture(
            catalog.Read(WeaponHudIconEdits.BitmapTemplate), texture.HeaderTag),
            new(packageId, bitmapEntry.Reference, bitmapEntry.TypeInfo, null, firstEntry + 2), WeaponHudIconEdits.BitmapTemplate));
        config = WeaponHudIconEdits.SetGlyph(WeaponHudIconEdits.RetagConfig(config, sourceConfig, Tag(3)), hash);
        assets.Add(new(name + ".hud.config", config,
            new(packageId, configEntry.Reference, configEntry.TypeInfo, null, firstEntry + 3), WeaponHudIconEdits.ConfigTemplate));
        return new(hash, texture.HeaderTag, Tag(2), Tag(3), assets, new Dictionary<uint, byte[]>
        {
            [WeaponHudIconEdits.LibraryTag] = WeaponHudIconEdits.AddGlyph(library, hash, Tag(2)),
            [gameplayRootTag] = WeaponConfigEdits.RetagRoot(root, sourceConfig, Tag(3))
        });

        string Id(uint tag) => project.AddedAssets.Single(asset => asset.Native is { } native && NativeTag(native) == tag).Id;
        bool Owned(uint tag) => project.AddedAssets.Any(asset => asset.Native is { } native && NativeTag(native) == tag)
            || plannedAssets?.Any(asset => asset.Native is { } native && NativeTag(native) == tag) == true;
        uint? Origin(uint tag) => plannedAssets?.SingleOrDefault(asset => asset.Native is { } native && NativeTag(native) == tag)?.BaseTagHash
            ?? project.AddedAssets.SingleOrDefault(asset => asset.Native is { } native && NativeTag(native) == tag)?.BaseTagHash;
        PackageEntry Require(uint tag, uint type) => catalog.TryGetEntry(tag, out var entry) && entry.Reference == type
            ? entry : throw new InvalidDataException("A HUD silhouette source is missing or has the wrong class.");
    }

    private static uint NativeTag(NativeAssetMetadata native) =>
        checked(0x80800000u + ((uint)native.PackageId << 13) + (uint)native.EntryIndex!.Value);
}
