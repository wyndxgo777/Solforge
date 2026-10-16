using System.Numerics;
using System.Text.Json;
using SunriseStudio.Formats;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks the native-basis world oracle: linked-object shader semantics must be retained for material-normal parts. */
internal static class WorldBasisChecks
{
    /** The same owned part supplies both the established asset importer and shared world importer. */
    internal static void Export(string game, uint tag, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var connections = new AssetConnectionReader(catalog).Read(tag);
        var source = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(connections.PreviewTag);
        MeshInstance? chosen = null;
        foreach (var instance in source.Instances.Where(instance => instance.InitiallyVisible != false))
        {
            if (instance.Mesh.Native is not { } native || !NativeMeshBasis.IsSupported(catalog, native)) continue;
            var preview = MaterialPreviewAnalyzer.Read(catalog, instance.Mesh.MaterialTag, renderStages: [native.RenderStage]);
            if (preview.Recipe is null && preview.Palette is null && preview.Program?.Outputs.ContainsKey("Normal") == true)
            { chosen = instance; break; }
        }
        if (chosen is null) throw new InvalidDataException("Owned basis fixture has no supported material-normal part.");
        Matrix4x4 shear = Matrix4x4.Identity; shear.M12 = .3f; shear.M23 = .2f; shear.M42 = 4;
        Matrix4x4[] variations = [Matrix4x4.Identity,
            Matrix4x4.CreateScale(2, .5f, 3) * Matrix4x4.CreateRotationX(.4f) * Matrix4x4.CreateTranslation(4, 0, 0),
            Matrix4x4.CreateScale(-1, 2, .75f) * Matrix4x4.CreateRotationZ(.37f) * Matrix4x4.CreateTranslation(-4, 0, 0), shear];
        var world = new SceneDocument { Name = "Owned native-basis world oracle", TagHash = tag };
        for (int index = 0; index < variations.Length; index++)
            world.Instances.Add(new()
            {
                Mesh = chosen.Mesh, Transform = chosen.Transform * variations[index], Name = "Basis " + index,
                ModelSourceConfigTag = chosen.ModelSourceConfigTag, ModelSourceResourceClass = chosen.ModelSourceResourceClass,
                InitiallyVisible = true, Layer = chosen.Layer, InstanceSourceTag = chosen.InstanceSourceTag, InstanceSourceIndex = index
            });
        var modern = WorldSceneExport.Export(catalog, world, Path.Combine(output, "world"));
        if (modern.Manifest.Geometry.Count != 1 || modern.Manifest.Materials.Count != 1
            || !modern.Manifest.Materials[0].NeedsNativeBasis || modern.Manifest.Prototypes.Any(prototype => prototype.CollectionInstanceSafe))
            throw new InvalidDataException("World basis fixture did not retain linked-object shader semantics.");
        var legacy = AssetBundle.Export(catalog, tag, Path.Combine(output, "asset"), new(IncludeAnimations: false));
        var baseline = legacy.Manifest.Meshes.First(mesh => mesh.ModelTag == chosen.Mesh.TagHash
            && mesh.PartIndex == chosen.Mesh.PartIndex && mesh.MaterialTag == chosen.Mesh.MaterialTag
            && mesh.RenderStage == chosen.Mesh.Native!.RenderStage);
        File.WriteAllText(Path.Combine(output, "oracle.json"), JsonSerializer.Serialize(new
        {
            WorldManifest = modern.ManifestPath, AssetManifest = legacy.ManifestPath, AssetMeshId = baseline.Id,
            ModelTag = chosen.Mesh.TagHash, chosen.Mesh.PartIndex, chosen.Mesh.MaterialTag,
            WorldTransforms = world.Instances.Select(instance => Values(instance.Transform)).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"world_basis_fixture tag={tag:X8} model={chosen.Mesh.TagHash:X8} part={chosen.Mesh.PartIndex} instances=4 geometry=1 path={output}");
    }

    private static float[] Values(Matrix4x4 value) => [value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24, value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44];
}
