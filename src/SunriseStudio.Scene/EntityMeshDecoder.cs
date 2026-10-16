using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * Entity-mesh part of AssetSceneLoader: decodes entity models, their material variants and animation
 * clip names.
 */
public sealed partial class AssetSceneLoader
{
    private readonly Dictionary<(uint Model, uint Config), List<(uint[] Materials, MeshData[] Meshes)>> variantMeshes = [];
    private sealed record InitialModelState(uint[] Materials, EntityInitialState Initial, bool MaterialsKnown);
    private readonly Dictionary<(uint Entity, uint Config, uint Source, int Offset, uint Variant, string Pairs), InitialModelState>
        initialMaterials = [];
    private readonly HashSet<EntityMaterialWarning> initialWarnings = [];

    /**
     * Entity geometry is a bind-pose preview; skinning is not evaluated.
     * @param external A part with group index v uses external[v] before its direct material.
     */
    private MeshData[] EntityMesh(uint tag, uint[]? external = null, uint configTag = 0)
    {
        if (external is { Length: > 0 })
        {
            if (!variantMeshes.TryGetValue((tag, configTag), out var variants))
                variantMeshes[(tag, configTag)] = variants = [];
            foreach (var variant in variants)
                if (variant.Materials.AsSpan().SequenceEqual(external)) return variant.Meshes;
            var decoded = DecodeEntityMesh(tag, external);
            variants.Add((external.ToArray(), decoded));
            return decoded;
        }
        if (meshes.TryGetValue(tag, out var cached)) return cached;
        return meshes[tag] = DecodeEntityMesh(tag, null);
    }

    /**
     * Clip label shared by the Animations tab and bundle exports.
     * @param catalog Supplies the package name.
     * @param clipTag Clip to name.
     * @param slot Clip index in its animation definition; used when no name is known.
     * @param names Caller's tag names; asked before the package name.
     * @param recoveredName Exact hash match from this definition's referenced clip.
     * @return The first name found, else "Clip N".
     */
    public static string ClipName(PackageCatalog catalog, uint clipTag, int slot,
        Func<uint, string?>? names = null, string? recoveredName = null) =>
        names?.Invoke(clipTag) ?? catalog.GetName(clipTag) ?? recoveredName ?? $"Clip {slot}";

    /** Model and cloth resources share this map; each row yields its first material only. */
    private static uint[] ExternalMaterials(TagData config, int resource)
    {
        if (!config.Has(resource, 0x320)) return [];
        try
        {
            var materials = config.Rows(resource + 0x310, 0x80800014, 4).Select(config.U32).ToArray();
            return config.Rows(resource + 0x2D0, 0x808072C4, 12).Select(row =>
            {
                int count = config.I16(row), start = config.I32(row + 4);
                return count > 0 && start >= 0 && start < materials.Length ? materials[start] : uint.MaxValue;
            }).ToArray();
        }
        catch (InvalidDataException) { return []; }
    }

    /** Each spawn row may supply different initial filters for a shared model component. */
    private InitialModelState InitialMaterials(uint entity, uint configTag, TagData config, int resource,
        SpawnInput? spawn, uint variant, IReadOnlyList<(uint Key, uint Value)> pairs, IReadOnlyDictionary<uint, uint> picks)
    {
        var key = (entity, configTag, spawn?.Tag ?? 0, spawn?.Offset ?? -1, variant,
            string.Join(",", pairs.Concat(picks.Select(pick => (pick.Key, pick.Value))).Select(pair => $"{pair.Key:X8}={pair.Value:X8}")));
        if (initialMaterials.TryGetValue(key, out var cached)) return cached;
        uint[] materials;
        string? unresolved;
        EntityInitialState initial = new(null, null);
        try
        {
            initial = new EntityInitialStateReader(catalog) { Reads = scene.ReadTagSet }
                .Read(entity, configTag, key.Item3, key.Item4, variant, pairs, picks);
            var selected = EntityMaterialSelector.Read(config.Bytes, resource, initial.EnabledParts, initial.Unresolved);
            materials = selected.Materials;
            unresolved = selected.Unresolved ?? initial.Unresolved;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException
            or OverflowException or IndexOutOfRangeException or KeyNotFoundException)
        {
            materials = ExternalMaterials(config, resource);
            unresolved = error.Message;
        }
        if (unresolved is not null) InitialWarning(entity, configTag, spawn, unresolved);
        return initialMaterials[key] = new(materials, initial, unresolved is null);
    }

