using SunriseStudio.Animation;

namespace SunriseStudio.Viewport;

/** A skin drawn rigidly on one bone: every one of Entries palette entries is that bone's object transform. */
public sealed record BoneAttachment(int Bone, int Entries);

/** Attached-skin half of AssetAnimationSource: skins that follow a single rig bone rigidly. */
public sealed partial class AssetAnimationSource
{
    /** Skins that follow one rig bone rigidly instead of their own weights. */
    public IReadOnlyDictionary<ModelSkinBinding, BoneAttachment> AttachedSkins { get; } =
        new Dictionary<ModelSkinBinding, BoneAttachment>(ReferenceEqualityComparer.Instance);

    private AssetAnimationSource(AssetAnimationSource source, IReadOnlyList<ModelSkinBinding> skins,
        IReadOnlyDictionary<ModelSkinBinding, BoneAttachment> attached, IReadOnlyDictionary<uint, string> clipNames,
        AnimationRigBinding? binding)
    {
        catalog = source.catalog; Rig = source.Rig; SkinBindings = skins; Binding = binding;
        Clips = source.Clips; ClipNames = clipNames; BaseClipTags = source.BaseClipTags;
        Diagnostics = source.Diagnostics; BindingError = source.BindingError; WeaponBinding = source.WeaponBinding;
        RigidSkins = source.RigidSkins;
        AttachedSkins = attached;
    }

    /**
     * A copy of source with extra skins; attached keys are added to SkinBindings too.
     * @param clipNames Replaces the source's clip names; null keeps them.
     * @param binding Replaces the source's binding, such as a copy with a rest layer; null keeps it.
     */
    public static AssetAnimationSource WithSkins(AssetAnimationSource source, IReadOnlyList<ModelSkinBinding> extraSkins,
        IReadOnlyDictionary<ModelSkinBinding, BoneAttachment> attached, IReadOnlyDictionary<uint, string>? clipNames = null,
        AnimationRigBinding? binding = null)
    {
        var skins = source.SkinBindings.Concat(extraSkins).Concat(attached.Keys)
            .Distinct<ModelSkinBinding>(ReferenceEqualityComparer.Instance).ToArray();
        var merged = new Dictionary<ModelSkinBinding, BoneAttachment>(ReferenceEqualityComparer.Instance);
        foreach (var pair in source.AttachedSkins.Concat(attached)) merged[pair.Key] = pair.Value;
        return new(source, Array.AsReadOnly(skins), merged, clipNames ?? source.ClipNames, binding ?? source.Binding);
    }
}
