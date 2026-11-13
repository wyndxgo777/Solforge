using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    switch (args)
    {
        case ["inspect", var source]:
            Inspect(source);
            return 0;
        case ["donor", var gameRoot, var entityTag]:
            using (var catalog = PackageCatalog.Open(gameRoot))
                DescribeDonor(catalog, Tag(entityTag));
            return 0;
        case ["item", var itemRoot, var itemId]:
            using (var catalog = PackageCatalog.Open(itemRoot))
                DescribeItem(catalog, itemId);
            return 0;
        case ["source-hash", var sourceRoot, var sourceTag]:
            using (var catalog = PackageCatalog.Open(sourceRoot))
                Print(Convert.ToHexString(SHA256.HashData(catalog.Read(Tag(sourceTag)))));
            return 0;
        case ["validate-rigid", var rigidRoot, var manifest, var package]:
            using (var catalog = PackageCatalog.Open(rigidRoot))
            {
                var donor = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(RigidExoticImport.BaseItem));
                var art = RigidExoticImport.Prepare(catalog, manifest, checked((ushort)Tag(package)),
                    0, "Rigid weapon validation", donor.Variants);
                Print(new { art.Entities, assets = art.Assets.Count });
            }
            return 0;
        case ["compile", var root, var model, var mapping, var output]:
            Compile(root, model, mapping, output);
            return 0;
        default:
            Console.Error.WriteLine("""
                weapon-model inspect <model.glb>
                weapon-model donor <game-root> <entity-tag>
                weapon-model item <game-root> <exact-name-or-item-hash-or-hex-tag>
                weapon-model source-hash <game-root> <native-tag>
                weapon-model validate-rigid <game-root> <manifest.json> <unused-package-id>
                weapon-model compile <game-root> <model.glb> <mapping.json> <new-output-directory>
                """);
            return 2;
    }
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or
    InvalidDataException or NotSupportedException or ArgumentException or FormatException or
    OverflowException or JsonException or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

void Inspect(string source)
{
    var meshes = GltfInterchange.Read(source);
    using var document = GltfInterchange.ReadJson(source);
    var root = document.RootElement;
    Print(new
    {
        primitives = meshes.Select((mesh, index) => new
        {
            index, mesh.Name, mesh.MaterialIndex,
            vertices = mesh.Positions.Length, triangles = mesh.Indices.Length / 3
        }),
        materials = root.TryGetProperty("materials", out var materials) ? materials : (JsonElement?)null,
        images = GltfInterchange.ReadImages(source).Select(image => new
        {
            image.Index, image.Name, image.MimeType, size = image.Bytes.Length
        })
    });
}

void DescribeDonor(PackageCatalog catalog, uint entity)
{
    var scene = new AssetSceneLoader(catalog).Load(entity);
    var parts = scene.Instances.Where(part => part.InitiallyVisible != false && part.Mesh.LodLevel == 0)
        .Select(part => new
        {
            part.Name, modelTag = Hex(part.Mesh.TagHash), sourcePartOffset = part.Mesh.PartIndex,
            modelConfigTag = Hex(part.ModelSourceConfigTag),
            materialTag = Hex(part.Mesh.MaterialTag),
            declaration = part.Mesh.Native?.NativeDeclarationId,
            renderStage = part.Mesh.Native?.RenderStage,
            partFlags = $"0x{part.Mesh.PartFlags:X4}",
            part.Mesh.LogicalPartIndex,
            vertexShaderTag = part.Mesh.Native is { } shader ? Hex(shader.VertexShaderTag) : null,
            paletteIndices = part.Mesh.Native is { NativeDeclarationId: 139 } native
                && native.VertexStreams[0] is { Stride: 8 } stream
                ? part.Mesh.SourceVertexIndices.Select(vertex => BinaryPrimitives.ReadInt16LittleEndian(
                    stream.Data.AsSpan(checked((int)vertex * 8 + 6)))).Distinct().Order().ToArray()
                : []
        }).ToArray();
    if (parts.Length == 0)
        throw new InvalidDataException("The donor entity has no readable LOD 0 mesh in this catalog.");
    Print(parts);
}

