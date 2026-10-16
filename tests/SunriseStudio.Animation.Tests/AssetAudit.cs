using System.Buffers.Binary;
using System.Text.Json;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Diagnostic tool, not a pass/fail test: decodes and previews every requested clip tag from an installation and writes a JSON audit so broken clips can be found by package identity. */
internal static class AssetAudit
{
    /** Each clip keeps its decode and editor-preview failure beside its package identity. */
    internal static void Run(string game, string output, string[] tags)
    {
        using var catalog = PackageCatalog.Open(game);
        var results = new List<object>();
        foreach (string text in tags)
        {
            uint tag = Convert.ToUInt32(text, 16);
            if (!catalog.TryGetEntry(tag, out var entry))
            {
                results.Add(new { tag = text, error = "Package entry not found" });
                continue;
            }
            var errors = new List<string>();
            var connections = new AssetConnectionReader(catalog).Read(tag);
            var configs = new List<object>();
            if (entry.Reference == 0x80809C0F)
            {
                var data = new AnimationControlReader.Data(catalog.Read(tag));
                foreach (int row in data.Rows(0x10, 0x80809C04, 12))
                {
                    uint config = data.U32(row);
                    if (!catalog.TryGetEntry(config, out var configEntry)) continue;
                    byte[] bytes = catalog.Read(config);
                    string Resource(int field)
                    {
                        if (bytes.Length < field + 8) return "";
                        long pointer = field + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
                        return pointer < 4 || pointer > bytes.Length ? "" :
                            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)pointer - 4)).ToString("X8");
                    }
                    configs.Add(new { tag = $"{config:X8}", type = $"{configEntry.Reference:X8}",
                        primary = Resource(0x10), secondary = Resource(0x18) });
                }
            }
            SkeletonRig? rig = null;
            AnimationRigBinding? binding = null;
            if (connections.AnimationEntityTag != 0)
            {
                try { rig = new SkeletonReader(catalog).Read(connections.AnimationEntityTag); }
                catch (Exception error) { errors.Add("Skeleton: " + error.Message); }
                if (rig is not null)
                    try { binding = new AnimationRigReader(catalog).Read(rig); }
                    catch (Exception error) { errors.Add("Binding: " + error.Message); }
            }
            var clips = new List<object>();
            if (connections.AnimationEntityTag != 0)
                foreach (var reference in new EntityAnimationCatalog(catalog).Read(connections.AnimationEntityTag)
                    .DistinctBy(row => row.ClipTag))
                {
                    AnimationControlClip? clip = null;
                    string? failure = null;
                    try
                    {
                        clip = new AnimationControlReader(catalog).Read(reference.ClipTag);
                        if (rig is not null && binding is not null) binding.ApplyPreview(clip, clip.DurationSeconds / 2, rig.CreateBindPose());
                    }
                    catch (Exception error) { failure = error.Message; }
                    clips.Add(new { tag = $"{reference.ClipTag:X8}", name = catalog.GetName(reference.ClipTag),
                        frames = clip?.FrameCount, mode = clip?.PoseMode, controls = clip?.ControlCount,
                        groups = clip?.ControlGroups, failure });
                }
            object[] meshes = [];
            try
            {
                var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(connections.PreviewTag);
                meshes = scene.Instances.Select(instance => (object)new
                {
                    model = $"{instance.Mesh.TagHash:X8}", instance.Mesh.PartIndex,
                    material = $"{instance.Mesh.MaterialTag:X8}", instance.Mesh.LodLevel,
                    vertices = instance.Mesh.Vertices.Length, native = instance.Mesh.Native?.InputElements,
                    declaration = instance.Mesh.Native?.NativeDeclarationId,
                    verified = instance.Mesh.Native?.InputLayoutVerified
                }).ToArray();
            }
            catch (Exception error) { errors.Add("Scene: " + error.Message); }
            results.Add(new { tag = text, type = $"{entry.Reference:X8}", name = catalog.GetName(tag),
                preview = $"{connections.PreviewTag:X8}", animationOwner = $"{connections.AnimationEntityTag:X8}",
                configs, bones = rig?.Bones.Count, ik = binding?.IkControlIndices, clips, meshes, errors });
            Console.WriteLine($"asset_audit tag={tag:X8} clips={clips.Count} meshes={meshes.Length} errors={errors.Count}");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
