using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** One installed rig bone: name hash, parent and bind matrices in object space. */
public sealed record CompatibleSkinBone(uint NameHash, int ParentIndex, Matrix4x4 BindObject,
    Matrix4x4 InverseBindObject);
/** Imported geometry with the rig bones and per-vertex influences it was weighted against. */
public sealed record CompatibleSkinnedMeshInput(InterchangeMesh Geometry,
    IReadOnlyList<CompatibleSkinBone> Bones, IReadOnlyList<VertexSkinWeights> Influences);
/** Target entity and model, the source part to replace, and the package and first entry index for the new assets. */
public sealed record CompatibleSkinReplacementOptions(uint EntityTag, uint ModelTag, int SourcePartOffset,
    string Name, ushort PackageId, int FirstEntryIndex);
/** Model part and material a replacement takes its draw state from. */
public sealed record CompatibleSkinTemplate(uint ModelTag, int PartOffset, uint MaterialTag);
/** Compiled skinned replacement: new tags, counts, kept and removed source parts, assets to add and raw replacements. */
public sealed record PreparedCompatibleSkin(uint EntityTag, uint ModelTag, uint SkeletonTag, uint MaterialTag,
    int VertexCount, int TriangleCount, IReadOnlyList<int> RetainedSourceParts, int RemovedSourceParts,
    IReadOnlyList<ProjectAssetAddition> Additions, IReadOnlyDictionary<uint, byte[]> Replacements,
    IReadOnlyDictionary<uint, string> SourceHashes)
{
    public int ConsumedBones { get; init; }
}

/** A replacement keeps the installed rig and compiles only the checked four-weight matrix profile. */
public static partial class CompatibleSkinnedMesh
{
    private const string MatrixShaderHash = "880AC672C57AC1947343E344CBAD49EEB3B6A7759C68FE28EC1504D1A52997EF";
    private const string ShadowShaderHash = "A8558442B245BD8CDF3E40E4115D7248A903B44D0E864BBD9C1FF1B3986E15F8";
    private const uint ModelClass = 0x808073A5, MeshClass = 0x80807378, PartClass = 0x8080737E;
    private const int EntryCount = 6;
    private const float BindTolerance = .0002f;

    /** Template choices come from installed geometry even after a project replaces the model. */
    public static IReadOnlyList<CompatibleSkinTemplate> Templates(PackageCatalog catalog, uint entityTag,
        CancellationToken token = default)
    {
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var rig = new SkeletonReader(originals).Read(entityTag);
        var scene = new AssetSceneLoader(originals).Load(entityTag, token);
        var shaders = new Dictionary<uint, bool>();
        bool Supported(MeshData mesh)
        {
            if (mesh.LodLevel != 0 || !rig.ModelTags.Contains(mesh.TagHash)
                || mesh.Native is not { Kind: NativeMeshKind.Entity, RenderStage: 0 } native || !Profile(native)) return false;
            if (!shaders.TryGetValue(native.VertexShaderDataTag, out bool accepted))
                shaders[native.VertexShaderDataTag] = accepted = Hash(originals.Read(native.VertexShaderDataTag)) == MatrixShaderHash;
            return accepted;
        }
        return scene.Instances.Where(instance => instance.InitiallyVisible != false)
            .Select(instance => instance.Mesh).Where(Supported)
            .Select(mesh => new CompatibleSkinTemplate(mesh.TagHash, mesh.PartIndex, mesh.MaterialTag))
            .Distinct().OrderBy(value => value.ModelTag).ThenBy(value => value.PartOffset).ToArray();
    }

