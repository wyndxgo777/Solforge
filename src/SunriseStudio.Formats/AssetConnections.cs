using SunriseStudio.Animation;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** The role a related asset plays for the asset being viewed. */
public enum AssetRelationshipKind
{
    Component, Model, Material, Texture, Shader, Buffer, Data, Skeleton,
    AnimationDefinition, AnimationClip, Owner, Attachment, Source, Reference
}

/** A package asset linked from another, with where the link was found (source tag and offset); IsCandidate marks hash matches that may be coincidence. */
public sealed record RelatedAsset(uint TagHash, string Name, AssetRelationshipKind Kind, string Role,
    uint SourceTag, int SourceOffset, AssetOpenContext? Context = null, bool IsCandidate = false);

/** Resolved links of one asset: related assets, the preview and animation entity to use, and any diagnostics. */
public sealed record AssetConnections(uint AssetTag, uint PreviewTag, uint AnimationEntityTag,
    uint SelectedClipTag, IReadOnlyList<RelatedAsset> Related, IReadOnlyList<uint> EntityCandidates,
    IReadOnlyList<string> Diagnostics);

/** The package links of one asset, read once. Complete adds the preview scene and the inspection. */
public sealed class AssetConnectionDraft
{
    internal AssetConnectionDraft(uint tag, AssetOpenContext? context, Func<uint, string?>? resolveName, bool hasOwner,
        uint previewTag, uint animationEntityTag, uint selectedClipTag, List<RelatedAsset> links,
        HashSet<uint> entityCandidates, List<string> diagnostics)
    {
        Tag = tag; Context = context; ResolveName = resolveName; HasOwner = hasOwner;
        PreviewTag = previewTag; AnimationEntityTag = animationEntityTag; SelectedClipTag = selectedClipTag;
        Links = links; EntityCandidates = entityCandidates; Diagnostics = diagnostics;
    }

    public uint Tag { get; }
    public uint PreviewTag { get; }
    public uint AnimationEntityTag { get; }
    public uint SelectedClipTag { get; }
    internal AssetOpenContext? Context { get; }
    internal Func<uint, string?>? ResolveName { get; }
    internal bool HasOwner { get; }
    internal IReadOnlyList<RelatedAsset> Links { get; }
    internal IReadOnlySet<uint> EntityCandidates { get; }
    internal IReadOnlyList<string> Diagnostics { get; }
}

/** Typed package edges define navigation; shared owners never choose an animation rig implicitly. */
public sealed class AssetConnectionReader(PackageCatalog catalog)
{
    /** Physical reference rows preserve repeated targets at distinct native offsets. */
    public IReadOnlyList<RelatedAsset> ReadNativeReferences(uint tag, CancellationToken token = default) =>
        ReadDirect(tag, null, token);

    private const uint EntityClass = 0x80809C0F, ConfigClass = 0x80809C36, ModelClass = 0x808073A5;
    private sealed record ModelLink(uint Config, uint Model, int Offset);
    private sealed record EntityLinks(uint Tag, uint[] Configs, ModelLink[] Models,
        EntityAnimationReference[] Clips, uint[] Skeletons);

    /** Owner tags are hints from an existing index, not permission to choose the first matching entity. */
    public AssetConnections Read(uint tag, AssetOpenContext? context = null, SceneDocument? previewScene = null,
        Func<uint, string?>? resolveName = null, IEnumerable<uint>? ownerTags = null, CancellationToken token = default,
        AssetInspection? inspection = null) =>
        Complete(ReadDraft(tag, context, resolveName, ownerTags, token), previewScene, inspection, token);