    private void InitialWarning(uint entity, uint config, SpawnInput? spawn, string reason)
    {
        string message = $"Model preview for entity 0x{entity:X8}, component 0x{config:X8} uses an authored fallback: {reason}";
        AddDiagnostic(message);
        var warning = new EntityMaterialWarning(entity, config, reason, spawn?.Tag ?? 0, spawn?.Offset ?? -1);
        if (initialWarnings.Add(warning)) scene.MaterialWarnings.Add(warning);
    }

    private MeshData[] DecodeEntityMesh(uint tag, uint[]? external)
    {
        var data = Read(tag);
        var result = new List<MeshData>();
        var scale = data.V3(0x50);
        var translation = data.V3(0x60);
        foreach (var mesh in data.Rows(0x10, 0x80807378, 0x88))
        {
            var raw = Buffer(data.U32(mesh), 4, out var header);
            var stride = header.U16(4);
            // The authored declaration names the position format; without one, stride 48 holds floats.
            var declared = DeclaredPositionFormat(data, mesh);
            bool floats = declared is null ? stride == 48
                : declared is NativeVertexFormat.R32G32B32A32Float or NativeVertexFormat.R32G32B32Float;
            if (declared is { } format && !floats && format != NativeVertexFormat.R16G16B16A16SNorm)
                throw new InvalidDataException($"Entity position format {format} is not supported.");
            if (declared is null && stride is not (8 or 12 or 16 or 20 or 24 or 28 or 32 or 48)
                || stride < (floats ? 12 : 8) || header.U32(0) > raw.Length)
                throw new InvalidDataException($"Entity position stride {stride} is not supported.");
            var all = new SceneVertex[header.U32(0) / stride];
            for (var i = 0; i < all.Length; ++i)
            {
                var position = floats ? raw.V3(i * stride)
                    : new Vector3(Snorm(raw.I16(i * stride)), Snorm(raw.I16(i * stride + 2)), Snorm(raw.I16(i * stride + 4)));
                all[i] = new SceneVertex(position * scale + translation, Vector3.UnitZ, Vector2.Zero, Vector4.One);
            }
            var extra = data.U32(mesh + 4);
            if (catalog.TryGetEntry(extra, out _)) VertexTexcoords(extra, all, data);
            var seen = new HashSet<(uint First, uint Count, ushort Topology, short LogicalPart, bool Hidden)>();
            var partRows = data.Rows(mesh + 0x18, 0x8080737E, 0x20);
            for (var partOrdinal = 0; partOrdinal < partRows.Length; ++partOrdinal)
            {
                var part = partRows[partOrdinal];
                var lod = LodLevel(data.U8(part + 0x1B));
                if (lod < 0 || lod > 0 && !IncludeLowerLods) continue;
                var first = data.U32(part + 8);
                var count = data.U32(part + 12);
                var topology = data.U16(part + 6);
                bool hidden = (data.U16(part + 0x18) & MeshData.HiddenPartFlag) != 0;
                if (!seen.Add((first, count, topology, data.I16(part + 0x14), hidden))) continue;
                var indices = IndexData(data.U32(mesh + 0x10), first, count, checked((byte)topology), all.Length);
                var vertices = all;
                var sourceIndices = Compact(ref vertices, indices);
                GenerateNormals(vertices, indices);
                var stage = -1;
                for (var candidate = 0; candidate < 23; ++candidate)
                    if (partOrdinal >= data.U16(mesh + 0x28 + candidate * 2)
                        && partOrdinal < data.U16(mesh + 0x2A + candidate * 2))
                    { stage = candidate; break; }
                var declaration = stage < 0 ? -1 : data.I16(mesh + 0x58 + stage * 2);
                var material = data.U32(part);
                var variantIndex = data.I16(part + 4);
                if (variantIndex != -1)
                    material = external is not null && variantIndex >= 0 && variantIndex < external.Length
                        ? external[variantIndex] : uint.MaxValue;
                var decoded = new MeshData
                {
                    TagHash = tag,
                    Vertices = vertices,
                    Indices = indices,
                    MaterialTag = material,
                    PartIndex = part,
                    LogicalPartIndex = data.I16(part + 0x14),
                    PartFlags = data.U16(part + 0x18),
                    LodLevel = lod,
                    SourceVertexIndices = sourceIndices,
                    Native = NativeMesh(NativeMeshKind.Entity, data, material, data.U32(mesh), extra,
                        data.U32(mesh + 0x10), first, count, checked((byte)topology), weights: data.U32(mesh + 8),
                        declarationId: declaration, renderStage: stage)
                };
                ApplyNativeTexcoords(decoded);
                result.Add(decoded);
            }
        }
        return result.ToArray();
    }

