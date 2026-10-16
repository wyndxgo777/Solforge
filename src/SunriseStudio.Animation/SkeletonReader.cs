using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Typed resource pairs distinguish skeletons from other entity components. */
public sealed class SkeletonReader(PackageCatalog catalog)
{
    public Func<uint, string?>? NameResolver { get; set; }

    /** Accept an entity class or its skeleton config and preserve package bone indices. */
    public SkeletonRig Read(uint tag)
    {
        uint entityTag = 0;
        uint resourceTag = tag;
        var models = new HashSet<uint>();
        var clothModels = new List<ClothModelReference>();
        if (Class(tag) == 0x80809C0F)
        {
            entityTag = tag;
            var entity = new TagData(catalog.Read(tag));
            var skeletons = new HashSet<uint>();
            foreach (int row in entity.Rows(0x10, 0x80809C04, 12))
            {
                uint configTag = entity.U32(row);
                if (Class(configTag) != 0x80809C36) continue;
                var config = new TagData(catalog.Read(configTag));
                int resource = config.Pointer(0x18);
                uint resourceClass = config.U32(resource - 4);
                if (resourceClass == 0x80808546 && config.U32(config.Pointer(0x10) - 4) == 0x80808545)
                    skeletons.Add(configTag);
                if (resourceClass == 0x808072BD && config.Has(resource + 0x1DC, 4))
                {
                    uint model = config.U32(resource + 0x1DC);
                    if (Class(model) == 0x808073A5) models.Add(model);
                }
                if (resourceClass == 0x80807286 && ClothReference(configTag) is { } cloth) clothModels.Add(cloth);
            }
            if (skeletons.Count != 1)
                throw new NotSupportedException($"Entity 0x{tag:X8} has {skeletons.Count} supported skeleton resources.");
            resourceTag = skeletons.Single();
        }
        if (Class(resourceTag) != 0x80809C36)
            throw new NotSupportedException("Skeleton input must be an entity or skeleton config tag.");
        var data = new TagData(catalog.Read(resourceTag));
        int primary = data.Pointer(0x10), secondary = data.Pointer(0x18);
        if (data.U32(primary - 4) != 0x80808545 || data.U32(secondary - 4) != 0x80808546)
            throw new NotSupportedException("Config does not contain the supported skeleton resource pair.");
        int[] hierarchy = data.Rows(secondary + 0x80, 0x80808A08, 16);
        int[] bind = data.Rows(secondary + 0x90, 0x80809F75, 32);
        int[] inverse = data.Rows(secondary + 0xA0, 0x80809F75, 32);
        if (hierarchy.Length is < 1 or > 4096 || bind.Length != hierarchy.Length
            || inverse.Length != hierarchy.Length)
            throw new InvalidDataException("Skeleton hierarchy and transform counts disagree.");
        var parents = hierarchy.Select(row => data.I32(row + 4)).ToArray();
        int[] order = EvaluationOrder(parents);
        var objectTransforms = bind.Select(data.Transform).ToArray();
        var inverseTransforms = inverse.Select(data.Transform).ToArray();
        var bones = new SkeletonBone[hierarchy.Length];
        float maximumError = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            int row = hierarchy[i];
            uint hash = data.U32(row);
            int child = data.I32(row + 8), sibling = data.I32(row + 12);
            if (child < -1 || child >= bones.Length || sibling < -1 || sibling >= bones.Length)
                throw new InvalidDataException("Skeleton child link is outside the hierarchy.");
            var product = objectTransforms[i].Matrix * inverseTransforms[i].Matrix;
            maximumError = MathF.Max(maximumError, IdentityError(product));
            var local = parents[i] < 0 ? objectTransforms[i]
                : BoneTransform.FromMatrix(objectTransforms[i].Matrix * inverseTransforms[parents[i]].Matrix);
            bones[i] = new SkeletonBone(i, hash,
                NameResolver?.Invoke(hash) ?? BoneNameCandidates.Resolve(hash) ?? $"bone_{hash:X8}",
                parents[i], child, sibling, objectTransforms[i], inverseTransforms[i], local);
        }
        if (maximumError > 0.002f)
            throw new InvalidDataException($"Skeleton bind and inverse transforms disagree by {maximumError:G6}.");
        ValidateChildren(bones);
        return new SkeletonRig(entityTag, resourceTag, models.Order().ToArray(), bones, order,
            maximumError, clothModels.Distinct().ToArray());
    }

    /**
     * The cloth model and Havok cloth asset a component config names.
     * @return Null unless the config pairs a 0x80807273 primary with a 0x80807286 cloth resource.
     */
    public ClothModelReference? ClothReference(uint configTag)
    {
        if (Class(configTag) != 0x80809C36) return null;
        var config = new TagData(catalog.Read(configTag));
        int resource = config.Pointer(0x18);
        if (config.U32(resource - 4) != 0x80807286 || !config.Has(resource + 0x358, 4)
            || config.U32(config.Pointer(0x10) - 4) != 0x80807273)
            return null;
        // The resource names the model at +0x1DC and the cloth asset at +0x358.
        uint model = config.U32(resource + 0x1DC), asset = config.U32(resource + 0x358);
        return Class(model) == 0x808073A5 && Class(asset) == 0x8080727A ? new(configTag, model, asset) : null;
    }

    private uint Class(uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry.Reference : 0;

    /** A parent cycle is rejected before evaluating any pose. */
    private static int[] EvaluationOrder(int[] parents)
    {
        var states = new byte[parents.Length];
        var order = new List<int>(parents.Length);
        void Visit(int index)
        {
            if (states[index] == 2) return;
            if (states[index] == 1) throw new InvalidDataException("Skeleton parent cycle.");
            states[index] = 1;
            int parent = parents[index];
            if (parent < -1 || parent >= parents.Length)
                throw new InvalidDataException("Skeleton parent index is outside the hierarchy.");
            if (parent >= 0) Visit(parent);
            states[index] = 2;
            order.Add(index);
        }
        for (int i = 0; i < parents.Length; i++) Visit(i);
        return order.ToArray();
    }

    /** Every child must occur once in its parent's sibling chain. */
    private static void ValidateChildren(SkeletonBone[] bones)
    {
        var seen = new bool[bones.Length];
        foreach (var parent in bones)
        {
            int child = parent.FirstChildIndex;
            while (child >= 0)
            {
                if (seen[child] || bones[child].ParentIndex != parent.Index)
                    throw new InvalidDataException("Skeleton child chain disagrees with its parent indices.");
                seen[child] = true;
                child = bones[child].NextSiblingIndex;
            }
        }
        if (bones.Any(b => b.ParentIndex >= 0 && !seen[b.Index]))
            throw new InvalidDataException("Skeleton parent omits a child from its chain.");
    }

    /** Throws when a transform's rotation is not unit length or its values are not finite. */
    internal static void CheckTransform(BoneTransform value)
    {
        float length = value.Rotation.LengthSquared();
        if (!float.IsFinite(length) || MathF.Abs(length - 1) > 0.01f
            || !float.IsFinite(value.Scale) || value.Scale <= 0
            || !float.IsFinite(value.Translation.X) || !float.IsFinite(value.Translation.Y)
            || !float.IsFinite(value.Translation.Z))
            throw new InvalidDataException("Bone transform contains invalid rotation, scale, or translation.");
    }

    private static float IdentityError(Matrix4x4 m)
    {
        float[] values = [m.M11 - 1, m.M12, m.M13, m.M14, m.M21, m.M22 - 1, m.M23, m.M24,
            m.M31, m.M32, m.M33 - 1, m.M34, m.M41, m.M42, m.M43, m.M44 - 1];
        return values.Max(MathF.Abs);
    }
}
