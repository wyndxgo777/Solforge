using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** Entity-level result of a skinned replacement: the prepared skin plus the entity, model and config assets cloned for it. */
public sealed record PreparedCompatibleEntity(PreparedCompatibleSkin Skin, uint EntityTag, uint ModelTag,
    uint ConfigTag, string ProjectPath, IReadOnlyList<ProjectAssetAddition> Additions,
    IReadOnlyDictionary<uint, string> SourceHashes, IReadOnlyDictionary<uint, PackageEntry> SourceEntries);

/** Entity creation half of CompatibleSkinnedMesh: clones the checked entity, model and config template set around a prepared skin. */
public static partial class CompatibleSkinnedMesh
{
    private const uint EntityTemplate = 0x80B82FE3, EntityModel = 0x80C4424F, EntityConfig = 0x80C443F8;
    private const int EntityEntryCount = 9;
    private const string EntityTemplateHash = "137B1BAEC5BB147BC555E5CB579CD14DA5AFEC720BBF92DB72EBEF0B260F05EC";
    private const string EntityConfigHash = "B34BEAFF3DC82D5486BD9097B2C69F0546601A53A818C83EB00DF7A69A27E117";
    private const string EntityModelHash = "D89C5A5340854926FC86C50AFA1FB93C66DD5B045553BCE956B92A0FAF4A53C2";

    /** True only for the one entity and model template pair whose component layout is checked. */
    public static bool SupportsEntityCreation(uint entityTag, uint modelTag) =>
        entityTag == EntityTemplate && modelTag == EntityModel;

    /** Creation keeps the checked component layout and shares the installed rig and clips. */
    // TODO: no caller yet; for a skinned import that builds its input without a glTF file.
    public static PreparedCompatibleEntity PrepareEntity(StudioProject project, PackageCatalog catalog,
        CompatibleSkinnedMeshInput input, CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        long revision = catalog.OverlayRevision;
        ValidateEntityDestination(project, catalog, options);
        return CloneEntity(project, catalog, options, Prepare(catalog, input, options, token), revision, token);
    }

    /** File joints still have to match the installed template's package-order bind pose. */
    public static PreparedCompatibleEntity PrepareEntityGltf(StudioProject project, PackageCatalog catalog,
        string path, CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        long revision = catalog.OverlayRevision;
        ValidateEntityDestination(project, catalog, options);
        return CloneEntity(project, catalog, options, PrepareGltf(catalog, path, options, token), revision, token);
    }

