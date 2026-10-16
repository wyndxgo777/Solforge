using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

/** Result of a mesh import: the model asset, tags, counts and every added asset. */
public sealed record MeshImportResult(AddedProjectAsset ModelAsset, uint ModelTag, uint TemplateTag,
    int VertexCount, int TriangleCount, IReadOnlyList<AddedProjectAsset> Assets);
/** Position, attribute and index buffers with the offsets and scales that decode them. */
public sealed record EncodedStaticGeometry(byte[] Positions, byte[] Attributes, byte[] Indices,
    Vector3 PositionOffset, float PositionScale, Vector2 UvOffset, float UvScale);
/** Compiled static model ready to add to a project. */
public sealed record PreparedStaticModel(uint ModelTag, uint TemplateTag, int VertexCount, int TriangleCount,
    IReadOnlyList<ProjectAssetAddition> Assets);

/** Static model headers stay as deltas; all authored geometry payloads contain user mesh data. */
public static class MeshAssetImporter
{
    // This shader reads XYZ positions and multiplies the normal-tangent cross product by tangent W.
    private const string StaticShader = "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66";
    private const uint PreferredTemplate = 0x80B9EEE1;
    private const int EntryCount = 8;

    /** Reads a mesh file, compiles it and adds the assets to the project at the next free entry index. */
    public static MeshImportResult ImportFile(StudioProject project, PackageCatalog catalog,
        string path, string name, ushort packageId, uint? templateTag = null,
        CancellationToken cancellationToken = default)
    {
        int first = project.AddedAssets.Where(x => x.Native?.PackageId == packageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = PrepareFile(catalog, path, name, packageId, first, templateTag, cancellationToken);
        var additions = project.AddAssets(prepared.Assets, catalog);
        return new(additions[0], prepared.ModelTag, prepared.TemplateTag, prepared.VertexCount, prepared.TriangleCount, additions);
    }

    /** Preparation allocates native indices without publishing project files. */
    public static PreparedStaticModel PrepareFile(PackageCatalog catalog, string path, string name,
        ushort packageId, int firstIndex, uint? templateTag = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var meshes = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".obj" => ObjInterchange.Read(path),
            ".gltf" or ".glb" => GltfInterchange.Read(path),
            _ => throw new NotSupportedException("Native mesh import accepts OBJ, glTF and GLB.")
        };
        return PrepareParts(catalog, [Combine(meshes, name)], null, name, packageId,
            firstIndex, templateTag, cancellationToken);
    }

