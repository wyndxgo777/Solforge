/** File: Skeleton data model: bone transforms, bind pose, skinning and cloth references. */
using System.Numerics;

namespace SunriseStudio.Animation;

/** Rotation, translation and uniform scale of one bone. */
public readonly record struct BoneTransform(Quaternion Rotation, Vector3 Translation, float Scale)
{
    public static BoneTransform Identity => new(Quaternion.Identity, Vector3.Zero, 1);
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);

    /** Local and object transforms use uniform scale and row-vector matrices. */
    public static BoneTransform FromMatrix(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation)
            || MathF.Abs(scale.X - scale.Y) > 0.001f || MathF.Abs(scale.X - scale.Z) > 0.001f)
            throw new InvalidDataException("Bone transform cannot be decomposed into uniform scale.");
        return new BoneTransform(Quaternion.Normalize(rotation), translation, scale.X);
    }
}

/** One skeleton bone: hierarchy links plus bind transforms in object and local space. */
public sealed record SkeletonBone(int Index, uint NameHash, string Name, int ParentIndex,
    int FirstChildIndex, int NextSiblingIndex, BoneTransform BindObject,
    BoneTransform InverseBindObject, BoneTransform BindLocal);

/** Cloth model and Havok asset tags tied to a config. */
public sealed record ClothModelReference(uint ConfigTag, uint ModelTag, uint HavokAssetTag);

/** Bones remain in package order because mesh influences use these indices. */
public sealed class SkeletonRig
{
    public uint EntityTag { get; }
    public uint ResourceTag { get; }
    public IReadOnlyList<uint> ModelTags { get; }
    public IReadOnlyList<ClothModelReference> ClothModels { get; }
    public IReadOnlyList<SkeletonBone> Bones { get; }
    public float MaximumBindInverseError { get; }
    internal int[] EvaluationOrder { get; }

    /** Rig arrays are exposed through read-only views. */
    internal SkeletonRig(uint entityTag, uint resourceTag, uint[] modelTags,
        SkeletonBone[] bones, int[] order, float inverseError, ClothModelReference[] clothModels)
    {
        EntityTag = entityTag;
        ResourceTag = resourceTag;
        ModelTags = Array.AsReadOnly(modelTags);
        ClothModels = Array.AsReadOnly(clothModels);
        Bones = Array.AsReadOnly(bones);
        EvaluationOrder = order;
        MaximumBindInverseError = inverseError;
    }

    /** Creates a pose at the rig's bind transforms. */
    public SkeletonPose CreateBindPose() => new(this);
}

/** Skin matrices transform a bind-space point into the current object-space pose. */
public sealed class SkeletonPose
{
    private readonly SkeletonRig rig;
    public SkeletonRig Rig => rig;
    private readonly BoneTransform[] local;
    private readonly Matrix4x4[] objects;
    private readonly Matrix4x4[] skin;
    private readonly Matrix4x4[] normalSkin;
    private readonly Matrix4x4[] clothObjects;
    private readonly Matrix4x4[] clothSkin;
    private readonly IReadOnlyList<Matrix4x4> clothView;
    private bool clothDirty = true;

    public IReadOnlyList<BoneTransform> LocalTransforms { get; }
    public IReadOnlyList<Matrix4x4> ObjectTransforms { get; }
    public IReadOnlyList<Matrix4x4> SkinMatrices { get; }

    /** Cloth receives the pose before render corrections and omits the animated scale lane. */
    public IReadOnlyList<Matrix4x4> ClothInputSkinMatrices
    {
        get
        {
            if (clothDirty)
            {
                for (int i = 0; i < clothSkin.Length; i++)
                {
                    var value = BoneTransform.FromMatrix(clothObjects[i]) with { Scale = 1 };
                    clothSkin[i] = rig.Bones[i].InverseBindObject.Matrix * value.Matrix;
                }
                clothDirty = false;
            }
            return clothView;
        }
    }

    /** Each pose owns its local transforms and evaluation buffers. */
    internal SkeletonPose(SkeletonRig rig)
    {
        this.rig = rig;
        local = new BoneTransform[rig.Bones.Count];
        objects = new Matrix4x4[rig.Bones.Count];
        skin = new Matrix4x4[rig.Bones.Count];
        normalSkin = new Matrix4x4[rig.Bones.Count];
        clothObjects = new Matrix4x4[rig.Bones.Count];
        clothSkin = new Matrix4x4[rig.Bones.Count];
        clothView = Array.AsReadOnly(clothSkin);
        LocalTransforms = Array.AsReadOnly(local);
        ObjectTransforms = Array.AsReadOnly(objects);
        SkinMatrices = Array.AsReadOnly(skin);
        ResetToBindPose();
    }

    /** Reset restores authored local transforms before evaluating the hierarchy. */
    public void ResetToBindPose()
    {
        for (int i = 0; i < local.Length; i++) local[i] = rig.Bones[i].BindLocal;
        Evaluate();
    }

