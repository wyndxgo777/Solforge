using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

namespace SunriseStudio.Authoring;

public sealed record PreparedRigidExoticArt(IReadOnlyList<ProjectAssetAddition> Assets,
    IReadOnlyDictionary<int, uint> Entities, IReadOnlyDictionary<string, string> SourceHashes);

/** Imports rigid, textured GLBs for all art selectors while preserving the donor's animation rig. */
public static class RigidExoticImport
{
    public const uint BaseItem = 0x81319B72; // Borealis, build 86657.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsManifest(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> SourceFiles(string path, IReadOnlyList<WeaponArtVariant> variants) =>
        ReadManifest(path, variants, out _).Select(input => input.Path).OfType<string>().Prepend(path);

    public static OriginalAssetInspection Inspect(string path, IReadOnlyList<WeaponArtVariant> variants)
    {
        var inputs = ReadManifest(path, variants, out _);
        return new(path, inputs.Select(input => new OriginalAssetSurface(
            $"Art selector {input.Row.Selector}", true, input.Row.RetainDonor || File.Exists(input.Path),
            input.Row.RetainDonor ? "Keeps the stock donor assembly."
                : File.Exists(input.Path) ? "GLB present; native geometry and materials are checked on Create."
                : $"Missing {input.Row.File}.")).ToArray());
    }

