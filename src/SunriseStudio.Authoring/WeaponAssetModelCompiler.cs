using System.Buffers.Binary;
using System.Security.Cryptography;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** One rigid primitive: mesh, material and bone palette index. */
public sealed record WeaponRigidPrimitive(InterchangeMesh Mesh, uint MaterialTag, short PaletteIndex)
{
    public IReadOnlyList<short>? PaletteIndices { get; init; }
}
/** Entity, model and source part offset a rigid weapon model replaces. */
public sealed record WeaponRigidModelOptions(uint EntityTag, uint ModelTag, int SourcePartOffset,
    ushort PackageId, int FirstEntryIndex, string Name, uint SkeletonEntityTag = 0);
/** Compiled rigid weapon model with its shaders and assets. */
public sealed record PreparedWeaponRigidModel(uint ModelTag, uint VertexShaderTag,
    int VertexCount, int TriangleCount, IReadOnlyList<ProjectAssetAddition> Assets);

/** The weapon profile stores one matrix-palette index in each position's fourth short. */
public static class WeaponAssetModelCompiler
{
    private const uint ModelClass = 0x808073A5, MeshClass = 0x80807378, PartClass = 0x8080737E;
    private const string ShaderHash = "4D3706BDD7372EEDF5D57D289C1956F2C833DBBC2A2A7E2A0D2E9E4852B534B6";
    private const int EntryCount = 7, Declaration = 139, PositionStride = 8, AttributeStride = 24;

    /** Primitive coordinates are package Z-up space and palette indices belong to the donor. */
    public static PreparedWeaponRigidModel Prepare(PackageCatalog catalog,
        IReadOnlyList<WeaponRigidPrimitive> primitives, WeaponRigidModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (primitives.Count is < 1 or > short.MaxValue)
            throw new InvalidDataException("A weapon model needs one or more triangle primitives.");
        if (!PackageWriter.IsNewPackageId(options.PackageId) || catalog.ContainsInstalledPackage(options.PackageId)
            || options.FirstEntryIndex < 0 || options.FirstEntryIndex > 8192 - EntryCount)
            throw new InvalidDataException("Weapon geometry needs seven entries in an unused package.");
        var modelEntry = Require(catalog, options.ModelTag);
        if (modelEntry.Reference != ModelClass)
            throw new InvalidDataException("Weapon template is not a native entity model.");
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(options.EntityTag);
        var chosen = scene.Instances.FirstOrDefault(instance => instance.Mesh.TagHash == options.ModelTag
            && instance.Mesh.PartIndex == options.SourcePartOffset)
            ?? throw new InvalidDataException("Weapon template part is not linked to its entity.");
        if (chosen.InitiallyVisible == false || chosen.Mesh.LodLevel != 0
            || chosen.Mesh.Native is not { } native || !Profile(native)
            || Convert.ToHexString(SHA256.HashData(catalog.Read(native.VertexShaderDataTag))) != ShaderHash)
            throw new NotSupportedException("Weapon geometry needs the checked declaration-139 matrix profile.");
        byte[] model = catalog.Read(options.ModelTag).ToArray();
        int[] meshRows = NativeRows.Rows(model, 0x10, MeshClass, 0x88);
        int sourceMesh = meshRows.Single(row => NativeRows.Rows(model, row + 0x18, PartClass, 0x20)
            .Contains(options.SourcePartOffset));
        var paletteIndices = new HashSet<short>();
        foreach (var instance in scene.Instances.Where(instance => instance.Mesh.TagHash == options.ModelTag))
        {
            var source = instance.Mesh.Native?.VertexStreams[0];
            if (source is null || source.Stride != PositionStride) continue;
            foreach (uint vertex in instance.Mesh.SourceVertexIndices)
                paletteIndices.Add(BinaryPrimitives.ReadInt16LittleEndian(source.Data.AsSpan(checked((int)vertex * PositionStride + 6))));
        }
        int paletteCount = options.SkeletonEntityTag == 0 ? 0
            : new SkeletonReader(catalog).Read(options.SkeletonEntityTag).Bones.Count;
        foreach (var primitive in primitives)
        {
            if (primitive.PaletteIndices is not null && primitive.PaletteIndices.Count != primitive.Mesh.Positions.Length)
                throw new InvalidDataException("Every weapon vertex needs one palette index.");
            foreach (short index in primitive.PaletteIndices ?? [primitive.PaletteIndex])
                if (index < 0 || (paletteCount == 0 ? !paletteIndices.Contains(index) : index >= paletteCount))
                    throw new NotSupportedException("A weapon vertex names a bone outside its native palette.");
        }
        foreach (var primitive in primitives) _ = ObjInterchange.ToMesh(primitive.Mesh);
        var positions = primitives.SelectMany(part => part.Mesh.Positions).ToArray();
        var normals = primitives.SelectMany(part => part.Mesh.Normals).ToArray();
        var uvs = primitives.SelectMany(part => part.Mesh.TexCoords).ToArray();
        var indices = new List<uint>();
        uint baseVertex = 0;
        foreach (var primitive in primitives)
        {
            indices.AddRange(primitive.Mesh.Indices.Select(index => checked(index + baseVertex)));
            baseVertex = checked(baseVertex + (uint)primitive.Mesh.Positions.Length);
        }
        var encoded = MeshAssetImporter.EncodeGeometry(new(options.Name, positions, normals, uvs, indices.ToArray()));
        byte[] attributes = new byte[checked(positions.Length * AttributeStride)];
        int firstVertex = 0;
        foreach (var primitive in primitives)
            for (int local = 0; local < primitive.Mesh.Positions.Length; local++)
            {
                int vertex = firstVertex++;
                BinaryPrimitives.WriteInt16LittleEndian(encoded.Positions.AsSpan(vertex * PositionStride + 6),
                    primitive.PaletteIndices?[local] ?? primitive.PaletteIndex);
                encoded.Attributes.AsSpan(vertex * 20, 20).CopyTo(attributes.AsSpan(vertex * AttributeStride));
                BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(vertex * AttributeStride + 20), BitConverter.HalfToUInt16Bits((Half)1));
                BinaryPrimitives.WriteUInt16LittleEndian(attributes.AsSpan(vertex * AttributeStride + 22), BitConverter.HalfToUInt16Bits((Half)1));
            }
        uint Tag(int index) => checked(0x80800000u + ((uint)options.PackageId << 13)
            + (uint)(options.FirstEntryIndex + index));
        byte[] parts = new byte[checked(primitives.Count * 0x20)];
        uint firstIndex = 0;
        for (int index = 0; index < primitives.Count; index++)
        {
            int at = index * 0x20;
            model.AsSpan(options.SourcePartOffset, 0x20).CopyTo(parts.AsSpan(at));
            Put32(parts, at, primitives[index].MaterialTag);
            Put16(parts, at + 4, ushort.MaxValue);
            Put16(parts, at + 6, 3);
            Put32(parts, at + 8, firstIndex);
            Put32(parts, at + 12, checked((uint)primitives[index].Mesh.Indices.Length));
            firstIndex = checked(firstIndex + (uint)primitives[index].Mesh.Indices.Length);
        }
        byte[] mesh = model.AsSpan(sourceMesh, 0x88).ToArray();
        Put32(mesh, 0, Tag(1)); Put32(mesh, 4, Tag(3)); Put32(mesh, 8, uint.MaxValue);
        Put32(mesh, 0x10, Tag(5));
        for (int stage = 0; stage <= 23; stage++)
            Put16(mesh, 0x28 + stage * 2, stage == 0 ? (ushort)0 : checked((ushort)primitives.Count));
        model = NativeRows.Append(model, 0x10, MeshClass, 0x88, mesh, out int meshAt);
        model = NativeRows.Append(model, meshAt + 0x18, PartClass, 0x20, parts, out _);
        if (paletteCount > 0) Put32(model, 0x40, checked((uint)paletteCount));
        for (int axis = 0; axis < 3; axis++) PutFloat(model, 0x50 + axis * 4, encoded.PositionScale);
        PutFloat(model, 0x60, encoded.PositionOffset.X); PutFloat(model, 0x64, encoded.PositionOffset.Y);
        PutFloat(model, 0x68, encoded.PositionOffset.Z); PutFloat(model, 0x6C, encoded.PositionScale);
        PutFloat(model, 0x70, encoded.UvScale); PutFloat(model, 0x74, encoded.UvScale);
        PutFloat(model, 0x78, encoded.UvOffset.X); PutFloat(model, 0x7C, encoded.UvOffset.Y);
        var assets = new List<ProjectAssetAddition>
        {
            new(options.Name + ".model", model,
                new(options.PackageId, modelEntry.Reference, modelEntry.TypeInfo, options.Name, options.FirstEntryIndex), options.ModelTag)
        };
        Pair(native.VertexStreams[0].HeaderTag, encoded.Positions, 1, "positions", false);
        Pair(native.VertexStreams[1].HeaderTag, attributes, 3, "attributes", false);
        Pair(native.IndexHeaderTag, encoded.Indices, 5, "indices", true);
        return new(Tag(0), native.VertexShaderTag, positions.Length, indices.Count / 3, assets);