void DescribeItem(PackageCatalog catalog, string identity)
{
    var investment = InvestmentCatalog.Load(catalog);
    uint? number = identity.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Tag(identity)
        : uint.TryParse(identity, out uint hash) ? hash : null;
    var item = investment.Items.FirstOrDefault(row => number is uint id
        ? row.Hash == id || row.Tag == id
        : string.Equals(row.Name, identity, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("The donor weapon was not found in the installed item catalog.");
    var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(item.Tag));
    Print(new
    {
        item.Name, item.TypeName, item.Hash, itemTag = Hex(item.Tag),
        entityTag = Hex(art.EntityTag),
        variants = art.Variants.Select(variant => new { variant.Selector, entityTag = Hex(variant.EntityTag) })
    });
}

void Compile(string root, string source, string mapping, string output)
{
    if (Directory.Exists(output) || File.Exists(output))
        throw new IOException("Choose a new output directory.");
    var plan = JsonSerializer.Deserialize<CompileMapping>(File.ReadAllText(mapping), json)
        ?? throw new InvalidDataException("Missing weapon model mapping.");
    var meshes = GltfInterchange.Read(source);
    if (plan.Primitives is null || plan.Primitives.Length != meshes.Count)
        throw new InvalidDataException("Specify one native material and palette index per inspected primitive, in order.");

    using var catalog = PackageCatalog.Open(root);
    foreach (uint material in plan.Primitives.Where(part => part.MaterialTag is not null)
        .Select(part => Tag(part.MaterialTag)).Distinct())
        if (!catalog.ContainsTag(material))
            throw new InvalidDataException($"Native material {Hex(material)} is absent from the donor catalog.");
    var options = new WeaponRigidModelOptions(Tag(plan.EntityTag), Tag(plan.ModelTag), plan.SourcePartOffset,
        checked((ushort)Tag(plan.PackageId)), plan.FirstEntryIndex, plan.Name,
        plan.SkeletonEntityTag is null ? 0 : Tag(plan.SkeletonEntityTag));
    var usedMaterials = meshes.Select((mesh, index) => (mesh, index))
        .Where(row => plan.Primitives[row.index].MaterialTag is null)
        .Select(row => row.mesh.MaterialIndex ?? -1).ToArray();
    PreparedWeaponMaterials? imported = null;
    if (usedMaterials.Length > 0)
    {
        var donor = new AssetSceneLoader(catalog).Load(options.EntityTag).Instances
            .FirstOrDefault(part => part.Mesh.TagHash == options.ModelTag
                && part.Mesh.PartIndex == options.SourcePartOffset);
        uint shader = donor?.Mesh.Native?.VertexShaderTag
            ?? throw new InvalidDataException("The selected donor part has no native vertex shader.");
        imported = WeaponAssetMaterialCompiler.PrepareUsed(catalog, source, options.Name, options.PackageId,
            options.FirstEntryIndex, shader, usedMaterials);
        options = options with { FirstEntryIndex = imported.NextEntryIndex };
    }
    var primitives = meshes.Select((mesh, index) => new WeaponRigidPrimitive(mesh,
        plan.Primitives[index].MaterialTag is { } material ? Tag(material)
            : imported!.MaterialTags[mesh.MaterialIndex ?? -1],
        plan.Primitives[index].PaletteIndex)).ToArray();
    var model = WeaponAssetModelCompiler.Prepare(catalog, primitives, options);
    var art = plan.CloneArtEntity ? WeaponAssetEntityCompiler.Prepare(catalog, options.EntityTag,
        model.ModelTag, options.PackageId, checked(options.FirstEntryIndex + model.Assets.Count), options.Name)
        : null;
    var assets = (imported?.Assets ?? []).Concat(model.Assets).Concat(art?.Assets ?? []).ToArray();

    var manifest = new
    {
        kind = "solforge-rigid-weapon-model",
        version = 1,
        modelTag = Hex(model.ModelTag),
        artEntityTag = art is null ? null : Hex(art.EntityTag),
        vertexShaderTag = Hex(model.VertexShaderTag),
        model.VertexCount,
        model.TriangleCount,
        assets = assets.Select((asset, index) => new
        {
            file = $"{index:D3}.bin", asset.Name, asset.Native, asset.BaseTagHash
        }).ToArray()
    };
    string fullOutput = Path.GetFullPath(output);
    Directory.CreateDirectory(fullOutput);
    for (int index = 0; index < assets.Length; index++)
        File.WriteAllBytes(Path.Combine(fullOutput, manifest.assets[index].file), assets[index].Data);
    File.WriteAllText(Path.Combine(fullOutput, "weapon-model.json"), JsonSerializer.Serialize(manifest, json));
    Print(manifest);
}

void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, json));

static uint Tag(string? value)
{
    if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
        value = value[2..];
    return uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint result)
        ? result : throw new FormatException("Expected a hexadecimal package ID or native tag.");
}

static string Hex(uint value) => $"0x{value:X8}";

internal sealed record CompileMapping(string Name, string EntityTag, string ModelTag, int SourcePartOffset,
    string PackageId, int FirstEntryIndex, string? SkeletonEntityTag, PrimitiveMapping[] Primitives,
    bool CloneArtEntity = false);
internal sealed record PrimitiveMapping(string? MaterialTag, short PaletteIndex);
