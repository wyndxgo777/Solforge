using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: opens a weapon model and its animation clips. */
public sealed partial class StudioWorkspace
{
    /** The weapon scene owns its assembled art and full linked animation catalog. */
    public void OpenWeaponModel(uint weaponItem)
    {
        Open(weaponItem, new AssetOpenContext { WeaponItemTag = weaponItem });
        pendingView = new(World is null ? "asset:sunrise.preview" : "world:asset:sunrise.preview", null);
    }

    /** Clip selection retains the weapon item as the scene document. */
    public void PreviewWeaponAnimation(uint weaponItem, uint clipTag, uint presentationEntity)
    {
        Select(weaponItem, context: new AssetOpenContext(EntityTag: presentationEntity)
        { WeaponItemTag = weaponItem, SelectedAnimationClipTag = clipTag });
        pendingView = new(World is null ? "asset:sunrise.preview" : "world:asset:sunrise.preview", null);
    }

    /** Gameplay bindings may change only in a project-owned presentation definition. */
    public void BindWeaponAnimation(uint definitionTag, int slot, uint clipTag)
    {
        RequireEditable();
        if (slot is not (WeaponPresentationAuthoring.FireSlot or WeaponPresentationAuthoring.ReloadSlot)
            || !nativeAdditions.ContainsKey(definitionTag))
            throw new InvalidDataException("Choose a project weapon's fire or reload binding.");
        byte[] changed = WeaponPresentationAuthoring.BindClip(Catalog!, Catalog!.Read(definitionTag), slot, clipTag);
        ApplyAssetEdits(new Dictionary<uint, byte[]> { [definitionTag] = changed }, "Changed weapon animation binding");
    }

    /** The native weapon resource supplies the animation owner without a creation receipt. */
    private StudioDocument ReadWeaponModelDocument(PackageCatalog catalog, PackageEntry entry,
        string name, AssetOpenContext context)
    {
        if (entry.Reference != InvestmentCatalog.ItemClass || entry.TagHash != context.WeaponItemTag)
            throw new InvalidDataException("Choose a weapon item for its assembled model view.");
        var document = ReadDocument(catalog, entry, name, false, context with { WeaponItemTag = 0 });
        WeaponArchetypeSource art;
        try { art = WeaponArchetypeAuthoring.Read(catalog, document.Bytes); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Unreadable art still opens the item, with no model and the reason as a preview note.
            return document with { OpenContext = context, PreviewError = $"No weapon model: {error.Message}" };
        }
        uint owner = 0;
        string? ownerError = null;
        // Without an owner the weapon opens at rest; the scene diagnostics carry the reason.
        try { owner = WeaponPresentationReader.Read(catalog, art.EntityTag, WeaponArchetypeAuthoring.GearKey(catalog, art)); }
        catch (Exception error) when (error is NotSupportedException or InvalidDataException or KeyNotFoundException)
        { ownerError = error.Message; }
        if (context.EntityTag != 0 && context.EntityTag != owner)
            throw new InvalidDataException("The selected animation owner does not belong to this weapon.");
        ReportProgress("Preparing weapon scene");
        PreparedWeaponAnimationPreview prepared;
        try
        {
            prepared = WeaponAnimationPreviewSource.Prepare(catalog, art.EntityTag, owner,
                art.Variants.Select(part => part.EntityTag).ToArray(), OperationToken, ResolveHash,
                tag => catalog.GetName(tag) ?? ResolveClipName(tag), ownerError);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The item stays open with the failure shown; each art part still opens on its own.
            var parts = document.Connections?.Related.ToList() ?? [];
            parts.AddRange(art.Variants.Select(part => part.EntityTag).Distinct().Select(tag =>
                new RelatedAsset(tag, DisplayName(tag), AssetRelationshipKind.Component, "Weapon art part", entry.TagHash, -1)));
            return document with
            {
                OpenContext = context,
                PreviewError = $"Assembled weapon view failed: {error.Message}",
                Connections = document.Connections is { } connections ? connections with { Related = parts } : null
            };
        }
        var clips = prepared.Animation?.Clips ?? [];
        if (context.SelectedAnimationClipTag != 0 && !clips.Any(clip => clip.ClipTag == context.SelectedAnimationClipTag))
            throw new InvalidDataException("The selected clip is not linked to this weapon.");
        prepared.Scene.Name = name;
        if (NativeGearPlating.ReadItemDyes(catalog, document.Bytes, out string? dyeError) is { } dyes)
            NativeGearPlating.SetDyes(prepared.Scene, dyes);
        else prepared.Scene.Diagnostics.Add($"Dyes: unreadable, using defaults ({dyeError})");
        var native = PrepareScene(prepared.Scene, out string nativeError, assetPreview: true);
        var related = document.Connections?.Related.ToList() ?? [];
        foreach (var clip in clips.DistinctBy(clip => clip.ClipTag))
            related.Add(new(clip.ClipTag, prepared.Animation!.ClipNames[clip.ClipTag],
                AssetRelationshipKind.AnimationClip, "Weapon animation", clip.DefinitionTag, -1,
                new AssetOpenContext(EntityTag: owner)
                { WeaponItemTag = entry.TagHash, SelectedAnimationClipTag = clip.ClipTag }));
        foreach (var model in prepared.Scene.Instances.DistinctBy(instance => instance.Mesh.TagHash))
            related.Add(new(model.Mesh.TagHash, DisplayName(model.Mesh.TagHash), AssetRelationshipKind.Model,
                "Weapon model", model.ModelSourceConfigTag, -1,
                new(model.ObjectClassTag, model.Mesh.TagHash, model.ModelSourceConfigTag)));
        return document with
        {
            Scene = prepared.Scene, Animation = prepared.Animation, NativePlan = native,
            NativeError = nativeError, OpenContext = context with { EntityTag = owner },
            Connections = new(entry.TagHash, art.EntityTag, owner, context.SelectedAnimationClipTag,
                related, owner == 0 ? [] : [owner], document.Connections?.Diagnostics ?? []),
            Surface = AssetSurface.Read(catalog, entry.TagHash, scene: prepared.Scene, nameResolver: ResolveName,
                token: OperationToken, decodedTextures: native?.Textures),
            SourceTags = (document.SourceTags ?? new HashSet<uint>()).Concat(prepared.SourceTags)
                .Concat(clips.SelectMany(clip => new[]
                    { clip.ClipTag, clip.ConfigTag, clip.DefinitionTag }))
                .Concat(new[] { entry.TagHash, WeaponArchetypeAuthoring.GearTableTag,
                    WeaponArchetypeAuthoring.ArrangementTableTag, WeaponArchetypeAuthoring.ArtMapTag,
                    WeaponArchetypeAuthoring.RuntimeMapTag }).ToHashSet()
        };
    }
}