        void Pair(uint headerTag, byte[] data, int index, string name, bool indexBuffer)
        {
            var headerEntry = Require(catalog, headerTag);
            var payloadEntry = Require(catalog, headerEntry.Reference);
            if (headerEntry.Type != 32 || headerEntry.SubType != (indexBuffer ? 6 : 4)
                || payloadEntry.Type != 40 || payloadEntry.SubType != headerEntry.SubType)
                throw new InvalidDataException("Weapon buffer source types changed.");
            byte[] header = catalog.Read(headerTag).ToArray();
            if (indexBuffer) { header[1] = 1; Put64(header, 8, data.Length); }
            else Put32(header, 0, checked((uint)data.Length));
            assets.Add(new(options.Name + "." + name, header,
                new(options.PackageId, Tag(index + 1), headerEntry.TypeInfo, null, options.FirstEntryIndex + index), headerTag));
            assets.Add(new(options.Name + "." + name + ".bin", data,
                new(options.PackageId, Tag(index), payloadEntry.TypeInfo, null, options.FirstEntryIndex + index + 1)));
        }
    }

    private static bool Profile(NativeMeshData native) => native.Kind == NativeMeshKind.Entity
        && native.InputLayoutVerified && native.NativeDeclarationId == Declaration && native.RenderStage == 0
        && native.VertexStreams.Length == 2 && native.VertexStreams[0].Stride == PositionStride
        && native.VertexStreams[1].Stride == AttributeStride && native.VertexStreams.All(stream => stream.Type == 0)
        && native.SkinWeightStream is null && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 2, 1, 20, NativeVertexFormat.R16G16Float)
        });

    private static PackageEntry Require(PackageCatalog catalog, uint tag) => catalog.TryGetEntry(tag, out var entry)
        ? entry : throw new InvalidDataException("Weapon geometry source is missing.");
    private static void Put16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);
    private static void PutFloat(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
}