    /** Input bones and vertices use package order and Z-up object space; no bone retargeting occurs. */
    public static PreparedCompatibleSkin Prepare(PackageCatalog catalog, CompatibleSkinnedMeshInput input,
        CompatibleSkinReplacementOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input.Geometry); ArgumentNullException.ThrowIfNull(input.Bones);
        ArgumentNullException.ThrowIfNull(input.Influences);
        long sourceRevision = catalog.OverlayRevision;
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > 8192 - EntryCount)
            throw new InvalidDataException("Skin buffers need six unused entries in a new package.");
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var modelEntry = Require(originals, options.ModelTag);
        if (modelEntry.Reference != ModelClass || !catalog.ContainsInstalledPackage(modelEntry.PackageId))
            throw new NotSupportedException("Choose an installed entity model to replace.");
        var rig = new SkeletonReader(originals).Read(options.EntityTag);
        if (!rig.ModelTags.Contains(options.ModelTag))
            throw new NotSupportedException("The model must use this entity's non-cloth skeleton.");
        ValidateBones(input.Bones, rig);
        var activeRig = new SkeletonReader(catalog).Read(options.EntityTag);
        if (!activeRig.ModelTags.Contains(options.ModelTag) || activeRig.ResourceTag != rig.ResourceTag)
            throw new NotSupportedException("The entity no longer links this installed model and rig.");
        ValidateBones(input.Bones, activeRig);
        if (input.Influences.Count != input.Geometry.Positions.Length)
            throw new InvalidDataException("Each imported vertex needs four package-order skin influences.");
        token.ThrowIfCancellationRequested();
        var scene = new AssetSceneLoader(originals) { IncludeLowerLods = true }.Load(options.EntityTag, token);
        var selectedInstance = scene.Instances
            .FirstOrDefault(instance => instance.Mesh.TagHash == options.ModelTag && instance.Mesh.PartIndex == options.SourcePartOffset)
            ?? throw new InvalidDataException("The selected part does not belong to the template entity.");
        var selected = selectedInstance.Mesh;
        if (selectedInstance.InitiallyVisible == false)
            throw new NotSupportedException("Choose a template part that is visible in the entity's initial state.");
        if (selected.LodLevel != 0 || selected.Native is not { Kind: NativeMeshKind.Entity, RenderStage: 0 } native
            || !Profile(native) || Hash(originals.Read(native.VertexShaderDataTag)) != MatrixShaderHash)
            throw new NotSupportedException("Choose a LOD0 part with the checked declaration-16 matrix skinning shader.");
        var hashes = new Dictionary<uint, string>();
        var activeSources = new Dictionary<uint, byte[]>();
        byte[] Active(uint tag)
        {
            if (activeSources.TryGetValue(tag, out var bytes)) return bytes;
            bytes = catalog.Read(tag); activeSources.Add(tag, bytes); hashes[tag] = Hash(bytes); return bytes;
        }
        byte[] Read(uint tag)
        { Active(tag); return originals.Read(tag); }
        Read(options.EntityTag); Read(rig.ResourceTag); Read(selected.MaterialTag); Read(native.VertexShaderDataTag);
        byte[] model = Read(options.ModelTag);
        if (model.Length < 0xA0 || I64(model, 0) != model.Length)
            throw new InvalidDataException("Entity model header is invalid.");
        byte[] activeEntity = Active(options.EntityTag);
        if (!NativeRows.Rows(activeEntity, 0x10, 0x80809C04, 12).Any(row => U32(activeEntity, row) == selectedInstance.ModelSourceConfigTag))
            throw new NotSupportedException("The entity no longer uses the installed template's model component.");
        byte[] activeConfig = Active(selectedInstance.ModelSourceConfigTag);
        int consumedBones = ConsumedBoneCount(activeConfig, model, options.ModelTag);
        if (consumedBones > rig.Bones.Count)
            throw new InvalidDataException("The model consumes more bones than its entity skeleton supplies.");
        var weights = EncodeInfluences(input.Influences, consumedBones);
        var geometry = MeshAssetImporter.EncodeGeometry(input.Geometry, token);
        var positions = new byte[checked(input.Geometry.Positions.Length * 16)];
        for (int vertex = 0; vertex < input.Geometry.Positions.Length; vertex++)
        {
            geometry.Positions.AsSpan(vertex * 8, 6).CopyTo(positions.AsSpan(vertex * 16));
            weights.AsSpan(vertex * 8, 8).CopyTo(positions.AsSpan(vertex * 16 + 8));
        }
        var sourcePositions = scene.Instances.Where(instance => instance.Mesh.TagHash == options.ModelTag)
            .SelectMany(instance => instance.Mesh.Vertices.Select(vertex => vertex.Position)).ToArray();
        var minimum = sourcePositions.Aggregate(Vector3.Min); var maximum = sourcePositions.Aggregate(Vector3.Max);
        if (input.Geometry.Positions.Any(position => position.X < minimum.X || position.Y < minimum.Y || position.Z < minimum.Z
            || position.X > maximum.X || position.Y > maximum.Y || position.Z > maximum.Z))
            throw new NotSupportedException("Replacement vertices must stay within the source model's bind bounds.");
        int[] meshRows = NativeRows.Rows(model, 0x10, MeshClass, 0x88);
        int originalPartCount = meshRows.Sum(row => NativeRows.Rows(model, row + 0x18, PartClass, 0x20).Length);
        int sourceMesh = meshRows.Single(row => NativeRows.Rows(model, row + 0x18, PartClass, 0x20).Contains(options.SourcePartOffset));
        int[] sourceParts = NativeRows.Rows(model, sourceMesh + 0x18, PartClass, 0x20);
        int sourcePart = options.SourcePartOffset;
        int Stage(int ordinal)
        {
            for (int stage = 0; stage < 23; stage++)
                if (ordinal >= U16(model, sourceMesh + 0x28 + stage * 2) && ordinal < U16(model, sourceMesh + 0x2A + stage * 2)) return stage;
            throw new InvalidDataException("A model part has no render-stage range.");
        }
        var retained = sourceParts.Select((part, ordinal) => (Part: part, Stage: Stage(ordinal)))
            .Where(value => U32(model, value.Part + 8) == U32(model, sourcePart + 8)
                && U32(model, value.Part + 12) == U32(model, sourcePart + 12)
                && U16(model, value.Part + 6) == U16(model, sourcePart + 6)
                && model[value.Part + 0x1B] == model[sourcePart + 0x1B]).OrderBy(value => value.Stage).ToArray();
        uint visibilityCount = U32(model, 0x30);
        if (retained.Length == 0 || visibilityCount == 0 || (visibilityCount & 31) != 0
            || retained.Any(value => U16(model, value.Part + 0x16) >= visibilityCount))
            throw new InvalidDataException("The template part visibility indices leave their original mask.");
        uint[] selectedMaterials = [];
        if (retained.Any(value => BinaryPrimitives.ReadInt16LittleEndian(model.AsSpan(value.Part + 4)) != -1))
        {
            var stateReads = new HashSet<uint>();
            var initial = new EntityInitialStateReader(catalog) { Reads = stateReads }
                .Read(options.EntityTag, selectedInstance.ModelSourceConfigTag);
            foreach (uint tag in stateReads) Active(tag);
            int resource = checked((int)(0x18 + I64(activeConfig, 0x18)));
            var selection = EntityMaterialSelector.Read(activeConfig, resource, initial.EnabledParts, initial.Unresolved);
            if (selection.Unresolved is not null)
                throw new NotSupportedException("The template's initial material state is unresolved: " + selection.Unresolved);
            selectedMaterials = selection.Materials;
        }
        var checkedMaterials = new HashSet<(uint Tag, int Stage)>();
        uint materialTag = 0;
        foreach (var part in retained)
        {
            if (BinaryPrimitives.ReadInt16LittleEndian(model.AsSpan(sourceMesh + 0x58 + part.Stage * 2)) != native.NativeDeclarationId)
                throw new NotSupportedException("A retained render pass uses a different vertex declaration.");
            uint[] materials = PartMaterials(model, part.Part, activeConfig);
            if (part.Part == sourcePart)
            {
                int group = BinaryPrimitives.ReadInt16LittleEndian(model.AsSpan(part.Part + 4));
                materialTag = group == -1 ? U32(model, part.Part) : selectedMaterials[group];
                if (materialTag == uint.MaxValue)
                    throw new NotSupportedException("The selected part has no material in its initial state.");
            }
            foreach (uint tag in materials)
            {
                if (!checkedMaterials.Add((tag, part.Stage))) continue;
                string expected = part.Stage switch { 0 => MatrixShaderHash, 3 => ShadowShaderHash,
                    _ => throw new NotSupportedException("A retained render pass has no checked skinning profile.") };
                ValidateMaterial(catalog, tag, expected, Active);
            }
        }
        uint Tag(int offset) => checked(0x80800000u + ((uint)options.PackageId << 13) + (uint)(options.FirstEntryIndex + offset));
        var partBytes = new byte[checked(retained.Length * 0x20)];
        for (int index = 0; index < retained.Length; index++)
        {
            model.AsSpan(retained[index].Part, 0x20).CopyTo(partBytes.AsSpan(index * 0x20));
            Put16(partBytes, index * 0x20 + 6, 3); Put32(partBytes, index * 0x20 + 8, 0);
            Put32(partBytes, index * 0x20 + 12, checked((uint)input.Geometry.Indices.Length));
        }
        byte[] mesh = model.AsSpan(sourceMesh, 0x88).ToArray();
        Put32(mesh, 0, Tag(0)); Put32(mesh, 4, Tag(2)); Put32(mesh, 8, uint.MaxValue); Put32(mesh, 0x10, Tag(4));
        for (int stage = 0; stage <= 23; stage++) Put16(mesh, 0x28 + stage * 2, checked((ushort)retained.Count(value => value.Stage < stage)));
        model = NativeRows.Append(model, 0x10, MeshClass, 0x88, mesh, out int newMesh);
        model = NativeRows.Append(model, newMesh + 0x18, PartClass, 0x20, partBytes, out _);
        for (int axis = 0; axis < 3; axis++) PutFloat(model, 0x50 + axis * 4, geometry.PositionScale);
        PutFloat(model, 0x60, geometry.PositionOffset.X); PutFloat(model, 0x64, geometry.PositionOffset.Y);
        PutFloat(model, 0x68, geometry.PositionOffset.Z); PutFloat(model, 0x6C, geometry.PositionScale);
        PutFloat(model, 0x70, geometry.UvScale); PutFloat(model, 0x74, geometry.UvScale);
        PutFloat(model, 0x78, geometry.UvOffset.X); PutFloat(model, 0x7C, geometry.UvOffset.Y);
        var additions = new List<ProjectAssetAddition>();
        BufferPair(native.VertexStreams[0].HeaderTag, positions, 0, "positions", false);
        BufferPair(native.VertexStreams[1].HeaderTag, geometry.Attributes, 2, "attributes", false);
        BufferPair(native.IndexHeaderTag, geometry.Indices, 4, "indices", true);
        token.ThrowIfCancellationRequested();
        if (catalog.OverlayRevision != sourceRevision)
            throw new InvalidDataException("Project sources changed while the skin replacement was being prepared.");
        return new(options.EntityTag, options.ModelTag, rig.ResourceTag, materialTag,
            input.Geometry.Positions.Length, input.Geometry.Indices.Length / 3, retained.Select(value => value.Part).ToArray(),
            originalPartCount - retained.Length, additions.AsReadOnly(), new Dictionary<uint, byte[]> { [options.ModelTag] = model }, hashes)
        { ConsumedBones = consumedBones };

        void BufferPair(uint headerTag, byte[] data, int index, string suffix, bool indices)
        {
            var headerEntry = Require(originals, headerTag); var payloadEntry = Require(originals, headerEntry.Reference);
            if (!catalog.ContainsInstalledPackage(headerEntry.PackageId) || !catalog.ContainsInstalledPackage(payloadEntry.PackageId)
                || headerEntry.Type != 32 || headerEntry.SubType != (indices ? 6 : 4)
                || payloadEntry.Type != 40 || payloadEntry.SubType != headerEntry.SubType)
                throw new NotSupportedException("The template must still reference installed vertex/index buffer pairs.");
            byte[] header = Read(headerTag).ToArray(); Read(payloadEntry.TagHash);
            if (indices) { if (header.Length < 16) throw new InvalidDataException("Truncated index header."); header[1] = 1; Put64(header, 8, data.Length); }
            else Put32(header, 0, checked((uint)data.Length));
            additions.Add(new(options.Name + "." + suffix, header,
                new(options.PackageId, Tag(index + 1), headerEntry.TypeInfo, null, options.FirstEntryIndex + index), headerEntry.TagHash));
            additions.Add(new(options.Name + "." + suffix + ".bin", data,
                new(options.PackageId, Tag(index), payloadEntry.TypeInfo, null, options.FirstEntryIndex + index + 1), payloadEntry.TagHash));
        }
    }

    /** Each material variant and retained shadow pass must consume the same encoded skin layout. */
    private static void ValidateMaterial(PackageCatalog catalog, uint tag, string shaderHash, Func<uint, byte[]> read)
    {
        if (Require(catalog, tag).Reference != 0x808071E8)
            throw new InvalidDataException("A retained assignment is not a material.");
        byte[] material = read(tag);
        if (material.Length < 0x400 || I64(material, 0) != material.Length)
            throw new InvalidDataException("A retained material has an invalid header.");
        var headerEntry = Require(catalog, U32(material, 0x48));
        var payloadEntry = Require(catalog, headerEntry.Reference);
        byte[] header = read(headerEntry.TagHash), payload = read(payloadEntry.TagHash);
        if (headerEntry.Type != 33 || headerEntry.SubType != 1 || payloadEntry.Type != 41 || payloadEntry.SubType != 1
            || header.Length < 12 || I64(header, 0) != header.Length || U32(header, 8) != payload.Length
            || Hash(payload) != shaderHash)
            throw new NotSupportedException("A retained material variant no longer uses the checked skinning shader.");
    }

    /** Validate every listed alternative even when initial state selects only one. */
    private static uint[] PartMaterials(byte[] model, int part, byte[] config)
    {
        uint direct = U32(model, part);
        int variant = BinaryPrimitives.ReadInt16LittleEndian(model.AsSpan(part + 4));
        if (variant == -1) return [direct];
        int resource = checked((int)(0x18 + I64(config, 0x18)));
        int[] groups = NativeRows.Rows(config, resource + 0x2D0, 0x808072C4, 12);
        int[] materials = NativeRows.Rows(config, resource + 0x310, 0x80800014, 4);
        if ((uint)variant >= groups.Length) throw new InvalidDataException("The part's external material group is missing.");
        int count = BinaryPrimitives.ReadInt16LittleEndian(config.AsSpan(groups[variant]));
        int first = BinaryPrimitives.ReadInt32LittleEndian(config.AsSpan(groups[variant] + 4));
        if (count <= 0 || first < 0 || first > materials.Length - count)
            throw new InvalidDataException("The part's external material variants leave their table.");
        return materials.AsSpan(first, count).ToArray().Select(offset => U32(config, offset)).ToArray();
    }

    /** The model constructor uploads a prefix of the skeleton, independently of its physics partitions. */
    private static int ConsumedBoneCount(byte[] config, byte[] model, uint modelTag)
    {
        if (config.Length < 32) throw new InvalidDataException("The model component is truncated.");
        int primary = checked((int)(0x10 + I64(config, 0x10)));
        int resource = checked((int)(0x18 + I64(config, 0x18)));
        if (primary < 4 || primary > config.Length - 0xF8 || resource < 4 || resource > config.Length - 0x1E4
            || U32(config, primary - 4) != 0x808072B8 || U32(config, resource - 4) != 0x808072BD
            || U32(config, resource + 0x1DC) != modelTag || U32(config, resource + 0x1E0) != 2)
            throw new NotSupportedException("The model component does not use the checked matrix-palette constructor.");
        uint count = U32(config, primary + 0xF4);
        int authored = BinaryPrimitives.ReadInt32LittleEndian(model.AsSpan(0x40));
        if (authored >= 0) count = Math.Max(count, (uint)authored);
        if (count is 0 or > 256) throw new NotSupportedException("The consumed matrix palette does not fit byte joint indices.");
        return (int)count;
    }

    /** Coupled model and buffer publication is one project transaction after source revalidation. */
    public static IReadOnlyList<AddedProjectAsset> Apply(StudioProject project, PackageCatalog catalog, PreparedCompatibleSkin prepared)
        => project.ApplyTransaction(prepared.Additions, _ => Edits(catalog, prepared), catalog);

    /** The editor checks the same source snapshot before publishing its undoable transaction. */
    public static ProjectAssetEdits Edits(PackageCatalog catalog, PreparedCompatibleSkin prepared)
    {
        foreach (var (tag, expected) in prepared.SourceHashes)
            if (Hash(catalog.Read(tag)) != expected) throw new InvalidDataException("The compatible skin template changed during preparation.");
        return new(prepared.Replacements);
    }

    /** Bone names, parent order and both bind matrices must match the selected installed rig. */
    private static void ValidateBones(IReadOnlyList<CompatibleSkinBone> supplied, SkeletonRig rig)
    {
        if (supplied.Count != rig.Bones.Count) throw new InvalidDataException("Imported and template skeleton counts differ.");
        for (int index = 0; index < supplied.Count; index++)
        {
            var input = supplied[index]; var native = rig.Bones[index];
            if (input.NameHash != native.NameHash || input.ParentIndex != native.ParentIndex
                || !Close(input.BindObject, native.BindObject.Matrix) || !Close(input.InverseBindObject, native.InverseBindObject.Matrix))
                throw new InvalidDataException($"Imported bone {index} differs from the template's package-order hierarchy or bind pose.");
        }
    }

    /** Largest remainders keep normalized byte weights at exactly 255. */
    public static byte[] EncodeInfluences(IReadOnlyList<VertexSkinWeights> vertices, int boneCount)
    {
        if (boneCount is < 1 or > 256) throw new InvalidDataException("This skin profile addresses at most 256 bones.");
        byte[] result = new byte[checked(vertices.Count * 8)];
        for (int vertex = 0; vertex < vertices.Count; vertex++)
        {
            var source = vertices[vertex]; int[] indices = [source.Bone0, source.Bone1, source.Bone2, source.Bone3];
            float[] weights = [source.Weights.X, source.Weights.Y, source.Weights.Z, source.Weights.W];
            double sum = weights.Sum(value => (double)value);
            if (weights.Any(value => !float.IsFinite(value) || value < 0 || value > 1) || Math.Abs(sum - 1) > .00001)
                throw new InvalidDataException("Skin weights must be finite, nonnegative and sum to one.");
            double[] scaled = weights.Select(value => value / sum * 255).ToArray();
            int[] encoded = scaled.Select(value => (int)Math.Floor(value)).ToArray();
            foreach (int lane in Enumerable.Range(0, 4).OrderByDescending(lane => scaled[lane] - encoded[lane]).ThenBy(lane => lane).Take(255 - encoded.Sum())) encoded[lane]++;
            for (int lane = 0; lane < 4; lane++)
            {
                if (weights[lane] > 0 && (uint)indices[lane] >= boneCount) throw new InvalidDataException("A weighted joint is outside the package-order skeleton.");
                result[vertex * 8 + lane] = checked((byte)encoded[lane]);
                result[vertex * 8 + 4 + lane] = weights[lane] > 0 ? checked((byte)indices[lane]) : (byte)0;
            }
        }
        return result;
    }

    private static bool Profile(NativeMeshData native) => native.InputLayoutVerified && native.NativeDeclarationId == 16
        && native.SkinWeightStream is null && native.VertexStreams.Length == 2
        && native.VertexStreams[0].Stride == 16 && native.VertexStreams[1].Stride == 20
        && native.VertexStreams.All(stream => stream.Type == 0)
        && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("BLENDWEIGHT", 0, 0, 8, NativeVertexFormat.R8G8B8A8UNorm),
            new("BLENDINDICES", 0, 0, 12, NativeVertexFormat.R8G8B8A8UInt),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm)
        });
    private static bool Close(Matrix4x4 left, Matrix4x4 right)
    {
        float[] a = [left.M11,left.M12,left.M13,left.M14,left.M21,left.M22,left.M23,left.M24,left.M31,left.M32,left.M33,left.M34,left.M41,left.M42,left.M43,left.M44];
        float[] b = [right.M11,right.M12,right.M13,right.M14,right.M21,right.M22,right.M23,right.M24,right.M31,right.M32,right.M33,right.M34,right.M41,right.M42,right.M43,right.M44];
        return a.Zip(b).All(pair => float.IsFinite(pair.First) && MathF.Abs(pair.First - pair.Second) <= BindTolerance);
    }
    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry) ? entry : throw new InvalidDataException("Skin template entry is missing.");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void PutFloat(byte[] bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
}