    public static PreparedRigidExoticArt Prepare(PackageCatalog catalog, string path,
        ushort package, int firstEntry, string name, IReadOnlyList<WeaponArtVariant> variants)
    {
        if (!PackageWriter.IsNewPackageId(package) || catalog.ContainsInstalledPackage(package))
            throw new InvalidDataException("Choose an unused package ID for the rigid art.");
        var inputs = ReadManifest(path, variants, out var hashes, out var transform);
        var additions = new List<ProjectAssetAddition>();
        var entities = new Dictionary<int, uint>();
        int next = firstEntry;
        foreach (var (row, source) in inputs)
        {
            uint entity = variants.Single(variant => variant.Selector == row.Selector).EntityTag;
            if (row.RetainDonor)
            {
                byte[] bytes = catalog.Read(entity);
                if (!catalog.TryGetEntry(entity, out var donor) || donor.Reference != 0x80809C0F
                    || !Hash(bytes).Equals(row.EntitySha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The retained donor entity for selector {row.Selector} changed.");
                if (next is < 0 or >= 8192)
                    throw new InvalidDataException("No package entry remains for a retained donor assembly.");
                uint tag = 0x80800000u + ((uint)package << 13) + (uint)next;
                if (catalog.ContainsTag(tag))
                    throw new InvalidDataException("The retained donor assembly destination is already in use.");
                additions.Add(new($"{name}.art-{row.Selector}.entity", bytes,
                    new(package, donor.Reference, donor.TypeInfo, null, next), entity));
                entities.Add(row.Selector, tag);
                next++;
                continue;
            }
            ArgumentNullException.ThrowIfNull(source);
            if (!hashes.ContainsKey(source))
                hashes.Add(source, Hash(File.ReadAllBytes(source)));
            using var document = GltfInterchange.ReadJson(source);
            var root = document.RootElement;
            if (root.GetProperty("buffers").EnumerateArray().Any(buffer => buffer.TryGetProperty("uri", out _))
                || (root.TryGetProperty("images", out var images)
                    && images.EnumerateArray().Any(image => image.TryGetProperty("uri", out _))))
                throw new InvalidDataException("Rigid weapon GLBs must embed their buffers and textures.");
            var read = GltfInterchange.Read(source);
            if (row.PaletteIndices is null || row.PaletteIndices.Length != read.Count)
                throw new InvalidDataException($"Art selector {row.Selector} needs one palette index per primitive.");
            var excluded = (row.ExcludePrimitives ?? []).ToHashSet();
            if (excluded.Any(index => index < 0 || index >= read.Count) || excluded.Count == read.Count)
                throw new InvalidDataException($"Art selector {row.Selector} excludes a missing primitive or every primitive.");
            int[] kept = Enumerable.Range(0, read.Count).Where(index => !excluded.Contains(index)).ToArray();
            // The manifest transform undoes exporter node transforms in package Z-up space; glTF V goes straight to D3D.
            var place = transform * Matrix4x4.CreateTranslation(Translation(row.Translation, $"Art selector {row.Selector}"));
            var meshes = kept.Select(index => read[index] with
            {
                Positions = read[index].Positions.Select(point => Vector3.Transform(point, place)).ToArray(),
                Normals = read[index].Normals.Select(normal => Vector3.Normalize(Vector3.TransformNormal(normal, transform))).ToArray(),
                TexCoords = read[index].TexCoords.Select(uv => new Vector2(uv.X, 1 - uv.Y)).ToArray()
            }).ToArray();
            short[] palette = kept.Select(index => row.PaletteIndices[index]).ToArray();
            int[] material = SharedMaterials(root, meshes.Select(mesh => mesh.MaterialIndex ?? -1).ToArray());
            if (!uint.TryParse(row.ModelTag?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint sourceModel))
                throw new InvalidDataException($"Art selector {row.Selector} needs a hexadecimal donor model tag.");
            var part = new AssetSceneLoader(catalog).Load(entity).Instances.FirstOrDefault(part =>
                part.Mesh.TagHash == sourceModel && part.Mesh.PartIndex == row.SourcePartOffset)
                ?? throw new InvalidDataException($"The donor has no matching model part for selector {row.Selector}.");
            uint shader = part.Mesh.Native?.VertexShaderTag
                ?? throw new InvalidDataException($"The donor part for selector {row.Selector} has no native shader.");
            string partName = $"{name}.art-{row.Selector}";
            var materials = WeaponAssetMaterialCompiler.PrepareUsed(catalog, source, partName, package, next,
                shader, material);
            var primitives = meshes.Select((mesh, index) => new WeaponRigidPrimitive(mesh,
                materials.MaterialTags[material[index]], palette[index])).ToArray();
            var model = WeaponAssetModelCompiler.Prepare(catalog, primitives,
                new(entity, sourceModel, row.SourcePartOffset, package, materials.NextEntryIndex, partName));
            var art = WeaponAssetEntityCompiler.PrepareRigid(catalog, entity, part.ModelSourceConfigTag, sourceModel,
                row.EntitySha256.ToUpperInvariant(), row.ConfigSha256.ToUpperInvariant(), model.ModelTag, package,
                checked(materials.NextEntryIndex + model.Assets.Count), partName);
            additions.AddRange(materials.Assets);
            additions.AddRange(model.Assets);
            additions.AddRange(art.Assets);
            next = checked(firstEntry + additions.Count);
            entities.Add(row.Selector, art.EntityTag);
        }
        WeaponOriginalAssetImporter.ValidateSources(hashes);
        return new(additions, entities, hashes);
    }

    /** glTF materials that differ only in name, extras or duplicate texture slots compile to one native material. */
    private static int[] SharedMaterials(JsonElement root, int[] indices)
    {
        if (!root.TryGetProperty("materials", out var materials)) return indices;
        var textures = root.TryGetProperty("textures", out var rows) ? rows.EnumerateArray().ToArray() : [];
        string Key(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
                .Where(property => property.Name is not ("name" or "extras"))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => property.Name + ":" + (property.Name == "index" && property.Value.TryGetInt32(out int texture)
                    && texture >= 0 && texture < textures.Length ? Key(textures[texture]) : Key(property.Value)))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Key)) + "]",
            _ => value.GetRawText()
        };
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        return indices.Select(index => index < 0 || index >= materials.GetArrayLength() ? index
            : first.TryAdd(Key(materials[index]), index) ? index : first[Key(materials[index])]).ToArray();
    }

    private static (Variant Row, string? Path)[] ReadManifest(string path,
        IReadOnlyList<WeaponArtVariant> variants, out Dictionary<string, string> hashes)
        => ReadManifest(path, variants, out hashes, out _);

    private static (Variant Row, string? Path)[] ReadManifest(string path,
        IReadOnlyList<WeaponArtVariant> variants, out Dictionary<string, string> hashes, out Matrix4x4 transform)
    {
        path = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(path);
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes, Json);
        if (manifest is not { Version: 1, Variants: not null } ||
            manifest.Variants.Any(row => row is null) || !manifest.Variants.Any(row => !row.RetainDonor) ||
            !manifest.Variants.Select(row => row.Selector).Order()
                .SequenceEqual(variants.Select(variant => variant.Selector).Order()))
            throw new InvalidDataException("Use a version 1 rigid art manifest with exactly one GLB for every donor art selector.");
        float[] rotation = manifest.RotationDegrees ?? [0, 0, 0];
        float scale = manifest.Scale ?? 1;
        if (rotation.Length != 3 || !rotation.All(float.IsFinite) || !float.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("The rigid art manifest needs a positive scale and three finite rotation angles.");
        // Scale, then rotate about package X, Y and Z, in degrees, then move by the translation in metres.
        transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rotation[0] * MathF.PI / 180)
            * Matrix4x4.CreateRotationY(rotation[1] * MathF.PI / 180) * Matrix4x4.CreateRotationZ(rotation[2] * MathF.PI / 180)
            * Matrix4x4.CreateTranslation(Translation(manifest.Translation, "The rigid art manifest"));
        hashes = new(StringComparer.OrdinalIgnoreCase) { [path] = Hash(bytes) };
        string folder = Path.GetDirectoryName(path)!;
        return manifest.Variants.Select(row =>
        {
            if (row.EntitySha256?.Length != 64 || !row.EntitySha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Art selector {row.Selector} needs a donor entity fingerprint.");
            if (row.RetainDonor)
            {
                if (!string.IsNullOrEmpty(row.File) || row.ExcludePrimitives is not null || row.Translation is not null)
                    throw new InvalidDataException($"Art selector {row.Selector} cannot retain donor art and specify a GLB.");
                return (row, (string?)null);
            }
            if (string.IsNullOrWhiteSpace(row.File) || Path.GetFileName(row.File) != row.File
                || !row.File.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                || row.ConfigSha256?.Length != 64 || !row.ConfigSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Art selector {row.Selector} needs a neighboring GLB and both native SHA-256 fingerprints.");
            return (row, Path.Combine(folder, row.File));
        }).ToArray();
    }

    /** An optional package-space offset in metres; at most one metre on each axis. */
    private static Vector3 Translation(float[]? value, string owner)
    {
        if (value is null) return Vector3.Zero;
        if (value.Length != 3 || !value.All(axis => float.IsFinite(axis) && MathF.Abs(axis) <= 1))
            throw new InvalidDataException(owner + " needs a translation of three finite metres, each within 1.");
        return new(value[0], value[1], value[2]);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Manifest(int Version, Variant[] Variants, float? Scale = null, float[]? RotationDegrees = null,
        float[]? Translation = null);
    private sealed record Variant(int Selector, string File, string ModelTag, int SourcePartOffset,
        short[] PaletteIndices, string EntitySha256, string ConfigSha256, bool RetainDonor = false,
        int[]? ExcludePrimitives = null, float[]? Translation = null);
}
