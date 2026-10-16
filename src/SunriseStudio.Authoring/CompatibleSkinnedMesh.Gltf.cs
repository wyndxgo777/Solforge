using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Authoring;

/** glTF front end of CompatibleSkinnedMesh: maps the file's joints onto the installed rig and prepares the skin. */
public static partial class CompatibleSkinnedMesh
{
    /** Existing rig identities remain authoritative after mapping the file's joint order. */
    public static PreparedCompatibleSkin PrepareGltf(PackageCatalog catalog, string path,
        CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var file = GltfInterchange.ReadSkinned(path);
        var rig = new SkeletonReader(catalog).Read(options.EntityTag);
        if (file.Joints.Count != rig.Bones.Count)
            throw new InvalidDataException("Export every joint of the selected entity's skeleton.");
        var mapping = new int[file.Joints.Count];
        var assigned = new HashSet<int>();
        for (int index = 0; index < file.Joints.Count; index++)
        {
            var joint = file.Joints[index];
            if (joint.SourceEntityTag is uint owner && owner != options.EntityTag)
                throw new InvalidDataException("The file's bone properties name a different entity.");
            int mapped;
            if (joint.SourceBoneIndex is int ordinal && joint.SourceEntityTag == options.EntityTag)
                mapped = ordinal;
            else
            {
                var candidates = rig.Bones.Where(bone => joint.Name == bone.Name
                    || joint.Name == $"bone_{bone.Index:D4}_{bone.Name}"
                    || joint.Name == $"bone_{bone.NameHash:X8}").Select(bone => bone.Index).Distinct().ToArray();
                if (candidates.Length != 1)
                    throw new InvalidDataException($"Bone {joint.Name} needs a unique template name or its Sunrise properties.");
                mapped = candidates[0];
            }
            if ((uint)mapped >= rig.Bones.Count || !assigned.Add(mapped))
                throw new InvalidDataException("Imported bone identities are invalid or duplicated.");
            mapping[index] = mapped;
        }
        var bones = new CompatibleSkinBone[rig.Bones.Count];
        for (int index = 0; index < file.Joints.Count; index++)
        {
            var joint = file.Joints[index];
            int mapped = mapping[index];
            bones[mapped] = new(rig.Bones[mapped].NameHash, joint.ParentIndex < 0 ? -1 : mapping[joint.ParentIndex],
                joint.BindObject, joint.InverseBindObject);
        }
        var influences = file.Influences.Select(value => new VertexSkinWeights(mapping[value.Bone0],
            mapping[value.Bone1], mapping[value.Bone2], mapping[value.Bone3], value.Weights)).ToArray();
        return Prepare(catalog, new(file.Geometry, bones, influences), options, token);
    }
}