    /** The POSITION format of the mesh's first authored declaration; null when none resolves. */
    private NativeVertexFormat? DeclaredPositionFormat(TagData data, int mesh)
    {
        for (var stage = 0; stage < 23; ++stage)
        {
            int id = data.I16(mesh + 0x58 + stage * 2);
            if (id < 0) continue;
            try
            {
                nativeLayouts ??= new NativeLayoutCatalog(catalog);
                if (!nativeLayouts.TryGet(id, out var elements, out _)) return null;
                return elements.FirstOrDefault(e => e is { Semantic: "POSITION", SemanticIndex: 0, Slot: 0, Offset: 0 })?.Format;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
            {
                return null;
            }
        }
        return null;
    }

    /** Sky records store a full row-vector matrix before their model wrapper. */
    private void SkyEntities(uint tag, TagData data, uint bubble, Matrix4x4 parent)
    {
        foreach (var row in data.Rows(8, 0x80806F97, 0x80))
        {
            var wrapper = data.U32(row + 0x60);
            if (Class(wrapper) != 0x80806F9B) throw new InvalidDataException("Sky model wrapper class differs from build 86657.");
            var model = Read(wrapper).U32(8);
            if (Class(model) != 0x808073A5) throw new InvalidDataException("Sky resource does not reference an entity model.");
            var matrix = new Matrix4x4(data.F32(row), data.F32(row + 4), data.F32(row + 8), data.F32(row + 12),
                data.F32(row + 16), data.F32(row + 20), data.F32(row + 24), data.F32(row + 28),
                data.F32(row + 32), data.F32(row + 36), data.F32(row + 40), data.F32(row + 44),
                data.F32(row + 48), data.F32(row + 52), data.F32(row + 56), data.F32(row + 60));
            var objectPlacement = new SceneObjectPlacement(tag, row, matrix * parent);
            foreach (var mesh in EntityMesh(model))
                scene.Instances.Add(new MeshInstance
                {
                    Mesh = mesh,
                    ObjectPlacement = objectPlacement,
                    Transform = matrix * parent,
                    Name = Name(model),
                    BubbleHash = bubble,
                    Layer = SceneLayer.Skybox,
                    InitiallyVisible = mesh.NeverDrawn ? false : null,
                    InitialVisibilityReason = mesh.NeverDrawn ? MeshData.HiddenPartReason : null
                });
        }
    }

    /** Records the opened entity's inputs; each spawn entry names the variant it selects. */
    private void DescribePreviewInputs(uint entity, uint configTag, EntityInitialState initial, SpawnInput? spawn,
        uint variant)
    {
        scene.PreviewSpawn = spawn;
        scene.EntityParameters = initial.Parameters;
        scene.PreviewParameters = options.PreviewParameters;
        if (initial.Variants.Count != 0)
        {
            scene.EntityVariants = initial.Variants;
            scene.PreviewVariant = variant;
        }
        if (scene.SpawnChoices.Count != 0 || options.AuthoredSpawns.Count == 0) return;
        var reader = new EntityInitialStateReader(catalog) { Reads = scene.ReadTagSet };
        scene.SpawnChoices = options.AuthoredSpawns.Select(authored =>
        {
            var state = reader.Read(entity, configTag, authored.First.Tag, authored.First.Offset);
            return new SpawnChoice(authored.First, authored.RowCount, state.SelectedVariant, state.Unresolved);
        }).ToArray();
    }

    /** Entity model links are accepted only behind their typed resource marker. */
    private void ClassMeshes(uint classTag, uint bubble, Matrix4x4 transform,
        SceneMarker? placement = null)
    {
        var initialCount = scene.Instances.Count;
        var objectPlacement = placement?.ObjectPlacement ?? new SceneObjectPlacement(classTag, -1, transform);
        var definition = Read(classTag);
        foreach (var build in definition.Rows(0x10, 0x80809C04, 12))
        {
            var configTag = definition.U32(build);
            if (Class(configTag) != 0x80809C36) continue;
            var config = Read(configTag);
            var resource = config.Relative(0x18);
            if (resource < 4 || !config.Has(resource, 0x1E0)) continue;
            var resourceClass = config.U32(resource - 4);
            if (resourceClass == 0x80807286)
            {
                var primary = config.Relative(0x10);
                if (!config.Has(primary - 4, 20) || config.U32(primary - 4) != 0x80807273)
                    throw new InvalidDataException("Cloth model component has the wrong primary class.");
                CheckComponentPair(configTag, config, primary, resource, 0x80807273, 0x80807286);
            }
            else if (resourceClass != 0x808072BD) continue;
            if (config.U32(resource) != configTag)
                throw new InvalidDataException("Entity model component has the wrong owner tag.");
            var model = config.U32(resource + 0x1DC);
            if (Class(model) != 0x808073A5) continue;
            // Only the opened entity takes the preview inputs; a placed entity reads its own row.
            bool root = placement is null && classTag == scene.TagHash;
            SpawnInput? spawn = root ? options.PreviewSpawn
                : placement is null ? null : new SpawnInput(placement.SourceTag, placement.SourceOffset);
            uint variant = root ? options.PreviewVariant : 0;
            var state = InitialMaterials(classTag, configTag, config, resource, spawn, variant, root ? options.SpawnPairs : [],
                root ? options.PreviewParameters : new Dictionary<uint, uint>());
            if (root) DescribePreviewInputs(classTag, configTag, state.Initial, spawn, variant);
            if (root && resourceClass == 0x808072BD) DescribeObjectChannels(classTag, configTag, config, resource);
            if (state.Initial.DisabledByEveryVariant is not null)
            {
                string message = $"Entity 0x{classTag:X8}: no variant with positive weight; variant filter skipped"
                    + (root ? ". Pick another input in Parts." : "");
                AddDiagnostic(message);
            }
            var clothParts = resourceClass == 0x80807286 ? ClothDrawnParts(config, resource, model) : null;
            foreach (var mesh in EntityMesh(model, state.Materials, configTag))
            {
                bool? visible = state.Initial.Enabled(mesh.LogicalPartIndex);
                string? reason = visible == false ? MeshData.InactiveInitialPartReason : visible is null ? state.Initial.Unresolved
                    : state.Initial.EveryVariantDisables(mesh.LogicalPartIndex)
                        ? "Every variant disables this part"
                        : null;
                if (visible is null && reason is null)
                {
                    reason = "Part index outside its bitset";
                    InitialWarning(classTag, configTag, spawn, reason);
                }
                int group = Read(model).I16(mesh.PartIndex + 4);
                if (mesh.NeverDrawn) { visible = false; reason = MeshData.HiddenPartReason; }
                else if (clothParts is not null && mesh.Native is { } native
                    && !clothParts.Contains((native.RenderStage, native.FirstIndex, native.IndexCount)))
                { visible = false; reason = MeshData.ClothSetPartReason; }
                else if (mesh.MaterialTag == uint.MaxValue && (group == -1 || state.MaterialsKnown
                    && group >= 0 && group < state.Materials.Length))
                { visible = false; reason = "No active material"; }
                scene.Instances.Add(new MeshInstance
                {
                    Mesh = mesh,
                    ObjectPlacement = objectPlacement,
                    Transform = transform,
                    Name = placement?.Name ?? Name(classTag),
                    BubbleHash = bubble,
                    Layer = SceneLayer.Objects,
                    ObjectClassTag = classTag,
                    ModelSourceConfigTag = configTag,
                    ModelSourceResourceClass = resourceClass,
                    InitiallyVisible = visible,
                    InitialVisibilityReason = reason,
                    PlacementSourceTag = placement?.SourceTag ?? 0,
                    PlacementSourceOffset = placement?.SourceOffset ?? -1,
                    PlacementIdentity = placement?.PlacementIdentity ?? 0,
                    MapContainers = placement?.MapContainers ?? []
                });
            }
            if (resourceClass == 0x80807286)
                AddDiagnostic($"Cloth model 0x{model:X8} is drawn without cloth simulation.");
        }
        if (placement == null) ReadClassVolumes(classTag, bubble, Name(classTag));
        if (placement == null && scene.Instances.Count == initialCount)
        {
            scene.UnsupportedCount++;
            scene.Diagnostics.Add($"No supported mesh or trigger resource in class {Name(classTag)}.");
        }
    }

    /**
     * The opened entity's object channels: each reads the user's value, else its rest value.
     * The first model config with channels takes the user's values; another model keeps its rest values.
     */
    private void DescribeObjectChannels(uint entity, uint configTag, TagData config, int resource)
    {
        uint[] names;
        try { names = ObjectChannelRest.RequestNames(config, resource); }
        catch (InvalidDataException error) { AddDiagnostic($"Object channels of 0x{configTag:X8}: {error.Message}"); return; }
        if (names.Length == 0) return;
        if (scene.ObjectChannels.Count != 0)
        {
            if (!scene.ObjectChannels.Select(channel => channel.NameHash).SequenceEqual(names))
                AddDiagnostic($"Entity 0x{entity:X8}: model 0x{configTag:X8} names other object channels; it draws with its rest values.");
            return;
        }
        var rest = ChannelRestValues(entity, names);
        scene.ChannelValues = options.ChannelValues;
        scene.ObjectChannels = names.Select((name, index) => new SceneObjectChannel(index, name,
            options.ChannelValues.TryGetValue(name, out float value) ? new Vector4(value) : rest[index].Value, rest[index])).ToArray();
    }

    /** Each named channel's value at rest, read from the value provider and publishers among the entity's components. */
    private ObjectChannelRestValue[] ChannelRestValues(uint entity, uint[] names)
    {
        try
        {
            var definition = Read(entity);
            var configs = definition.Rows(0x10, 0x80809C04, 12).Select(row => definition.U32(row))
                .Where(tag => Class(tag) == 0x80809C36).Select(tag => (tag, Read(tag)));
            return ObjectChannelRest.RestValues(configs, names);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            AddDiagnostic($"Entity 0x{entity:X8}: object channel rest values unread: {error.Message}");
            return names.Select(_ => ObjectChannelRestValue.Open("value provider unreadable")).ToArray();
        }
    }

    // Cloth asset draw sets: four of 384 bytes from +144; each has a 16-byte head, then one int array per stage.
    private const int ClothSets = 144, ClothSetLists = 16, ClothListStride = 16, ClothStages = 23;

    /**
     * The parts the cloth draw 0x7FF742E78D60 takes: set 0's list for each stage, as indices from the stage's first part.
     * The set is the draw data's first int, which 0x7FF742E5B480 leaves zero.
     * @return Stage and index range of each listed part; null when the asset or model has another layout.
     */
    private HashSet<(int Stage, uint First, uint Count)>? ClothDrawnParts(TagData config, int resource, uint modelTag)
    {
        if (!config.Has(resource + 0x358, 4)) return null;
        uint assetTag = config.U32(resource + 0x358);
        if (Class(assetTag) != 0x8080727A) return null;
        var asset = Read(assetTag);
        var model = Read(modelTag);
        var meshes = model.Rows(0x10, 0x80807378, 0x88);
        if (meshes.Length != 1 || !asset.Has(ClothSets + ClothSetLists, ClothStages * ClothListStride)) return null;
        var parts = model.Rows(meshes[0] + 0x18, 0x8080737E, 0x20);
        var drawn = new HashSet<(int, uint, uint)>();
        for (int stage = 0; stage < ClothStages; stage++)
        {
            int start = model.U16(meshes[0] + 0x28 + stage * 2), end = model.U16(meshes[0] + 0x2A + stage * 2);
            foreach (int row in asset.Rows(ClothSets + ClothSetLists + stage * ClothListStride, 0x80800007, 4))
            {
                int part = start + asset.I32(row);
                if (part < start || part >= end || part >= parts.Length)
                    throw new InvalidDataException($"Cloth asset 0x{assetTag:X8} lists a part outside stage {stage}.");
                drawn.Add((stage, model.U32(parts[part] + 8), model.U32(parts[part] + 12)));
            }
        }
        return drawn.Count == 0 ? null : drawn;
    }
}