    /** Reads every link that does not need the preview scene; PreviewTag names the scene to load. */
    public AssetConnectionDraft ReadDraft(uint tag, AssetOpenContext? context = null,
        Func<uint, string?>? resolveName = null, IEnumerable<uint>? ownerTags = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!catalog.TryGetEntry(tag, out var entry)) throw new KeyNotFoundException($"Asset {tag:X8} is not installed.");
        var links = new List<RelatedAsset>();
        var diagnostics = new List<string>();
        var entities = new Dictionary<uint, EntityLinks>();
        var entityCandidates = new HashSet<uint>();
        uint preview = tag, animationEntity = 0, selectedClip = 0;
        EntityLinks Entity(uint candidate)
        {
            token.ThrowIfCancellationRequested();
            if (!entities.TryGetValue(candidate, out var found)) entities[candidate] = found = ReadEntity(candidate, token);
            return found;
        }
        string Name(uint target) => NameOf(target, resolveName);
        void Add(uint target, AssetRelationshipKind kind, string role, uint sourceTag, int offset,
            AssetOpenContext? navigation = null, bool candidate = false) =>
            AddLink(links, tag, resolveName, target, kind, role, sourceTag, offset, navigation ?? context, candidate);
        void Safe(Action action)
        {
            try { action(); }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add(error.Message); }
        }

        EntityLinks? owner = null;
        if (Class(tag) == EntityClass) Safe(() => owner = Entity(tag));
        else if (Class(tag) is ModelClass or ConfigClass or 0x80808F49 or 0x808036F6
            && context?.EntityTag is > 0 and var contextEntity && Class(contextEntity) == EntityClass)
        {
            Safe(() =>
            {
                var found = Entity(contextEntity);
                bool linked = found.Models.Any(model => model.Model == tag && (context.ConfigTag == 0 || model.Config == context.ConfigTag))
                    || entry.Reference == ConfigClass && found.Configs.Contains(tag)
                    || entry.Reference == 0x80808F49 && found.Clips.Any(clip => clip.ClipTag == tag)
                    || entry.Reference == 0x808036F6 && found.Clips.Any(clip => clip.DefinitionTag == tag);
                if (linked) owner = found;
                else diagnostics.Add("Owner: no typed link to this asset");
            });
        }
        foreach (uint candidate in (ownerTags ?? []).Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (Class(candidate) == EntityClass)
                Safe(() =>
                {
                    var found = Entity(candidate);
                    if (found.Models.Any(model => model.Model == tag) || found.Configs.Contains(tag)
                        || found.Clips.Any(clip => clip.ClipTag == tag || clip.DefinitionTag == tag))
                    {
                        entityCandidates.Add(candidate);
                        Add(candidate, AssetRelationshipKind.Owner, "Entity owner", candidate, -1, new(candidate));
                    }
                });
            else Safe(() =>
            {
                var reverse = ReadDirect(candidate, null, token);
                if (reverse.Any(edge => edge.TagHash == tag))
                {
                    Add(candidate, AssetRelationshipKind.Owner, "Used by", candidate, -1);
                }
            });
        }
        if (owner is not null)
        {
            context = (context ?? new()) with { EntityTag = owner.Tag };
            entityCandidates.Add(owner.Tag);
            if (entry.Reference is EntityClass or ModelClass or ConfigClass or 0x80808F49 or 0x808036F6)
                preview = owner.Tag;
            if (owner.Skeletons.Length != 0) animationEntity = owner.Tag;
            if (entry.Reference == 0x80808F49 && owner.Clips.Any(clip => clip.ClipTag == tag)) selectedClip = tag;
            Add(owner.Tag, AssetRelationshipKind.Owner, "Entity owner", owner.Tag, -1, new(owner.Tag));
            foreach (var model in owner.Models)
                Add(model.Model, AssetRelationshipKind.Model, "Model", model.Config, model.Offset,
                    context with { ModelTag = model.Model, ConfigTag = model.Config });
            foreach (uint skeleton in owner.Skeletons)
                Add(skeleton, AssetRelationshipKind.Skeleton, "Skeleton", owner.Tag, -1, context);
            foreach (var clip in owner.Clips)
            {
                Add(clip.DefinitionTag, AssetRelationshipKind.AnimationDefinition, "Animation definition", clip.ConfigTag, -1, context);
                Add(clip.ClipTag, AssetRelationshipKind.AnimationClip, $"Clip {clip.Index}", clip.DefinitionTag, -1, context);
            }
            if (entry.Reference == EntityClass)
                Safe(() =>
                {
                    var attachments = new AssetSceneLoader(catalog).DescribeEntityAttachments(owner.Tag);
                    foreach (var weapon in attachments.Loadouts.SelectMany(loadout => loadout.Weapons))
                        Add(weapon.EntityTag, AssetRelationshipKind.Attachment, "Weapon option", weapon.ConfigTag,
                            weapon.SourceOffset + 8, new(weapon.EntityTag));
                });
        }
        Safe(() => links.AddRange(ReadDirect(tag, context, token).Select(edge => edge with { Name = Name(edge.TagHash) })));
        if (entry.Reference == ConfigClass && owner is null)
            Safe(() => { if (ReadModelConfig(tag) is { } model) preview = model.Model; });
        return new(tag, context, resolveName, owner is not null, preview, animationEntity, selectedClip,
            links, entityCandidates, diagnostics);
    }

    /** Adds the preview scene's links and the inspection's references. The draft is not changed. */
    public AssetConnections Complete(AssetConnectionDraft draft, SceneDocument? previewScene = null,
        AssetInspection? inspection = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        uint tag = draft.Tag;
        var links = draft.Links.ToList();
        var diagnostics = draft.Diagnostics.ToList();
        void Add(uint target, AssetRelationshipKind kind, string role, uint sourceTag, int offset, bool candidate = false) =>
            AddLink(links, tag, draft.ResolveName, target, kind, role, sourceTag, offset, draft.Context, candidate);
        if (previewScene is not null)
            foreach (var mesh in previewScene.Instances.Select(instance => instance.Mesh).Distinct())
            {
                token.ThrowIfCancellationRequested();
                Add(mesh.TagHash, AssetRelationshipKind.Model, "Preview model", tag, -1);
                Add(mesh.MaterialTag, AssetRelationshipKind.Material, "Material", mesh.TagHash, mesh.PartIndex);
                if (mesh.Native is not { } native) continue;
                foreach (var stream in native.VertexStreams)
                    Add(stream.HeaderTag, AssetRelationshipKind.Buffer, "Vertex buffer", mesh.TagHash, -1);
                Add(native.IndexHeaderTag, AssetRelationshipKind.Buffer, "Index buffer", mesh.TagHash, -1);
            }
        if (!draft.HasOwner && draft.EntityCandidates.Count != 0)
            diagnostics.Add("Animations: open an entity owner");
        if (inspection is not null)
            foreach (var reference in inspection.References)
                if (!links.Any(link => link.TagHash == reference.TagHash && !link.IsCandidate))
                    Add(reference.TagHash, AssetRelationshipKind.Reference,
                        reference.IsCandidate ? "Possible reference" : "Reference", tag, reference.Offset,
                        candidate: reference.IsCandidate);
        return new(tag, draft.PreviewTag, draft.AnimationEntityTag, draft.SelectedClipTag,
            links.DistinctBy(link => (link.TagHash, link.Kind, link.Role, link.Context))
                .OrderBy(link => link.Kind).ThenBy(link => link.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            draft.EntityCandidates.Order().ToArray(), diagnostics.Distinct().ToArray());
    }

    private string NameOf(uint target, Func<uint, string?>? resolveName) =>
        resolveName?.Invoke(target) ?? catalog.GetName(target)
            ?? (catalog.TryGetEntry(target, out var named) ? AssetCategories.CategoryOf(named) : "Asset");

    private void AddLink(List<RelatedAsset> links, uint tag, Func<uint, string?>? resolveName, uint target,
        AssetRelationshipKind kind, string role, uint sourceTag, int offset, AssetOpenContext? context, bool candidate)
    {
        if (target == tag || target is 0 or uint.MaxValue || !catalog.ContainsTag(target)) return;
        links.Add(new(target, NameOf(target, resolveName), kind, role, sourceTag, offset, context, candidate));
    }

    /** Only a repeated config owner and known component resource can establish a model link. */
    private ModelLink? ReadModelConfig(uint tag)
    {
        if (Class(tag) != ConfigClass) return null;
        var data = new TagReader(catalog.Read(tag));
        int resource = data.Relative(0x18);
        if (resource < 4 || !data.Has(resource, 0x1E0) || data.U32(resource - 4) is not (0x808072BD or 0x80807286)) return null;
        if (data.U32(resource) != tag) throw new InvalidDataException("Model component has the wrong config owner.");
        if (data.U32(resource - 4) == 0x80807286)
        {
            int primary = data.Relative(0x10);
            if (primary < 4 || data.U32(primary - 4) != 0x80807273)
                throw new InvalidDataException("Cloth model component has the wrong primary class.");
        }
        uint model = data.U32(resource + 0x1DC);
        return Class(model) == ModelClass ? new(tag, model, resource + 0x1DC) : null;
    }

    /** Entity config membership keeps shared models and clip lists tied to their original owner. */
    private EntityLinks ReadEntity(uint tag, CancellationToken token)
    {
        if (Class(tag) != EntityClass) throw new InvalidDataException("Animation owner is not an entity definition.");
        var data = new TagReader(catalog.Read(tag));
        var rows = data.Array(0x10, 0x80809C04, 12);
        var configs = new List<uint>(); var models = new List<ModelLink>(); var skeletons = new List<uint>();
        for (int index = 0; index < rows.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            uint config = data.U32(rows.Data + index * 12);
            if (Class(config) != ConfigClass) continue;
            configs.Add(config);
            if (ReadModelConfig(config) is { } model) models.Add(model);
            var configData = new TagReader(catalog.Read(config));
            int primary = configData.Relative(0x10), secondary = configData.Relative(0x18);
            if (primary >= 4 && secondary >= 4 && configData.Has(primary - 4, 4) && configData.Has(secondary - 4, 4)
                && configData.U32(primary - 4) == 0x80808545 && configData.U32(secondary - 4) == 0x80808546)
                skeletons.Add(config);
        }
        return new(tag, configs.ToArray(), models.ToArray(), new EntityAnimationCatalog(catalog).Read(tag).ToArray(), skeletons.ToArray());
    }

    /** Metadata readers expose direct typed links without loading geometry or scanning arbitrary words. */
    private IReadOnlyList<RelatedAsset> ReadDirect(uint tag, AssetOpenContext? context, CancellationToken token)
    {
        if (!catalog.TryGetEntry(tag, out var entry)) return [];
        var result = new List<RelatedAsset>();
        void Add(uint target, AssetRelationshipKind kind, string role, int offset, uint expected = 0,
            int? expectedType = null, int? expectedSubtype = null)
        {
            if (target is 0 or uint.MaxValue || !catalog.TryGetEntry(target, out var linked)
                || expected != 0 && (!AssetCategories.IsStructured(linked) || linked.Reference != expected)
                || expectedType.HasValue && linked.Type != expectedType.Value
                || expectedSubtype.HasValue && linked.SubType != expectedSubtype.Value) return;
            result.Add(new(target, "", kind, role, tag, offset, context));
        }
        if (entry.Type is 32 or 33 or 34 && catalog.TryGetEntry(entry.Reference, out var payload)
            && payload.Type == entry.Type + 8 && payload.SubType == entry.SubType)
            Add(entry.Reference, AssetRelationshipKind.Data, "Data", -1);
        if (!AssetCategories.IsStructured(entry)) return result;
        var data = new TagReader(catalog.Read(tag));
        void Array(int field, uint type, int stride, Action<int, int> action)
        {
            var rows = data.Array(field, type, stride);
            for (int index = 0; index < rows.Count; index++) { token.ThrowIfCancellationRequested(); action(rows.Data + index * stride, index); }
        }
        switch (entry.Reference)
        {
            case EntityClass:
                Array(0x10, 0x80809C04, 12, (p, i) => Add(data.U32(p), AssetRelationshipKind.Component, $"Component {i}", p, ConfigClass));
                break;
            case ConfigClass:
                if (ReadModelConfig(tag) is { } model) Add(model.Model, AssetRelationshipKind.Model, "Model", model.Offset, ModelClass);
                int primary = data.Relative(0x10);
                if (primary >= 4 && data.Has(primary, 0xD4) && data.U32(primary - 4) == 0x808036E2)
                    Add(data.U32(primary + 0xD0), AssetRelationshipKind.AnimationDefinition, "Animation definition", primary + 0xD0, 0x808036F6);
                break;
            case 0x808071A7:
                Add(data.U32(8), AssetRelationshipKind.Model, "Mesh data", 8, 0x80807194);
                Array(0x10, 0x80800014, 4, (p, _) => Add(data.U32(p), AssetRelationshipKind.Material, "Material", p, 0x808071E8));
                break;
            case 0x80807194:
                Array(0x28, 0x80807199, 16, (p, i) =>
                {
                    Add(data.U32(p), AssetRelationshipKind.Buffer, $"Indices {i}", p, expectedType: 32, expectedSubtype: 6);
                    Add(data.U32(p + 4), AssetRelationshipKind.Buffer, $"Positions {i}", p + 4, expectedType: 32, expectedSubtype: 4);
                    Add(data.U32(p + 8), AssetRelationshipKind.Buffer, $"Attributes {i}", p + 8, expectedType: 32, expectedSubtype: 4);
                });
                break;
            case ModelClass:
                Array(0x10, 0x80807378, 0x88, (p, i) =>
                {
                    foreach (var (field, role) in new[] { (0, "Positions"), (4, "Attributes"), (8, "Skin weights"), (16, "Indices") })
                        Add(data.U32(p + field), AssetRelationshipKind.Buffer, $"{role} {i}", p + field,
                            expectedType: 32, expectedSubtype: field == 16 ? 6 : 4);
                    Array(p + 0x18, 0x8080737E, 0x20, (part, _) => Add(data.U32(part), AssetRelationshipKind.Material, "Material", part, 0x808071E8));
                });
                break;
            case 0x808071E8:
                foreach (var (start, stage, subtype) in new[] { (0x48, "Vertex", 1), (0x2C8, "Pixel", 0), (0x368, "Compute", 6) })
                {
                    Add(data.U32(start), AssetRelationshipKind.Shader, stage + " shader", start, expectedType: 33, expectedSubtype: subtype);
                    Array(start + 8, 0x80807211, 8, (p, _) => Add(data.U32(p + 4), AssetRelationshipKind.Texture,
                        $"{stage} texture {data.U32(p)}", p + 4, expectedType: 32));
                }
                break;
            case 0x808036F6:
                Array(8, 0x80808F48, 4, (p, i) => Add(data.U32(p), AssetRelationshipKind.AnimationClip, $"Clip {i}", p, 0x80808F49));
                break;
        }
        return result;
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) && AssetCategories.IsStructured(entry) ? entry.Reference : 0;
    private static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or OverflowException or IndexOutOfRangeException or IOException or KeyNotFoundException;
}
