using System.Buffers.Binary;
using SunriseStudio.Animation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Builds synthetic packages for asset connection tests (related models, component links) so verified animation owners and navigation contexts can be checked. */
internal static class AssetConnectionFixtures
{
    private const uint First = 0x80A00000;

    /** Two entities share geometry while their animation lists remain distinct. */
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sunrise-connections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "packages"));
        try
        {
            var material = Header(0x400);
            var model = Header(0x80);
            var buffer = new byte[12]; U(buffer, 0, 4);
            var entries = new AuthoredPackageEntry[]
            {
                Structured(0x80809C0F, Entity(First + 3, First + 16, First + 5, First + 7), "Alpha owner"),
                Structured(0x80809C0F, Entity(First + 4, First + 6, First + 8), "Beta owner"),
                Structured(0x808073A5, model, "Shared model"),
                Structured(0x80809C36, ModelConfig(First + 3)), Structured(0x80809C36, ModelConfig(First + 4)),
                Structured(0x80809C36, Config(First + 5, 0x80808545, 0x80808546)),
                Structured(0x80809C36, Config(First + 6, 0x80808545, 0x80808546)),
                Structured(0x80809C36, AnimationConfig(First + 7, First + 9)),
                Structured(0x80809C36, AnimationConfig(First + 8, First + 10)),
                Structured(0x808036F6, Definition(First + 11)), Structured(0x808036F6, Definition(First + 12)),
                Structured(0x80808F49, Header(32), "Alpha clip"), Structured(0x80808F49, Header(32), "Beta clip"),
                Structured(0x808071E8, material),
                new(First + 15, (32 << 9) | (4 << 6), buffer), new(uint.MaxValue, (40 << 9) | (4 << 6), [1, 2, 3, 4]),
                Structured(0x80809C36, ModelConfig(First + 16))
            };
            PackageWriter.WriteNew(Path.Combine(directory, "packages", "w64_connections_0100_0.pkg"), 0x100, entries);
            using var catalog = PackageCatalog.Open(directory);
            var reader = new AssetConnectionReader(catalog);
            var componentModels = reader.Read(First).Related.Where(link => link.Kind == AssetRelationshipKind.Model
                && link.TagHash == First + 2 && link.Role == "Model").ToArray();
            Check(componentModels.Length == 2 && componentModels.Select(link => link.Context!.ConfigTag)
                .ToHashSet().SetEquals([First + 3, First + 16]),
                "Related models merged distinct component navigation contexts.");
            Check(componentModels.All(link => reader.Read(link.TagHash, link.Context).AnimationEntityTag == First),
                "A component model link lost its verified animation owner.");
            var bare = reader.Read(First + 2, ownerTags: [First, First + 1]);
            Check(bare.AnimationEntityTag == 0 && bare.PreviewTag == First + 2 && bare.EntityCandidates.Count == 2,
                "A shared model chose an arbitrary entity owner.");
            var mesh = new MeshInstance { Mesh = new MeshData { TagHash = First + 2 }, ObjectClassTag = First,
                ModelSourceConfigTag = First + 3, PlacementSourceTag = First + 99, PlacementSourceOffset = 144 };
            var context = AssetOpenContext.ForObject(mesh)!;
            var fromScene = reader.Read(First + 2, context, ownerTags: [First + 1, First]);
            Check(fromScene.PreviewTag == First && fromScene.AnimationEntityTag == First
                && fromScene.Related.Any(link => link.TagHash == First + 11 && link.Kind == AssetRelationshipKind.AnimationClip)
                && fromScene.Related.All(link => link.Kind != AssetRelationshipKind.AnimationClip || link.TagHash != First + 12),
                "Scene selection lost its verified entity's animation list.");
            Check(context.PlacementTag == First + 99 && context.PlacementOffset == 144,
                "Scene placement evidence was dropped.");
            var chosen = reader.Read(First + 2, new(EntityTag: First + 1));
            Check(chosen.AssetTag == First + 2 && chosen.AnimationEntityTag == First + 1,
                "Choosing an owner replaced the inspected model or retained the other owner's rig.");
            var wrongConfig = reader.Read(First + 2, context with { ConfigTag = First + 4 }, ownerTags: [First, First + 1]);
            Check(wrongConfig.AnimationEntityTag == 0, "A conflicting scene config was ignored.");
            var clip = reader.Read(First + 11, context);
            Check(clip.SelectedClipTag == First + 11 && clip.PreviewTag == First && clip.AnimationEntityTag == First,
                "Clip navigation lost the entity preview context.");
            Check(reader.Read(First + 12, context).AnimationEntityTag == 0,
                "A clip from a different entity accepted the current rig.");
            var scene = new SceneDocument();
            scene.Instances.Add(new MeshInstance { Mesh = new MeshData { TagHash = First + 2, MaterialTag = First + 13 } });
            Check(reader.Read(First + 2, previewScene: scene).Related.Any(link => link.TagHash == First + 13
                && link.Kind == AssetRelationshipKind.Material), "Preview material edges are missing.");
            var payload = reader.Read(First + 14);
            Check(payload.Related.Single().TagHash == First + 15 && payload.Related[0].Kind == AssetRelationshipKind.Data,
                "Buffer header did not expose its typed payload.");
            Check(reader.Read(First + 15, ownerTags: [First + 14]).Related.Any(link => link.TagHash == First + 14),
                "Payload did not expose its verified header owner.");
            bool cancelled = false;
            try { reader.Read(First, token: new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Related asset discovery ignored cancellation.");
        }
        finally { Directory.Delete(directory, true); }
    }

    /** Scene-to-model navigation must recover the same installed rig and clip as the owning entity. */
    public static void Installed(PackageCatalog catalog)
    {
        const uint guard = 0x80C0FA98, clip = 0x80FE2C08;
        var scene = new AssetSceneLoader(catalog).Load(guard);
        var mesh = scene.Instances.First(item => item.ObjectClassTag == guard && item.Mesh.Native?.Kind == NativeMeshKind.Entity);
        var reader = new AssetConnectionReader(catalog);
        var connections = reader.Read(mesh.Mesh.TagHash, AssetOpenContext.ForObject(mesh), scene);
        Check(connections.AnimationEntityTag == guard && connections.PreviewTag == guard
            && connections.Related.Any(link => link.Kind == AssetRelationshipKind.AnimationClip && link.TagHash == clip),
            "Installed placed model did not recover its linked animation owner.");
        var rig = new SkeletonReader(catalog).Read(connections.AnimationEntityTag);
        var binding = new AnimationRigReader(catalog).Read(rig);
        var animation = new AnimationControlReader(catalog).Read(clip);
        var first = rig.CreateBindPose(); var second = rig.CreateBindPose();
        binding.Apply(animation, 0, first); binding.Apply(animation, .333333f, second);
        Check(!first.SkinMatrices.SequenceEqual(second.SkinMatrices), "Resolved animation does not change the installed pose.");
        var clipNavigation = reader.Read(clip, new(EntityTag: guard));
        Check(clipNavigation.PreviewTag == guard && clipNavigation.SelectedClipTag == clip,
            "Installed clip navigation cannot preview its owning entity.");
        Console.WriteLine($"connections installed=pass entity={guard:X8} model={mesh.Mesh.TagHash:X8} clip={clip:X8} related={connections.Related.Count}");
    }

    private static AuthoredPackageEntry Structured(uint type, byte[] data, string? name = null) => new(type, 8 << 9, data, name);
    private static byte[] Header(int size) { var data = new byte[size]; Q(data, 0, size); return data; }
    private static byte[] Entity(params uint[] configs)
    {
        var data = Header(0x200); Array(data, 0x10, 0x110, configs.Length, 0x80809C04);
        for (int i = 0; i < configs.Length; i++) U(data, 0x120 + i * 12, configs[i]);
        return data;
    }
    private static byte[] Config(uint tag, uint primary, uint secondary)
    {
        var data = Header(0x400); Q(data, 0x10, 0x50 - 0x10); Q(data, 0x18, 0x100 - 0x18);
        U(data, 0x4C, primary); U(data, 0xFC, secondary); U(data, 0x50, tag); U(data, 0x100, tag);
        return data;
    }
    private static byte[] ModelConfig(uint tag)
    { var data = Config(tag, 0x808072B8, 0x808072BD); U(data, 0x2DC, First + 2); return data; }
    private static byte[] AnimationConfig(uint tag, uint definition)
    { var data = Config(tag, 0x808036E2, 0); U(data, 0x120, definition); return data; }
    private static byte[] Definition(uint clip)
    { var data = Header(0x80); Array(data, 8, 0x30, 1, 0x80808F48); U(data, 0x40, clip); return data; }
    private static void Array(byte[] data, int field, int header, int count, uint type)
    { Q(data, field, count); Q(data, field + 8, header - field - 8); U(data, header - 4, 0x80809FBD); Q(data, header, count); U(data, header + 8, type); }
    private static void U(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Q(byte[] data, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset), value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