    /** Edits do not reach the palette until Evaluate is called. */
    public void SetLocalTransform(int boneIndex, BoneTransform transform)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boneIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(boneIndex, local.Length);
        SkeletonReader.CheckTransform(transform);
        local[boneIndex] = transform with { Rotation = Quaternion.Normalize(transform.Rotation) };
    }

    /** Object-space poses are converted back to parent-relative transforms before palette evaluation. */
    public void SetObjectTransforms(IReadOnlyList<BoneTransform> transforms,
        IReadOnlyList<BoneTransform>? clothInput = null)
    {
        if (transforms.Count != rig.Bones.Count || clothInput is not null && clothInput.Count != rig.Bones.Count)
            throw new ArgumentException("Object pose count differs from its skeleton.", nameof(transforms));
        foreach (var transform in transforms) SkeletonReader.CheckTransform(transform);
        if (clothInput is not null)
            foreach (var transform in clothInput) SkeletonReader.CheckTransform(transform);
        foreach (int index in rig.EvaluationOrder)
        {
            int parent = rig.Bones[index].ParentIndex;
            if (parent < 0) local[index] = transforms[index];
            else
            {
                if (!Matrix4x4.Invert(transforms[parent].Matrix, out var inverse))
                    throw new InvalidDataException("Animation parent transform is singular.");
                local[index] = BoneTransform.FromMatrix(transforms[index].Matrix * inverse);
            }
        }
        Evaluate();
        if (clothInput is not null)
            for (int i = 0; i < clothObjects.Length; i++) clothObjects[i] = clothInput[i].Matrix;
    }

    /** Evaluate after editing local transforms; parents always precede children in this pass. */
    public void Evaluate()
    {
        clothDirty = true;
        foreach (int index in rig.EvaluationOrder)
        {
            var bone = rig.Bones[index];
            objects[index] = bone.ParentIndex < 0 ? local[index].Matrix
                : local[index].Matrix * objects[bone.ParentIndex];
            skin[index] = bone.InverseBindObject.Matrix * objects[index];
            if (!Matrix4x4.Invert(skin[index], out var inverse))
                throw new InvalidDataException("Skin matrix is singular.");
            normalSkin[index] = Matrix4x4.Transpose(inverse);
            clothObjects[index] = objects[index];
        }
    }

    /** Weights must be finite and nonnegative; their sum is normalized per vertex. */
    public Vector3 SkinPosition(Vector3 bindPosition, ReadOnlySpan<BoneWeight> influences)
    {
        Vector3 result = Vector3.Zero;
        float sum = 0;
        foreach (var influence in influences)
        {
            if (!float.IsFinite(influence.Weight) || influence.Weight < 0)
                throw new InvalidDataException("Invalid skin influence weight.");
            if (influence.Weight == 0) continue;
            if ((uint)influence.BoneIndex >= skin.Length)
                throw new InvalidDataException("Skin influence bone is outside the skeleton.");
            result += Vector3.Transform(bindPosition, skin[influence.BoneIndex]) * influence.Weight;
            sum += influence.Weight;
        }
        if (sum <= 0 || !float.IsFinite(sum)) throw new InvalidDataException("Skin weights have no finite sum.");
        return result / sum;
    }

    /** Skins a bind-space position by up to four weighted bone matrices. */
    public Vector3 SkinPosition(Vector3 bindPosition, VertexSkinWeights influences)
    {
        Span<BoneWeight> weights = stackalloc BoneWeight[4]
        {
            new(influences.Bone0, influences.Weights.X), new(influences.Bone1, influences.Weights.Y),
            new(influences.Bone2, influences.Weights.Z), new(influences.Bone3, influences.Weights.W)
        };
        return SkinPosition(bindPosition, weights);
    }

    /** Normals use inverse-transpose skin matrices and are normalized after blending. */
    public Vector3 SkinNormal(Vector3 bindNormal, VertexSkinWeights influences)
    {
        Span<int> bones = stackalloc int[4]
            { influences.Bone0, influences.Bone1, influences.Bone2, influences.Bone3 };
        Span<float> weights = stackalloc float[4]
            { influences.Weights.X, influences.Weights.Y, influences.Weights.Z, influences.Weights.W };
        Vector3 result = Vector3.Zero;
        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(weights[i]) || weights[i] < 0)
                throw new InvalidDataException("Invalid skin influence weight.");
            if (weights[i] == 0) continue;
            if ((uint)bones[i] >= normalSkin.Length)
                throw new InvalidDataException("Skin influence bone is outside the skeleton.");
            result += Vector3.TransformNormal(bindNormal, normalSkin[bones[i]]) * weights[i];
        }
        if (!float.IsFinite(result.LengthSquared()) || result.LengthSquared() < 1e-12f)
            throw new InvalidDataException("Skinned normal has no finite direction.");
        return Vector3.Normalize(result);
    }
}

/** One bone index and its weight. */
public readonly record struct BoneWeight(int BoneIndex, float Weight);