    /** Only typed owner tags change; reciprocal offsets and root ordinals keep their original positions. */
    private static PreparedCompatibleEntity CloneEntity(StudioProject project, PackageCatalog catalog,
        CompatibleSkinReplacementOptions options, PreparedCompatibleSkin skin, long revision, CancellationToken token)
    {
        var hashes = skin.SourceHashes.ToDictionary();
        var originalEntries = catalog.Entries.Where(entry => entry.TagHash is EntityTemplate or EntityConfig or EntityModel)
            .ToDictionary(entry => entry.TagHash);
        foreach (var (tag, original) in originalEntries)
        {
            var current = Require(catalog, tag);
            if (current.Reference != original.Reference || current.TypeInfo != original.TypeInfo
                || current.Type != original.Type || current.SubType != original.SubType)
                throw new NotSupportedException("Create asset needs the installed root, config and model metadata.");
        }
        if (originalEntries.Count != 3) throw new InvalidDataException("The entity creation profile is not installed.");
        byte[] Original(uint tag, uint type, string? expected = null)
        {
            var entry = Require(catalog, tag);
            byte[] bytes = catalog.Read(tag), original = catalog.ReadOriginal(tag);
            if (entry.Reference != type || !bytes.AsSpan().SequenceEqual(original)
                || expected is not null && Hash(original) != expected)
                throw new NotSupportedException("Create asset needs the unchanged installed Hob component profile.");
            string hash = Hash(bytes);
            if (hashes.TryGetValue(tag, out string? earlier) && earlier != hash)
                throw new InvalidDataException("An entity source changed during preparation.");
            hashes[tag] = hash;
            return bytes.ToArray();
        }
        byte[] root = Original(EntityTemplate, 0x80809C0F, EntityTemplateHash);
        byte[] config = Original(EntityConfig, 0x80809C36, EntityConfigHash);
        if (Hash(catalog.ReadOriginal(EntityModel)) != EntityModelHash)
            throw new NotSupportedException("The installed model does not match the checked Hob creation profile.");
        int[] components = NativeRows.Rows(root, 0x10, 0x80809C04, 12);
        if (components.Length != 62) throw new InvalidDataException("The source entity component order changed.");
        foreach (int component in components)
        {
            token.ThrowIfCancellationRequested();
            Original(U32(root, component), 0x80809C36);
        }
        uint Tag(int index) => checked(0x80800000u + ((uint)options.PackageId << 13)
            + (uint)(options.FirstEntryIndex + index));
        uint modelTag = Tag(6), configTag = Tag(7), entityTag = Tag(8);
        int primary = checked(0x10 + (int)I64(config, 0x10));
        int resource = checked(0x18 + (int)I64(config, 0x18));
        if (U32(config, primary - 4) != 0x808072B8 || U32(config, resource - 4) != 0x808072BD)
            throw new InvalidDataException("The model component types changed.");
        Pair(primary, resource, 0x808072B8, 0x808072BD);
        Pair(primary + 0x30, resource + 0x48, 0x80809BD8, 0x80809BD9);
        Pair(primary + 0x100, resource + 0x260, 0x808072CB, 0x808072C7);
        Pair(primary + 0x140, resource + 0x2C0, 0x808072C0, 0x808072C1);
        Pair(primary + 0x1A0, resource + 0x1E8, 0x80807314, 0x80807315);
        int[] values = NativeRows.Rows(config, primary + 0x120, 0x80809788, 96);
        int[] valueResources = NativeRows.Rows(config, resource + 0x2A8, 0x80809789, 40);
        if (values.Length != 8 || values.Length != valueResources.Length)
            throw new InvalidDataException("The model value-bank owner pairs changed.");
        for (int index = 0; index < values.Length; index++)
            Pair(values[index], valueResources[index], 0x80809788, 0x80809789);
        if (U32(config, resource + 0x1DC) != EntityModel)
            throw new InvalidDataException("The model component lost its source model.");
        Put32(config, resource + 0x1DC, modelTag);
        int links = 0;
        RootLinks(0x10, 0x80809C04, 12, [0]);
        RootLinks(0x20, 0x80809BC9, 72, [8, 40]);
        RootLinks(0x30, 0x80809BC9, 72, [8, 40]);
        RootLinks(0x58, 0x80809C22, 40, [16]);
        RootLinks(0x68, 0x80809C20, 24, [0]);
        if (links != 44) throw new InvalidDataException("The source root's typed model references changed.");
        var additions = skin.Additions.ToList();
        Add(EntityModel, skin.Replacements[EntityModel], 6, "model");
        Add(EntityConfig, config, 7, "component");
        Add(EntityTemplate, root, 8, "entity");
        var metadata = hashes.Keys.ToDictionary(tag => tag, tag => Require(catalog, tag));
        var prepared = new PreparedCompatibleEntity(skin, entityTag, modelTag, configTag,
            Path.GetFullPath(project.ManifestPath), additions.AsReadOnly(), hashes, metadata);
        token.ThrowIfCancellationRequested();
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Project sources changed while the entity was being prepared.");
        EntityEdits(project, catalog, prepared);
        return prepared;

        void Pair(int p, int r, uint pType, uint rType)
        {
            if (U32(config, p) != EntityConfig || U32(config, p + 4) != rType || I64(config, p + 8) != r
                || U32(config, r) != EntityConfig || U32(config, r + 4) != pType || I64(config, r + 8) != p)
                throw new InvalidDataException("A component owner pair is not reciprocal.");
            Put32(config, p, configTag); Put32(config, r, configTag);
        }
        void RootLinks(int field, uint type, int stride, int[] offsets)
        {
            foreach (int row in NativeRows.Rows(root, field, type, stride))
                foreach (int offset in offsets)
                    if (U32(root, row + offset) == EntityConfig)
                    { Put32(root, row + offset, configTag); links++; }
        }
        void Add(uint source, byte[] bytes, int index, string suffix)
        {
            var entry = Require(catalog, source);
            additions.Add(new(options.Name + "." + suffix, bytes,
                new(options.PackageId, entry.Reference, entry.TypeInfo,
                    index == 8 ? options.Name : null, options.FirstEntryIndex + index), source));
        }
    }

    /** Destination tags are reserved as a complete nine-entry group before geometry preparation. */
    private static void ValidateEntityDestination(StudioProject project, PackageCatalog catalog,
        CompatibleSkinReplacementOptions options)
    {
        if (!SupportsEntityCreation(options.EntityTag, options.ModelTag))
            throw new NotSupportedException("Create asset supports only the checked installed Hob rig and model.");
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > 8192 - EntityEntryCount)
            throw new InvalidDataException("Entity creation needs nine unused entries in a new package.");
        for (int index = options.FirstEntryIndex; index < options.FirstEntryIndex + EntityEntryCount; index++)
        {
            uint tag = checked(0x80800000u + ((uint)options.PackageId << 13) + (uint)index);
            if (catalog.TryGetEntry(tag, out _) || project.AddedAssets.Any(asset =>
                asset.Native?.PackageId == options.PackageId && asset.Native.EntryIndex == index))
                throw new InvalidDataException("A destination entry is already used by this project.");
        }
    }

    /** Source bytes, metadata and destination identities must still match the review at publication. */
    public static ProjectAssetEdits EntityEdits(StudioProject project, PackageCatalog catalog,
        PreparedCompatibleEntity prepared)
    {
        long revision = catalog.OverlayRevision;
        if (!string.Equals(Path.GetFullPath(project.ManifestPath), prepared.ProjectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The entity was prepared for a different project.");
        var first = prepared.Additions[0].Native!;
        ValidateEntityDestination(project, catalog, new(EntityTemplate, EntityModel, 0,
            prepared.Additions[0].Name, first.PackageId, first.EntryIndex!.Value));
        foreach (var (tag, hash) in prepared.SourceHashes)
            if (Require(catalog, tag) != prepared.SourceEntries[tag] || Hash(catalog.Read(tag)) != hash)
                throw new InvalidDataException("An entity source changed after review.");
        if (catalog.OverlayRevision != revision)
            throw new InvalidDataException("Project sources changed during entity validation.");
        return new(new Dictionary<uint, byte[]>());
    }

    /** Adds the prepared assets and entity edits to the project in one transaction. */
    public static IReadOnlyList<AddedProjectAsset> ApplyEntity(StudioProject project, PackageCatalog catalog,
        PreparedCompatibleEntity prepared) => project.ApplyTransaction(prepared.Additions,
            _ => EntityEdits(project, catalog, prepared), catalog);
}