    /** One authored triangle-list part replaces the template's referenced arrays. */
    public static MeshImportResult ImportMesh(StudioProject project, PackageCatalog catalog,
        InterchangeMesh mesh, string name, ushort packageId, uint? templateTag = null,
        CancellationToken cancellationToken = default)
    {
        int first = project.AddedAssets.Where(x => x.Native?.PackageId == packageId)
            .Select(x => x.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
        var prepared = PrepareParts(catalog, [mesh], null, name, packageId, first, templateTag, cancellationToken);
        var additions = project.AddAssets(prepared.Assets, catalog);
        return new(additions[0], prepared.ModelTag, prepared.TemplateTag, prepared.VertexCount, prepared.TriangleCount, additions);
    }

    /** Each primitive retains a material assignment while sharing one authored geometry buffer group. */
    public static PreparedStaticModel PrepareParts(PackageCatalog catalog, IReadOnlyList<InterchangeMesh> meshes,
        IReadOnlyList<uint>? materialTags, string name, ushort packageId, int first,
        uint? templateTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (meshes.Count is < 1 or > short.MaxValue || materialTags is not null && materialTags.Count != meshes.Count)
            throw new InvalidDataException("Static primitives need one material assignment each.");
        foreach (var primitive in meshes) ValidateMesh(primitive);
        var mesh = Combine(meshes, name);
        ValidateMesh(mesh);
        if (!PackageWriter.IsNewPackageId(packageId) || catalog.ContainsInstalledPackage(packageId))
            throw new InvalidDataException($"Choose an unused package id from {PackageWriter.NewPackageIdRange}.");
        if (first < 0 || first > 8192 - EntryCount) throw new InvalidDataException("The package has no room for a mesh.");
        cancellationToken.ThrowIfCancellationRequested();
        using var originals = PackageCatalog.Open(catalog.GameRoot);
        originals.ReadOverride = catalog.ReadOriginal;
        var template = FindTemplate(originals, templateTag, cancellationToken);
        uint Tag(int index) => checked(0x80800000U + ((uint)packageId << 13) + (uint)(first + index));
        var encoded = EncodeGeometry(mesh, cancellationToken);
        byte[] model = (byte[])template.Model.Clone(), resource = (byte[])template.Resource.Clone();
        U(model, 8, Tag(1));
        WriteVector(model, 0x60, encoded.PositionOffset);
        F(model, 0x6C, encoded.PositionScale);
        F(model, 0x70, encoded.UvScale); F(model, 0x74, encoded.UvScale);
        F(model, 0x78, encoded.UvOffset.X); F(model, 0x7C, encoded.UvOffset.Y);
        var material = new byte[checked(4 * meshes.Count)];
        var assignments = new byte[checked(8 * meshes.Count)];
        var parts = new byte[checked(12 * meshes.Count)];
        uint firstIndex = 0;
        for (int index = 0; index < meshes.Count; index++)
        {
            U(material, index * 4, materialTags?[index] ?? template.Mesh.MaterialTag);
            template.Assignment.CopyTo(assignments, index * 8);
            S(assignments, index * 8, checked((ushort)index));
            template.Part.CopyTo(parts, index * 12);
            U(parts, index * 12, firstIndex); U(parts, index * 12 + 4, checked((uint)meshes[index].Indices.Length));
            S(parts, index * 12 + 8, 0); parts[index * 12 + 10] = 0; parts[index * 12 + 11] = 3;
            firstIndex = checked(firstIndex + (uint)meshes[index].Indices.Length);
        }
        model = AppendArray(model, 0x10, 0x80800014, 4, material);
        resource = AppendArray(resource, 8, 0x8080719B, 8, assignments);
        resource = AppendArray(resource, 0x18, 0x8080719A, 12, parts);
        var group = (byte[])template.Group.Clone();
        U(group, 0, Tag(6)); U(group, 4, Tag(2)); U(group, 8, Tag(4)); U(group, 12, 0);
        resource = AppendArray(resource, 0x28, 0x80807199, 16, group);
        var positionsHeader = (byte[])template.PositionHeader.Clone();
        var attributesHeader = (byte[])template.AttributeHeader.Clone();
        var indexHeader = (byte[])template.IndexHeader.Clone();
        U(positionsHeader, 0, checked((uint)encoded.Positions.Length));
        U(attributesHeader, 0, checked((uint)encoded.Attributes.Length));
        indexHeader[1] = 1;
        Q(indexHeader, 8, encoded.Indices.LongLength);
        ProjectAssetAddition Derived(int index, string suffix, byte[] bytes, PackageEntry source,
            uint reference, bool named = false) => new(name + suffix, bytes,
                new(packageId, reference, source.TypeInfo, named ? name : null, first + index), source.TagHash);
        ProjectAssetAddition Owned(int index, int headerIndex, string suffix, byte[] bytes, PackageEntry source) =>
            new(name + suffix, bytes, new(packageId, Tag(headerIndex), source.TypeInfo, null, first + index));
        cancellationToken.ThrowIfCancellationRequested();
        ProjectAssetAddition[] additions = [
            Derived(0, ".model", model, template.ModelEntry, template.ModelEntry.Reference, true),
            Derived(1, ".mesh", resource, template.ResourceEntry, template.ResourceEntry.Reference),
            Derived(2, ".positions", positionsHeader, template.PositionEntry, Tag(3)),
            Owned(3, 2, ".positions.bin", encoded.Positions, template.PositionPayload),
            Derived(4, ".attributes", attributesHeader, template.AttributeEntry, Tag(5)),
            Owned(5, 4, ".attributes.bin", encoded.Attributes, template.AttributePayload),
            Derived(6, ".indices", indexHeader, template.IndexEntry, Tag(7)),
            Owned(7, 6, ".indices.bin", encoded.Indices, template.IndexPayload)
        ];
        return new(Tag(0), template.ModelEntry.TagHash, mesh.Positions.Length, mesh.Indices.Length / 3, additions);
    }

    private sealed record Template(PackageEntry ModelEntry, byte[] Model,
        PackageEntry ResourceEntry, byte[] Resource, MeshData Mesh,
        byte[] Assignment, byte[] Part, byte[] Group,
        PackageEntry PositionEntry, byte[] PositionHeader, PackageEntry PositionPayload,
        PackageEntry AttributeEntry, byte[] AttributeHeader, PackageEntry AttributePayload,
        PackageEntry IndexEntry, byte[] IndexHeader, PackageEntry IndexPayload);

    /** A verified declaration and exact shader profile gate the supported attribute encoding. */
    private static Template FindTemplate(PackageCatalog catalog, uint? explicitTag, CancellationToken token)
    {
        IEnumerable<PackageEntry> candidates = explicitTag is uint tag
            ? catalog.TryGetEntry(tag, out var found) ? [found] : []
            : catalog.Entries.Where(x => x.Reference == 0x808071A7)
                .OrderBy(x => x.TagHash == PreferredTemplate ? 0 : 1).ThenBy(x => x.TagHash);
        var loader = new AssetSceneLoader(catalog);
        Dictionary<uint, bool> shaders = [];
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Reference != 0x808071A7 || candidate.Type is not (8 or 16)) continue;
            SceneDocument scene;
            try { scene = loader.LoadMesh(candidate.TagHash, token); }
            catch (Exception error) when (explicitTag is null && error is InvalidDataException
                or NotSupportedException or ArgumentException or OverflowException or IndexOutOfRangeException)
            { continue; }
            foreach (var mesh in scene.Instances.Select(x => x.Mesh))
            {
                var native = mesh.Native;
                if (native is null || !SupportedLayout(native) || mesh.LodLevel != 0) continue;
                if (!shaders.TryGetValue(native.VertexShaderDataTag, out bool supportedShader))
                {
                    supportedShader = native.VertexShaderDataTag != 0
                        && Convert.ToHexString(SHA256.HashData(catalog.Read(native.VertexShaderDataTag))) == StaticShader;
                    shaders[native.VertexShaderDataTag] = supportedShader;
                }
                if (!supportedShader) continue;
                try
                {
                    byte[] model = catalog.Read(candidate.TagHash);
                    if (model.Length < 128 || Q(model, 0) != model.LongLength)
                        throw new InvalidDataException("Invalid static model header.");
                    var resourceEntry = Require(catalog, U(model, 8), 0x80807194);
                    byte[] resource = catalog.Read(resourceEntry.TagHash);
                    if (resource.Length < 64 || Q(resource, 0) != resource.LongLength)
                        throw new InvalidDataException("Invalid static resource header.");
                    var materials = Rows(model, 0x10, 0x80800014, 4);
                    var assignments = Rows(resource, 8, 0x8080719B, 8);
                    var parts = Rows(resource, 0x18, 0x8080719A, 12);
                    var groups = Rows(resource, 0x28, 0x80807199, 16);
                    if (mesh.PartIndex < 0 || mesh.PartIndex >= parts.Length) continue;
                    int part = parts[mesh.PartIndex], groupIndex = S(resource, part + 8);
                    if (groupIndex >= groups.Length) throw new InvalidDataException("Invalid buffer group index.");
                    int group = groups[groupIndex];
                    int assignment = Enumerable.Range(0, Math.Min(materials.Length, assignments.Length))
                        .First(i => S(resource, assignments[i]) == mesh.PartIndex
                            && S(resource, assignments[i] + 2) == native.RenderStage
                            && U(model, materials[i]) == mesh.MaterialTag);
                    var position = Buffer(catalog, U(resource, group + 4), 4, 8);
                    var attribute = Buffer(catalog, U(resource, group + 8), 4, 20);
                    var index = Buffer(catalog, U(resource, group), 6, 0);
                    return new(candidate, model, resourceEntry, resource, mesh,
                        resource.AsSpan(assignments[assignment], 8).ToArray(), resource.AsSpan(part, 12).ToArray(),
                        resource.AsSpan(group, 16).ToArray(), position.Entry, position.Header, position.Payload,
                        attribute.Entry, attribute.Header, attribute.Payload, index.Entry, index.Header, index.Payload);
                }
                catch (Exception error) when (explicitTag is null && error is InvalidDataException or InvalidOperationException)
                { }
            }
        }
        throw new NotSupportedException("No supported static template with declaration 123 and the checked shader was found.");
    }

    private static bool SupportedLayout(NativeMeshData native) =>
        native.Kind == NativeMeshKind.Static && native.InputLayoutVerified
        && native.NativeDeclarationId == 123 && native.RenderStage == 0
        && native.VertexStreams.Length == 2 && native.VertexStreams[0].Stride == 8
        && native.VertexStreams[1].Stride == 20 && native.VertexStreams.All(x => x.Type == 0)
        && native.InputElements.SequenceEqual(new NativeInputElement[]
        {
            new("POSITION", 0, 0, 0, NativeVertexFormat.R16G16B16A16SNorm),
            new("TEXCOORD", 0, 1, 0, NativeVertexFormat.R16G16SNorm),
            new("NORMAL", 0, 1, 4, NativeVertexFormat.R16G16B16A16SNorm),
            new("TANGENT", 0, 1, 12, NativeVertexFormat.R16G16B16A16SNorm)
        });

    private static PackageEntry Require(PackageCatalog catalog, uint tag, uint reference)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != reference)
            throw new InvalidDataException("Static dependency has an unexpected class.");
        return entry;
    }

    /** Buffer headers retain native type words while their paired payload lengths change. */
    private static (PackageEntry Entry, byte[] Header, PackageEntry Payload) Buffer(
        PackageCatalog catalog, uint tag, int subtype, int stride)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type != 32 || entry.SubType != subtype
            || !catalog.TryGetEntry(entry.Reference, out var payload) || payload.Type != 40 || payload.SubType != subtype)
            throw new InvalidDataException("Static buffer header and payload types disagree.");
        byte[] header = catalog.Read(tag);
        if (subtype == 4 && (header.Length < 12 || U(header, 0) != payload.Size
            || S(header, 4) != stride || U(header, 8) != 0xDEADBEEF)
            || subtype == 6 && (header.Length < 16 || Q(header, 8) != payload.Size))
            throw new InvalidDataException("Static buffer header has an unsupported layout.");
        return (entry, header, payload);
    }

    /** Uniform position and UV transforms keep every encoded lane inside signed normalized range. */
    public static EncodedStaticGeometry EncodeGeometry(InterchangeMesh mesh,
        CancellationToken token = default)
    {
        ValidateMesh(mesh);
        var minimum = mesh.Positions.Aggregate(Vector3.Min);
        var maximum = mesh.Positions.Aggregate(Vector3.Max);
        var minUv = mesh.TexCoords.Aggregate(Vector2.Min);
        var maxUv = mesh.TexCoords.Aggregate(Vector2.Max);
        Vector3 offset = new(Mid(minimum.X, maximum.X), Mid(minimum.Y, maximum.Y), Mid(minimum.Z, maximum.Z));
        float scale = Math.Max(1e-6f, Math.Max(HalfRange(minimum.X, maximum.X),
            Math.Max(HalfRange(minimum.Y, maximum.Y), HalfRange(minimum.Z, maximum.Z))));
        Vector2 uvOffset = new(Mid(minUv.X, maxUv.X), Mid(minUv.Y, maxUv.Y));
        float uvScale = Math.Max(1e-6f, Math.Max(HalfRange(minUv.X, maxUv.X), HalfRange(minUv.Y, maxUv.Y)));
        var tangents = new Vector3[mesh.Positions.Length];
        var bitangents = new Vector3[mesh.Positions.Length];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            uint a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            Vector3 edge1 = mesh.Positions[b] - mesh.Positions[a], edge2 = mesh.Positions[c] - mesh.Positions[a];
            Vector2 uv1 = mesh.TexCoords[b] - mesh.TexCoords[a], uv2 = mesh.TexCoords[c] - mesh.TexCoords[a];
            float determinant = uv1.X * uv2.Y - uv1.Y * uv2.X;
            if (MathF.Abs(determinant) < 1e-12f) continue;
            Vector3 tangent = (edge1 * uv2.Y - edge2 * uv1.Y) / determinant;
            Vector3 bitangent = (edge2 * uv1.X - edge1 * uv2.X) / determinant;
            if (!Finite(tangent) || !Finite(bitangent)) throw new InvalidDataException("UV derivatives exceed finite range.");
            tangents[a] += tangent; tangents[b] += tangent; tangents[c] += tangent;
            bitangents[a] += bitangent; bitangents[b] += bitangent; bitangents[c] += bitangent;
        }
        var positions = new byte[checked(mesh.Positions.Length * 8)];
        var attributes = new byte[checked(mesh.Positions.Length * 20)];
        var indices = new byte[checked(mesh.Indices.Length * 4)];
        for (int i = 0; i < mesh.Positions.Length; i++)
        {
            if ((i & 0xFFF) == 0) token.ThrowIfCancellationRequested();
            Vector3 position = (mesh.Positions[i] - offset) / scale;
            Vector2 uv = (mesh.TexCoords[i] - uvOffset) / uvScale;
            Vector3 normal = Normalize(mesh.Normals[i], Vector3.UnitZ);
            Vector3 tangent = tangents[i] - normal * Vector3.Dot(normal, tangents[i]);
            tangent = Normalize(tangent, Normalize(Vector3.Cross(MathF.Abs(normal.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY, normal), Vector3.UnitX));
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent), bitangents[i]) < 0 ? -1 : 1;
            WriteSnorm3(positions, i * 8, position);
            WriteSnorm(attributes, i * 20, uv.X); WriteSnorm(attributes, i * 20 + 2, uv.Y);
            WriteSnorm3(attributes, i * 20 + 4, normal);
            WriteSnorm3(attributes, i * 20 + 12, tangent);
            WriteSnorm(attributes, i * 20 + 18, handedness);
        }
        for (int i = 0; i < mesh.Indices.Length; i++) U(indices, i * 4, mesh.Indices[i]);
        return new(positions, attributes, indices, offset, scale, uvOffset, uvScale);
    }

    /** Primitives keep their vertex and triangle order when joined into shared buffers. */
    private static InterchangeMesh Combine(IReadOnlyList<InterchangeMesh> meshes, string name)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uv = [];
        List<uint> indices = [];
        foreach (var mesh in meshes)
        {
            ValidateMesh(mesh);
            uint start = checked((uint)positions.Count);
            positions.AddRange(mesh.Positions); normals.AddRange(mesh.Normals); uv.AddRange(mesh.TexCoords);
            indices.AddRange(mesh.Indices.Select(x => checked(start + x)));
        }
        return new(name, positions.ToArray(), normals.ToArray(), uv.ToArray(), indices.ToArray());
    }

    private static void ValidateMesh(InterchangeMesh mesh)
    {
        if (mesh.Positions.Length is < 3 or > 4_000_000 || mesh.Indices.Length is < 3 or > 24_000_000
            || mesh.Indices.Length % 3 != 0 || mesh.Positions.Length != mesh.Normals.Length
            || mesh.Positions.Length != mesh.TexCoords.Length || mesh.Indices.Any(x => x >= mesh.Positions.Length)
            || mesh.Positions.Any(x => !Finite(x)) || mesh.Normals.Any(x => !Finite(x))
            || mesh.TexCoords.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Y)))
            throw new InvalidDataException("Mesh attributes, topology or dimensions are invalid.");
    }

    /** Appended arrays leave all unknown original bytes available through the base delta. */
    private static byte[] AppendArray(byte[] source, int field, uint type, int stride, byte[] rows)
    {
        if (rows.Length % stride != 0 || field < 8 || field > source.Length - 16)
            throw new InvalidDataException("Invalid native array replacement.");
        return NativeRows.Append(source, field, type, stride, rows, out _);
    }

    private static int[] Rows(byte[] bytes, int field, uint type, int stride)
    {
        if (field < 0 || field > bytes.Length - 16) throw new InvalidDataException("Truncated native array.");
        long count = Q(bytes, field), relative = Q(bytes, field + 8);
        if (count == 0 && relative == 0) return [];
        long header = checked(field + 8 + relative);
        if (count is < 0 or > 1_000_000 || header < 4 || header > bytes.Length - 16
            || U(bytes, (int)header - 4) != 0x80809FBD || Q(bytes, (int)header) != count
            || U(bytes, (int)header + 8) != type || U(bytes, (int)header + 12) != 0
            || count * stride > bytes.Length - header - 16)
            throw new InvalidDataException("Invalid typed native array.");
        return Enumerable.Range(0, (int)count).Select(i => checked((int)header + 16 + i * stride)).ToArray();
    }

    private static float Mid(float a, float b) => checked((float)(((double)a + b) * .5));
    private static float HalfRange(float a, float b) => checked((float)(((double)b - a) * .5));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static Vector3 Normalize(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-12f
        && float.IsFinite(value.LengthSquared()) ? Vector3.Normalize(value) : fallback;
    private static void WriteSnorm(byte[] bytes, int offset, float value)
    {
        if (!float.IsFinite(value) || value is < -1.00001f or > 1.00001f)
            throw new InvalidDataException("Encoded mesh attribute is outside signed normalized range.");
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset), (short)MathF.Round(Math.Clamp(value, -1, 1) * 32767));
    }
    private static void WriteSnorm3(byte[] bytes, int offset, Vector3 value)
    { WriteSnorm(bytes, offset, value.X); WriteSnorm(bytes, offset + 2, value.Y); WriteSnorm(bytes, offset + 4, value.Z); }
    private static void WriteVector(byte[] bytes, int offset, Vector3 value)
    { F(bytes, offset, value.X); F(bytes, offset + 4, value.Y); F(bytes, offset + 8, value.Z); }
    private static uint U(byte[] bytes, int p) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p));
    private static void U(byte[] bytes, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), value);
    private static ushort S(byte[] bytes, int p) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p));
    private static void S(byte[] bytes, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), value);
    private static long Q(byte[] bytes, int p) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(p));
    private static void Q(byte[] bytes, int p, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(p), value);
    private static void F(byte[] bytes, int p, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(p), value);
}
