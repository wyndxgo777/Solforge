using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using SunriseStudio.Animation;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** Checks bundle assembly and export, including view-only guardian exports and refusal of assemblies without a guardian context. */
internal static class BundleAssemblyChecks
{
    private const string ViewOnly = "Guardian exports are view only.";

    /** A 3-bone rig: one skinned part, one part attached to bone 2 and one part with no skin. */
    internal static void Synthetic(string root)
    {
        string install = Path.Combine(root, "assembly-install"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_assembly_0110_0.pkg"), 0x110, [new(0x80800001, 8 << 9, new byte[8])]);
        using var catalog = PackageCatalog.Open(install);
        var rig = Rig();
        SceneVertex[] vertices = [new(new(1, 0, 0), new(0, 0, 1), new(0, 0), Vector4.One),
            new(new(0, 1, 0), new(0, 0, 1), new(1, 0), Vector4.One), new(new(0, 0, 1), new(1, 0, 0), new(0, 1), Vector4.One)];
        MeshInstance Part(uint tag, Matrix4x4 transform, string name = "") => new()
            { Mesh = new() { TagHash = tag, Vertices = vertices, Indices = [0, 1, 2], SourceVertexIndices = [0, 1, 2] }, Transform = transform, Name = name };
        // Two parts share a 4-byte-per-character label that overflows the 63-byte name limit.
        string emoji = "\U0001F600", wide = string.Concat(Enumerable.Repeat(emoji, 20)) + ": face";
        var scene = new SceneDocument { TagHash = 0x80A20000 };
        scene.Instances.AddRange([Part(0x80A20001, Matrix4x4.Identity), Part(0x80A20002, Matrix4x4.CreateTranslation(5, 0, 0)),
            Part(0x80A20003, Matrix4x4.Identity, wide), Part(0x80A20003, Matrix4x4.Identity, wide)]);
        VertexSkinWeights Weight(int bone) => new(bone, 0, 0, 0, new(1, 0, 0, 0));
        var skinned = new ModelSkinBinding(0x80A20001, 0, 0, 12, [0], [Weight(1), Weight(1), Weight(0)]);
        var attached = new ModelSkinBinding(0x80A20002, 0, 0, 12, [0], [Weight(0), Weight(0), Weight(0)]);
        var context = new AssetOpenContext(0x80A20000) { Guardian = GuardianLoadout.Default with { Weapon = 0x80A20009 } };
        var assembly = new AssetBundleAssembly("Synthetic guardian", 0x80A20000, scene, rig, [skinned],
            new Dictionary<ModelSkinBinding, int>(ReferenceEqualityComparer.Instance) { [attached] = 2 }, [], context);
        try
        {
            AssetBundle.ExportAssembly(catalog, assembly with { Context = null }, Path.Combine(root, "assembly-no-context"));
            throw new InvalidDataException("An assembly without a guardian context was exported.");
        }
        catch (ArgumentException) { }
        var result = AssetBundle.ExportAssembly(catalog, assembly, Path.Combine(root, "assembly-synthetic"));
        string folder = Path.GetDirectoryName(result.ManifestPath)!;
        var manifest = result.Manifest;
        Check(manifest.Meshes[1].Transform.SequenceEqual([1f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]),
            "Attached part kept its scene transform on top of the bone bake.");
        Check(manifest.Meshes.Count == 4 && manifest.Meshes[0].Skinned && manifest.Meshes[1].Skinned && !manifest.Meshes[2].Skinned
            && !manifest.Meshes[3].Skinned,
            "Assembly meshes lost their skin state.");
        var bind = rig.Bones[2].BindObject.Matrix;
        var baked = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, manifest.Meshes[1].Path)))!;
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            var expected = Vector3.Transform(vertices[vertex].Position, bind);
            var position = baked["Positions"]![vertex]!.AsArray().Select(value => (float)value!).ToArray();
            Check(Vector3.Distance(new(position[0], position[1], position[2]), expected) < 1e-5f, "Attached part is not baked by its bone's bind.");
            Check(baked["Joints"]![vertex]!.AsArray().Select(value => (int)value!).SequenceEqual([2, 0, 0, 0])
                && baked["Weights"]![vertex]!.AsArray().Select(value => (float)value!).SequenceEqual([1f, 0, 0, 0]), "Attached part is not all on bone 2.");
        }
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, manifest.ScenePath!)))!;
        Check(gltf["skins"]!.AsArray().Count == 1 && gltf["skins"]![0]!["joints"]!.AsArray().Count == 3, "Assembly needs one 3-joint armature.");
        Check(Joints(gltf, 1).All(lanes => lanes[0] == 2), "Attached glTF joints are not bone 2.");
        Check(manifest.Animations.Count == 0 && gltf["animations"] is null, "Assembly without clips wrote an animation.");
        var meshNodes = gltf["nodes"]!.AsArray().Where(node => node!["mesh"] is not null).ToArray();
        string[] names = meshNodes.Select(node => (string)node!["name"]!).ToArray();
        // 13 and 12 whole characters fill the 63-byte limit with and without the " #2" suffix.
        string[] expectedNames = ["Part 80A20001 0", "Part 80A20002 0", string.Concat(Enumerable.Repeat(emoji, 13)) + " 80A20003 0",
            string.Concat(Enumerable.Repeat(emoji, 12)) + " 80A20003 0 #2"];
        Check(names.SequenceEqual(expectedNames) && names.All(name => System.Text.Encoding.UTF8.GetByteCount(name) <= 63)
            && meshNodes.Select(node => (string)node!["extras"]!["sunriseMeshId"]!).SequenceEqual(manifest.Meshes.Select(mesh => mesh.Id)),
            $"Assembly mesh nodes lost their display names or their mesh ids: {string.Join(" | ", names)}");
        Check(manifest.Notices.Any(value => value.Surface == "Guardian assembly")
            && manifest.Notices.Any(value => value.Surface == "Skin" && value.TagHash == 0x80A20003), "Assembly notices are missing.");
        Check(Equals(Reread(result.ManifestPath).Context, context), "Assembly context did not round-trip.");
        Refused(Path.Combine(root, "assembly-project"), catalog, result.ManifestPath);
        Console.WriteLine("bundle_assembly synthetic=pass attached=baked joints=3 view_only=refused");
    }

    /**
     * Titan body with its own skins, plus the default head and a cloth cloak skinned onto the body rig,
     * posed by the rifle hold pose. The cloak is Gensym Knight Cloak's male entity.
     */
    internal static void Owned(string game, string output)
    {
        const uint body = 0x80BAB778, head = 0x80BB1136, cloak = 0x80BC374B, hold = 0x80BC9FDB;
        using var catalog = PackageCatalog.Open(game);
        var rig = new SkeletonReader(catalog).Read(body);
        var scene = new SceneDocument { TagHash = body };
        var reader = new ModelSkinReader(catalog);
        var skins = new List<ModelSkinBinding>();
        foreach (uint model in rig.ModelTags) skins.AddRange(reader.Read(model, rig));
        var clothModels = new HashSet<uint>();
        foreach (var (entity, external) in new[] { (body, false), (head, true), (cloak, true) })
        {
            var parts = new AssetSceneLoader(catalog).Load(entity).Instances.Where(value => value.Mesh.LodLevel == 0).ToArray();
            scene.Instances.AddRange(parts);
            if (!external) continue;
            // Cloth models skin by their skeletal input; every other model by its own weights.
            foreach (var cloth in parts.Where(value => value.RequiresClothSimulation)
                .Select(value => new SkeletonReader(catalog).ClothReference(value.ModelSourceConfigTag)).OfType<ClothModelReference>().Distinct())
            {
                skins.AddRange(reader.ReadExternalClothInput(cloth, rig));
                clothModels.Add(cloth.ModelTag);
            }
            foreach (uint model in parts.Select(value => value.Mesh.TagHash).Distinct().Where(model => !clothModels.Contains(model)))
                skins.AddRange(reader.ReadExternal(model, rig));
        }
        Check(clothModels.Count != 0, $"Entity 0x{cloak:X8} has no cloth model.");
        var context = new AssetOpenContext(body) { Guardian = GuardianLoadout.Default };
        var assembly = new AssetBundleAssembly("Titan guardian", body, scene, rig, skins,
            new Dictionary<ModelSkinBinding, int>(), [hold], context);
        string folder = Path.Combine(output, "assembly-owned");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var result = AssetBundle.ExportAssembly(catalog, assembly, folder);
        var manifest = result.Manifest;
        var gltf = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, manifest.ScenePath!)))!;
        int joints = gltf["skins"]!.AsArray().Count == 1 ? gltf["skins"]![0]!["joints"]!.AsArray().Count : -1;
        Check(joints == 72, $"Guardian armature has {joints} joints, expected one skin with 72.");
        string[] objectNames = gltf["nodes"]!.AsArray().Where(node => node!["mesh"] is not null).Select(node => (string)node!["name"]!).ToArray();
        Check(objectNames.Length == manifest.Meshes.Count && objectNames.Distinct().Count() == objectNames.Length
            && objectNames.All(name => System.Text.Encoding.UTF8.GetByteCount(name) <= 63 && !name.StartsWith("mesh_")),
            "Guardian object names are not unique, short display names.");
        int skinned = 0;
        for (int mesh = 0; mesh < gltf["meshes"]!.AsArray().Count; mesh++)
        {
            var lanes = Joints(gltf, mesh);
            if (lanes.Length == 0) continue;
            skinned++;
            Check(lanes.All(value => value.All(joint => joint < 72)), $"Mesh {mesh} names a joint past the 72-bone rig.");
        }
        int headMeshes = manifest.Meshes.Count(value => value.Skinned && !rig.ModelTags.Contains(value.ModelTag));
        Check(headMeshes > 0, "No head mesh was skinned onto the body rig.");
        var clothMeshes = manifest.Meshes.Where(value => clothModels.Contains(value.ModelTag)).ToArray();
        Check(clothMeshes.Length != 0 && clothMeshes.All(value => value.Skinned), "A cloak cloth mesh is not skinned onto the body rig.");
        Check(manifest.Notices.Any(value => value.Surface == "Cloth"), "The cloth notice is missing.");
        Check(manifest.Animations.Count == 1 && manifest.Animations[0] is { Error: null, PosePath: null, ControlsPath: null }
            && gltf["animations"]!.AsArray().Count == 1, "Guardian bundle needs exactly one sampled pose action, in the scene glTF only.");
        Check(Equals(Reread(result.ManifestPath).Context, context), "Guardian context did not round-trip.");
        Refused(Path.Combine(output, "assembly-owned-project"), catalog, result.ManifestPath);
        Console.WriteLine($"bundle_assembly owned=pass meshes={manifest.Meshes.Count} skinned={skinned} head_meshes={headMeshes} cloth_meshes={clothMeshes.Length} "
            + $"joints={joints} materials={manifest.Materials.Count} assets={manifest.NativeAssets.Count} path={result.ManifestPath}");
    }

    /** PrepareImport must refuse the bundle with the view-only message. */
    private static void Refused(string projectPath, PackageCatalog catalog, string manifestPath)
    {
        if (Directory.Exists(projectPath)) Directory.Delete(projectPath, true);
        var project = StudioProject.Create(projectPath, "Guardian refusal");
        try { AssetBundle.PrepareImport(project, catalog, manifestPath, new(AssetBundleImportMode.ApplyDeltas)); }
        catch (InvalidDataException error) when (error.Message == ViewOnly) { return; }
        throw new InvalidDataException("A guardian bundle was not refused as view only.");
    }

    /** JOINTS_0 lanes of one glTF mesh; empty when it is not skinned. */
    private static int[][] Joints(JsonNode gltf, int mesh)
    {
        var attributes = gltf["meshes"]![mesh]!["primitives"]![0]!["attributes"]!;
        if (attributes["JOINTS_0"] is not { } index) return [];
        var accessor = gltf["accessors"]![(int)index]!;
        var view = gltf["bufferViews"]![(int)accessor["bufferView"]!]!;
        string uri = (string)gltf["buffers"]![0]!["uri"]!;
        byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        int offset = (int)view["byteOffset"]!, count = (int)accessor["count"]!;
        Check((int)accessor["componentType"]! == 5123, "JOINTS_0 is not unsigned short.");
        return Enumerable.Range(0, count).Select(vertex => Enumerable.Range(0, 4)
            .Select(lane => (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + (vertex * 4 + lane) * 2))).ToArray()).ToArray();
    }

    /** Root at the origin, bone 1 one unit up, bone 2 turned 90 degrees about Z one unit along X. */
    private static SkeletonRig Rig()
    {
        BoneTransform[] local = [BoneTransform.Identity, new(Quaternion.Identity, new(0, 0, 1), 1),
            new(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(1, 0, 0), 1)];
        var bones = new SkeletonBone[3];
        var objects = new Matrix4x4[3];
        for (int bone = 0; bone < 3; bone++)
        {
            objects[bone] = bone == 0 ? local[0].Matrix : local[bone].Matrix * objects[bone - 1];
            Matrix4x4.Invert(objects[bone], out var inverse);
            bones[bone] = new(bone, (uint)bone, $"bone{bone}", bone - 1, bone < 2 ? bone + 1 : -1, -1,
                BoneTransform.FromMatrix(objects[bone]), BoneTransform.FromMatrix(inverse), local[bone]);
        }
        var constructor = typeof(SkeletonRig).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        return (SkeletonRig)constructor.Invoke([0u, 0u, Array.Empty<uint>(), bones, new[] { 0, 1, 2 }, 0f, Array.Empty<ClothModelReference>()]);
    }

    private static AssetBundleManifest Reread(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<AssetBundleManifest>(File.ReadAllText(path))!;

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
