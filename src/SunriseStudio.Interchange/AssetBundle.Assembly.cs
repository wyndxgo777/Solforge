using System.Numerics;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Interchange;

/** Exports an already assembled scene on one rig as a view-only bundle (ExportAssembly). */
public static partial class AssetBundle
{
    /** Same folder rules and staging as Export. The bundle is view only; PrepareImport refuses it. */
    public static AssetBundleExportResult ExportAssembly(PackageCatalog catalog, AssetBundleAssembly assembly,
        string outputFolder, AssetBundleExportOptions? options = null, CancellationToken token = default,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly.Context?.Guardian is null)
            throw new ArgumentException("An assembly export needs a guardian context.", nameof(assembly));
        options ??= new();
        return Staged(catalog, outputFolder, options, token,
            staging => WriteAssembly(catalog, assembly, staging, options, token, progress));
    }

    /** One armature for every mesh; an attached mesh is baked onto its bone with all its weight there. */
    private static AssetBundleManifest WriteAssembly(PackageCatalog catalog, AssetBundleAssembly assembly, string root,
        AssetBundleExportOptions options, CancellationToken token, Action<string>? progress)
    {
        var notices = new List<BundleNotice> { new("Guardian assembly", "View only", "Cannot be imported back.") };
        if (assembly.ShaderInputs is null)
            notices.Add(new("Dyes", "Approximate", "Per-part dyes are left out."));
        if (assembly.AttachedBones.Count != 0)
        {
            notices.Add(new("Weapon", "Assumed", "On the grip bone, no offset."));
            notices.Add(new("Weapon animation", "Not exported", "The weapon follows the hand; its own clips, such as the magazine, are left out."));
        }
        notices.Add(new("Hands", "WIP", "No skin colour."));
        var rig = assembly.Rig;
        var rest = RestSkin(assembly);
        if (rest is not null)
            notices.Add(new("Face", "Baked", "Neutral face is in the mesh at bind; actions move the face from it."));
        var skins = assembly.Skins.Concat(assembly.AttachedBones.Keys)
            .Distinct<ModelSkinBinding>(ReferenceEqualityComparer.Instance).ToArray();
        if (skins.Any(skin => skin.PaletteKind == SkinPaletteKind.ClothInput))
            notices.Add(new("Cloth", "Skeletal input", "Rest shape only; no cloth simulation."));
        var meshData = new List<(BundleMesh Info, BundleGeometry Geometry)>();
        var reader = new GameMaterialReader(catalog);
        // One preview material per (material, shader inputs) pair, named v1, v2 and so on.
        var variantNames = new Dictionary<(uint Material, string Key), string>();
        var variants = new Dictionary<string, (uint Material, BundleShaderInputs Inputs)>();
        var labels = new Dictionary<uint, string>();
        var objectNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instance in assembly.Scene.Instances)
        {
            token.ThrowIfCancellationRequested();
            var mesh = instance.Mesh;
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) continue;
            string id = $"mesh_{mesh.TagHash:X8}_{mesh.PartIndex:X}_{meshData.Count}";
            var skin = skins.FirstOrDefault(value => value.ModelTag == mesh.TagHash && value.PartOffsets.Contains(mesh.PartIndex));
            MeshSkin? lanes = null; string? failure = null;
            try { lanes = skin is null ? null : AssemblyLanes(assembly, skin, mesh, rest); }
            catch (Exception error) when (Recoverable(error)) { failure = error.Message; }
            if (lanes is null)
            {
                string reason = failure ?? (skin is null ? "No skin on the rig." : "Vertex order lost; skin not written.");
                notices.Add(new("Skin", "Bind pose", reason, mesh.TagHash));
            }
            var written = WriteMesh(catalog, root, instance, id, lanes, reader, notices);
            string label = instance.Name.Split(':')[0];
            labels.TryAdd(mesh.MaterialTag, label);
            objectNames.Add(id, ObjectName(label, mesh, usedNames));
            if (assembly.ShaderInputs?.TryGetValue(instance, out var inputs) == true)
            {
                if (!variantNames.TryGetValue((mesh.MaterialTag, inputs.Key), out string? variant))
                {
                    variantNames.Add((mesh.MaterialTag, inputs.Key), variant = $"v{variantNames.Count + 1}");
                    variants.Add(variant, (mesh.MaterialTag, inputs));
                }
                written.Info = written.Info with { MaterialVariant = variant };
            }
            meshData.Add(written);
        }
        string? scenePath = null;
        var (animations, sampled) = SampleAssemblyClips(catalog, assembly, root, options, notices, token, progress);
        if (meshData.Count != 0)
        {
            scenePath = "scene.gltf";
            BundleSceneWriter.Write(Path.Combine(root, scenePath), meshData, rig, sampled, objectNames);
        }
        var seeds = new HashSet<uint>();
        foreach (var pair in meshData) { seeds.Add(pair.Info.ModelTag); seeds.Add(pair.Info.MaterialTag); }
        var nativeAssets = ExportNative(catalog, root, seeds, options, notices, token, progress);
        var (materials, textures, shaders) = WriteSources(catalog, root, nativeAssets, meshData, notices, token, progress);
        WriteVariants(catalog, root, materials, textures, meshData, variants, notices, token);
        // Blender shows a material by its name, so name it after the first part that uses it.
        for (int index = 0; index < materials.Count; index++)
            if (materials[index].Name is null && labels.TryGetValue(materials[index].TagHash, out string? label))
                materials[index] = materials[index] with { Name = $"{label} {materials[index].TagHash:X8}" };
        notices.Add(new("Materials", "Shader preview", "Dye, lighting and effects are approximate."));
        var manifest = new AssetBundleManifest(Format, 1, assembly.RootTag, assembly.RootTag, assembly.Name, assembly.Context,
            scenePath, meshData.Select(value => value.Info).ToArray(), materials, textures, shaders, animations, nativeAssets, notices);
        BundlePaths.Write(Path.Combine(root, ManifestName), manifest);
        File.WriteAllText(Path.Combine(root, "README.txt"), AssemblyInstructions);
        return manifest;
    }

    // Blender cuts object names longer than 63 UTF-8 bytes.
    private const int MaxObjectNameBytes = 63;

    /**
     * Blender object name: part label, model tag and part offset, such as "Head 80BF8D27 300".
     * Unique within the bundle; the label is cut at a character boundary to fit the byte limit.
     */
    private static string ObjectName(string label, MeshData mesh, HashSet<string> used)
    {
        label = label.Trim();
        if (label.Length == 0) label = "Part";
        for (int copy = 1; ; copy++)
        {
            string suffix = $" {mesh.TagHash:X8} {mesh.PartIndex:X}" + (copy == 1 ? "" : $" #{copy}");
            int budget = MaxObjectNameBytes - System.Text.Encoding.UTF8.GetByteCount(suffix), length = 0;
            foreach (var rune in label.EnumerateRunes())
            {
                if ((budget -= rune.Utf8SequenceLength) < 0) break;
                length += rune.Utf16SequenceLength;
            }
            string name = label[..length].TrimEnd() + suffix;
            if (used.Add(name)) return name;
        }
    }

    /**
     * One skin matrix per bone for the rest binding's bind pose; identity for bones it leaves at bind.
     * @return Null when there is no rest binding or it moves no bone.
     */
    private static Matrix4x4[]? RestSkin(AssetBundleAssembly assembly)
    {
        if (assembly.RestBinding is not { RestBones.Count: > 0 } binding) return null;
        var bind = assembly.Rig.CreateBindPose();
        var pose = assembly.Rig.CreateBindPose();
        binding.ApplyBindPose(pose);
        var result = new Matrix4x4[assembly.Rig.Bones.Count];
        for (int bone = 0; bone < result.Length; bone++)
        {
            var matrix = pose.SkinMatrices[bone];
            // Bind skin matrices are identity only to float error; keep unmoved bones exact.
            result[bone] = Same(matrix, bind.SkinMatrices[bone]) ? Matrix4x4.Identity : matrix;
        }
        return result.All(matrix => matrix.IsIdentity) ? null : result;
    }

    /** The affine parts of two matrices agree to float error. */
    private static bool Same(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return new[] { d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33, d.M41, d.M42, d.M43 }
            .All(value => MathF.Abs(value) < 1e-6f);
    }

    /** Attached skins get one joint at weight 1 on their bone; other skins keep their native lanes, posed by rest. */
    private static MeshSkin? AssemblyLanes(AssetBundleAssembly assembly, ModelSkinBinding skin, MeshData mesh,
        IReadOnlyList<Matrix4x4>? rest)
    {
        int bones = assembly.Rig.Bones.Count;
        if (assembly.AttachedBones.TryGetValue(skin, out int bone))
        {
            if ((uint)bone >= bones) throw new InvalidDataException($"Attachment bone {bone} is outside the rig.");
            var joints = Enumerable.Range(0, mesh.Vertices.Length).Select(_ => new[] { bone, 0, 0, 0 }).ToArray();
            var weights = Enumerable.Range(0, mesh.Vertices.Length).Select(_ => new[] { 1f, 0, 0, 0 }).ToArray();
            return new(joints, weights, assembly.Rig.Bones[bone].BindObject.Matrix);
        }
        var lanes = Lanes(skin, mesh);
        if (lanes is not null && lanes.Joints.Any(value => value.Any(joint => (uint)joint >= bones)))
            throw new InvalidDataException("The skin names a bone outside the rig.");
        return lanes is null || rest is null ? lanes : lanes with { Rest = rest };
    }

    /** Each clip becomes one sampled pose action; assembled bundles carry no native control actions. */
    private static (List<BundleAnimation> Animations, List<(uint Tag, string Name, BundleSceneWriter.SampledClip Clip)> Sampled)
        SampleAssemblyClips(PackageCatalog catalog, AssetBundleAssembly assembly, string root, AssetBundleExportOptions options,
            List<BundleNotice> notices, CancellationToken token, Action<string>? progress)
    {
        var animations = new List<BundleAnimation>();
        var sampled = new List<(uint Tag, string Name, BundleSceneWriter.SampledClip Clip)>();
        if (assembly.ClipTags.Count == 0) return (animations, sampled);
        // The rest binding samples clips as the viewer shows them, with the rest layer on top.
        AnimationRigBinding? binding = assembly.RestBinding;
        string? rigError = null;
        if (options.IncludeAnimations && binding is null)
            try { binding = new AnimationRigReader(catalog).Read(assembly.Rig); }
            catch (Exception error) when (Recoverable(error)) { rigError = error.Message; }
        if (!options.IncludeAnimations) binding = null;
        var rest = RestDelta(assembly);
        var clipNotices = new List<BundleNotice>();
        for (int index = 0; index < assembly.ClipTags.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            uint tag = assembly.ClipTags[index];
            string clipName = assembly.ClipNames?.GetValueOrDefault(tag)
                ?? AssetSceneLoader.ClipName(catalog, tag, index, options.NameResolver);
            string? failure = options.IncludeAnimations ? rigError : "Animation conversion was disabled.";
            if (binding is not null)
                try
                {
                    progress?.Invoke($"Exporting animation {index + 1} of {assembly.ClipTags.Count}");
                    SamplePose(catalog, assembly.Rig, binding, tag, clipName, root, options, clipNotices, sampled, rest, writeFile: false);
                }
                catch (Exception error) when (Recoverable(error)) { failure = error.Message; }
            animations.Add(new(tag, clipName, null, null, null, failure));
            if (failure is not null) notices.Add(new("Animation", "Not exported", failure, tag));
        }
        // One line for all layer clips, not one per clip.
        int layers = clipNotices.Count(value => value.Surface == "Animation base");
        notices.AddRange(clipNotices.Where(value => value.Surface != "Animation base"));
        if (layers == 1) notices.Add(clipNotices.First(value => value.Surface == "Animation base"));
        else if (layers > 1) notices.Add(new("Animation base", "Rig defaults", $"{layers} layer clips use the authored rig pose as their base."));
        return (animations, sampled);
    }

    /**
     * Per bone, the bind object transform times the inverse of its rest object transform, so that
     * delta * posed skins the rest-baked mesh as posed skins the bind mesh. Null without a rest binding.
     */
    private static Matrix4x4[]? RestDelta(AssetBundleAssembly assembly)
    {
        if (assembly.RestBinding is not { RestBones.Count: > 0 } binding) return null;
        var pose = assembly.Rig.CreateBindPose();
        binding.ApplyBindPose(pose);
        var result = new Matrix4x4[assembly.Rig.Bones.Count];
        for (int bone = 0; bone < result.Length; bone++)
        {
            if (!Matrix4x4.Invert(pose.ObjectTransforms[bone], out var inverse))
                throw new InvalidDataException($"Rest transform of bone {bone} is singular.");
            var value = assembly.Rig.Bones[bone].BindObject.Matrix * inverse;
            // Bones the rest pose leaves alone differ from identity only by float error; keep their frames exact.
            result[bone] = Same(value, Matrix4x4.Identity) ? Matrix4x4.Identity : value;
        }
        return result;
    }

    /**
     * Moves each frame onto a rest-baked mesh: every bone's object transform is multiplied by its rest delta,
     * then turned back into local transforms.
     */
    private static SampledAnimation RelativeToRest(SkeletonRig rig, SampledAnimation animation, IReadOnlyList<Matrix4x4> delta)
    {
        var frames = animation.Frames.Select(frame =>
        {
            var objects = new Matrix4x4?[rig.Bones.Count];
            Matrix4x4 Object(int bone)
            {
                if (objects[bone] is { } known) return known;
                int parent = rig.Bones[bone].ParentIndex;
                var value = parent < 0 ? frame.LocalTransforms[bone].Matrix : frame.LocalTransforms[bone].Matrix * Object(parent);
                objects[bone] = value;
                return value;
            }
            var moved = Enumerable.Range(0, rig.Bones.Count).Select(bone => delta[bone] * Object(bone)).ToArray();
            var local = moved.Select((value, bone) =>
            {
                int parent = rig.Bones[bone].ParentIndex;
                if (delta[bone].IsIdentity && (parent < 0 || delta[parent].IsIdentity)) return frame.LocalTransforms[bone];
                if (parent < 0) return BoneTransform.FromMatrix(value);
                if (!Matrix4x4.Invert(moved[parent], out var inverse)) throw new InvalidDataException("Posed parent transform is singular.");
                return BoneTransform.FromMatrix(value * inverse);
            }).ToArray();
            return frame with { LocalTransforms = local };
        }).ToArray();
        return animation with { Frames = frames };
    }

    /**
     * Samples one clip on the rig, writes its pose glTF and adds it to sampled.
     * @param rest Rest delta of each bone when the rest pose is baked into the mesh; null leaves the frames as sampled.
     * @param writeFile False adds the clip to sampled only; the scene glTF then holds its one copy.
     * @return The bundle path of the pose file, or null when none is written. Throws on an unreadable clip; nothing is added then.
     */
    private static string? SamplePose(PackageCatalog catalog, SkeletonRig rig, AnimationRigBinding binding, uint tag, string clipName,
        string root, AssetBundleExportOptions options, List<BundleNotice> notices,
        List<(uint Tag, string Name, BundleSceneWriter.SampledClip Clip)> sampled, IReadOnlyList<Matrix4x4>? rest = null,
        bool writeFile = true)
    {
        var clip = new AnimationControlReader(catalog).Read(tag);
        var animation = AnimationInterchange.Sample(rig, binding, clip, options.SampleRate, previewLayers: true)
            with { Name = $"{clipName} [{tag:X8}]" };
        if (rest is not null) animation = RelativeToRest(rig, animation, rest);
        if (clip.PoseMode != 0)
            notices.Add(new("Animation base", "Rig defaults", "This layer preview uses the authored rig pose as its base.", clip.TagHash));
        string? posePath = null;
        if (writeFile)
        {
            posePath = $"animations/{tag:X8}.pose.gltf";
            Directory.CreateDirectory(Path.Combine(root, "animations"));
            AnimationInterchange.WriteGltf(BundlePaths.Resolve(root, posePath), animation);
        }
        sampled.Add((tag, clipName, new(animation.Frames.Select(frame => frame.Time).ToArray(),
            animation.Frames.Select(frame => frame.LocalTransforms.ToArray()).ToArray())));
        return posePath;
    }

    /**
     * Adds a preview-only copy of a material for each variant: the part's dye bit, dye constants, draw textures and forced blend.
     * Composed plate atlases are written as PNG mip 0; dye textures as package textures.
     */
    private static void WriteVariants(PackageCatalog catalog, string root, List<BundleMaterial> materials, List<BundleTexture> textures,
        IReadOnlyList<(BundleMesh Info, BundleGeometry Geometry)> meshData,
        IReadOnlyDictionary<string, (uint Material, BundleShaderInputs Inputs)> variants, List<BundleNotice> notices,
        CancellationToken token)
    {
        if (variants.Count == 0) return;
        var tfx = MaterialPreviewAnalyzer.CreateInputs(catalog);
        var formats = new Dictionary<uint, int>();
        var volumeAtlases = new Dictionary<uint, (string Path, string Sha256)?>();
        var bases = materials.ToArray();
        foreach (var (variant, (tag, part)) in variants)
        {
            token.ThrowIfCancellationRequested();
            if (bases.FirstOrDefault(value => value.TagHash == tag) is not { } material) continue;
            try
            {
                var overrides = new List<MaterialPreviewAnalyzer.TextureInput>();
                foreach (var (slot, texture) in part.Textures)
                {
                    if (!formats.TryGetValue(texture, out int format))
                    {
                        if (part.ComposedTextures.TryGetValue(texture, out var composed))
                        {
                            textures.Add(WriteComposed(root, texture, composed));
                            format = composed.Format;
                        }
                        else
                        {
                            format = new TextureDataReader(catalog).Read(texture).Format;
                            if (textures.All(value => value.TagHash != texture)) textures.Add(ExportTexture(catalog, root, texture, notices, token));
                        }
                        formats.Add(texture, format);
                    }
                    overrides.Add(new(slot, texture, format));
                }
                var stages = meshData.Where(value => value.Info.MaterialVariant == variant).Select(value => value.Info.RenderStage).ToArray();
                tfx.Vectors[MaterialPreviewAnalyzer.GearDyeBit] = new Vector4(part.DyeBit);
                var preview = WithVolumeAtlases(catalog, root,
                    MaterialPreviewAnalyzer.Read(catalog, tag, tfx, stages, overrides, part.ConstantBuffers, part.ForcedBlend)
                    with { SlotTextures = overrides.ToDictionary(value => value.Slot, value => value.Tag) }, volumeAtlases, notices);
                materials.Add(material with { ShaderProfile = preview.Mode, Preview = preview, Variant = variant,
                    Name = $"{part.Label} {tag:X8}" + (part.DyeBit != 0 ? " dye 1" : "") });
            }
            catch (Exception error) when (Recoverable(error) || error is IOException)
            { notices.Add(new("Material preview", "Part inputs unavailable", error.Message, tag)); }
        }
    }

    /** A composed plate atlas has no package texture, so only its first mip is written, as PNG. */
    private static BundleTexture WriteComposed(string root, uint tag, TextureData data)
    {
        string path = $"textures/{tag:X8}_m0_s0_z0.png";
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        byte[] png = PngInterchange.Write(BundleTextureCodec.Decode(data));
        File.WriteAllBytes(BundlePaths.Resolve(root, path), png);
        return new(tag, "", "", [new(0, 0, 0, path, BundlePaths.Hash(png))]);
    }

    private const string AssemblyInstructions = """
        Sunrise Studio assembled bundle

        This needs the Sunrise Blender add-on, installed in Blender separately:
        https://github.com/stanuwu/SunriseBlenderPlugin

        In Blender, choose File > Import > Sunrise bundle and open asset.sunrise-bundle.json.
        This bundle is view only. Studio cannot import it back.

        Keep this working bundle outside Sunrise projects and the game installation.
        It contains original package data.
        """;
}
