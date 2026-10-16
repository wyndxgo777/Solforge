/** File: Reads per-vertex bone weights and skin palettes (skeleton or cloth) from model tags. */
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Animation;

/** Up to four bone influences for one vertex with their weights. */
public readonly record struct VertexSkinWeights(int Bone0, int Bone1, int Bone2, int Bone3,
    Vector4 Weights);

/** Which bone set a skin palette indexes. */
public enum SkinPaletteKind { Skeleton, ClothInput }

/** Tags a cloth skin was derived from, with the palette size and worst inverse-bind error. */
public sealed record ClothSkinProvenance(uint ConfigTag, uint AssetTag, uint HavokTag,
    uint DeclarationTag, int PaletteSize, float MaximumInverseBindError);

/** Vertex weights retain the original stream order before scene mesh compaction. */
public sealed record ModelSkinBinding(uint ModelTag, int MeshIndex, uint PositionHeaderTag,
    int PositionStride, IReadOnlyList<int> PartOffsets, IReadOnlyList<VertexSkinWeights> Vertices)
{
    public SkinPaletteKind PaletteKind { get; init; }
    public uint WeightHeaderTag { get; init; }
    public uint WeightPayloadTag { get; init; }
    public ClothSkinProvenance? Cloth { get; init; }
}

/** These pre-BL layouts are checked against shader inputs and byte-normalized weights. */
public sealed partial class ModelSkinReader(PackageCatalog catalog)
{
    /** Only a model referenced by the entity that supplied this rig is accepted. */
    public IReadOnlyList<ModelSkinBinding> Read(uint modelTag, SkeletonRig rig)
    {
        if (!rig.ModelTags.Contains(modelTag))
            throw new InvalidDataException("The model is not linked to the skeleton's entity.");
        return ReadModel(modelTag, rig);
    }

    /** Like Read, but for a model from another entity. The caller vouches that it uses the rig's bone order. */
    public IReadOnlyList<ModelSkinBinding> ReadExternal(uint modelTag, SkeletonRig rig) => ReadModel(modelTag, rig);

    /** Reads the weights packed in each mesh's position stream: 8, 12 or 16 bytes per vertex. */
    private IReadOnlyList<ModelSkinBinding> ReadModel(uint modelTag, SkeletonRig rig)
    {
        if (!catalog.TryGetEntry(modelTag, out var modelEntry) || modelEntry.Reference != 0x808073A5)
            throw new NotSupportedException("Skin input is not a build-86657 entity model.");
        byte[] model = catalog.Read(modelTag);
        var result = new List<ModelSkinBinding>();
        int meshIndex = 0;
        foreach (int mesh in Rows(model, 0x10, 0x80807378, 0x88))
        {
            uint headerTag = U32(model, mesh);
            if (!catalog.TryGetEntry(headerTag, out var headerEntry)
                || headerEntry.Type != 32 || headerEntry.SubType != 4)
                throw new InvalidDataException("Skin position stream header has an unexpected package type.");
            byte[] header = catalog.Read(headerTag);
            int size = checked((int)U32(header, 0));
            int stride = U16(header, 4);
            uint separateWeights = U32(model, mesh + 8);
            if (separateWeights is not (0 or uint.MaxValue))
                throw new NotSupportedException("Separate skin-weight streams are not decoded.");
            if (stride is not (8 or 12 or 16) || size == 0 || size % stride != 0)
                throw new NotSupportedException($"Entity skin position stride {stride} is not decoded.");
            byte[] positions = catalog.Read(headerEntry.Reference);
            if (size > positions.Length) throw new InvalidDataException("Skin position stream is truncated.");
            var weights = new VertexSkinWeights[size / stride];
            for (int i = 0; i < weights.Length; i++)
            {
                int offset = i * stride;
                weights[i] = stride switch
                {
                    8 => new VertexSkinWeights(I16(positions, offset + 6), 0, 0, 0, new Vector4(1, 0, 0, 0)),
                    12 => new VertexSkinWeights(positions[offset + 8], positions[offset + 9], 0, 0,
                        new Vector4(positions[offset + 10], positions[offset + 11], 0, 0) / 255f),
                    16 => new VertexSkinWeights(positions[offset + 12], positions[offset + 13],
                        positions[offset + 14], positions[offset + 15], new Vector4(positions[offset + 8],
                            positions[offset + 9], positions[offset + 10], positions[offset + 11]) / 255f),
                    _ => throw new UnreachableException()
                };
                Validate(weights[i], rig.Bones.Count);
            }
            result.Add(new ModelSkinBinding(modelTag, meshIndex++, headerTag, stride,
                Array.AsReadOnly(Rows(model, mesh + 0x18, 0x8080737E, 0x20)), Array.AsReadOnly(weights)));
        }
        return result.AsReadOnly();
    }

    /** A zero weight can carry the package's unused-bone sentinel. */
    private static void Validate(VertexSkinWeights value, int boneCount)
    {
        Span<int> bones = stackalloc int[4] { value.Bone0, value.Bone1, value.Bone2, value.Bone3 };
        Span<float> weights = stackalloc float[4]
            { value.Weights.X, value.Weights.Y, value.Weights.Z, value.Weights.W };
        float sum = 0;
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] > 0 && (uint)bones[i] >= boneCount)
                throw new NotSupportedException("A weighted bone does not belong to the entity's skeleton.");
            sum += weights[i];
        }
        if (MathF.Abs(sum - 1) > 0.00001f)
            throw new InvalidDataException("Packed skin weights do not sum to 255.");
    }

    private static ReadOnlySpan<byte> Slice(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("Skin range exceeds its tag.");
        return data.AsSpan(offset, length);
    }
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));
    private static short I16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(Slice(data, offset, 2));

    /** Mesh and part arrays carry distinct element classes and fixed strides. */
    private static int[] Rows(byte[] data, int field, uint expectedClass, int stride)
    {
        long count = BinaryPrimitives.ReadInt64LittleEndian(Slice(data, field, 8));
        if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid skin array count.");
        if (count == 0) return [];
        long relative = BinaryPrimitives.ReadInt64LittleEndian(Slice(data, field + 8, 8));
        int header = checked((int)(field + 8L + relative));
        if (U32(data, header - 4) != 0x80809FBD || U32(data, header + 8) != expectedClass)
            throw new InvalidDataException("Skin array has an unexpected element class.");
        int start = checked(header + 16);
        _ = Slice(data, start, checked((int)count * stride));
        return Enumerable.Range(0, (int)count).Select(i => start + i * stride).ToArray();
    }
}
