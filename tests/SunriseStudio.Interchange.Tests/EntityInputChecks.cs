using System.Numerics;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** A Hydra exports with the spawn, variant picks and channel values the entity view used. */
internal static class EntityInputChecks
{
    private const uint Hydra = 0x80C0D467, TakenHydra = 0x80BC9431, Model = 0x80F2FD4C;
    // FNV-1 of "type", "major" and "ultra"; then the runtime key and a value its parts name.
    private const uint TypeKey = 0xB10F785D, Major = 0x4A3554B4, Ultra = 0x357DB49D;
    private const uint RuntimeKey = 0x6ED9E9EF, RuntimeLabel = 0x871AC0EA;
    // The body part and the materials it takes at the native default, the first spawn (major) and an ultra pick.
    private const int BodyPart = 0x5F0;
    private const uint NativeBody = 0x81578D7C, MajorBody = 0x81578D7A, UltraBody = 0x81578D7B;
    // Object channel 1 of the Hydra model; it rests at 0, and its shield plates draw only below 1.
    private const uint ShieldChannel = 0x1116DDC1, ShieldMaterial = 0x80F2FD71;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string game, string temporary)
    {
        using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        var spawn = AuthoredSpawnIndex.Build(catalog).For(Hydra)[0].First;
        var major = new SceneLoadOptions { PreviewSpawn = spawn };
        var scene = new AssetSceneLoader(catalog).Load(Hydra, major);
        Check(scene.EntityParameters.Any(p => p.Key == TypeKey && p.Value == Major && p.Values.Contains(Ultra)),
            "The Hydra's first spawn does not read rank major with ultra on offer.");
        var shieldRest = scene.ObjectChannels.Single(c => c.NameHash == ShieldChannel);
        Check(scene.ObjectChannels.Count == 12 && shieldRest.Rest.Settled && shieldRest.Value == Vector4.Zero
            && scene.ObjectChannels.All(c => c.Rest.Settled || c.Value == new Vector4(SceneObjectChannel.ViewerDefault)),
            "The Hydra's shield channel does not rest at 0, or an unsettled channel is not 1.");

        var native = AssetBundle.Export(catalog, Hydra, Path.Combine(temporary, "hydra-native"));
        var spawned = AssetBundle.Export(catalog, Hydra, Path.Combine(temporary, "hydra-major"), new() { Scene = major });
        Check(Body(native.Manifest) == NativeBody && Body(spawned.Manifest) == MajorBody
            && spawned.Manifest.Notices.Any(n => n.Surface == "Entity inputs"), "The export ignored the entity's spawn.");

        // No channel value: the export must take channel 1 at its rest value, 0.
        var ultra = new SceneLoadOptions { PreviewSpawn = spawn, PreviewParameters = new Dictionary<uint, uint> { [TypeKey] = Ultra } };
        var picked = AssetBundle.Export(catalog, Hydra, Path.Combine(temporary, "hydra-ultra"), new() { Scene = ultra });
        var body = picked.Manifest.Materials.Single(m => m.TagHash == UltraBody && m.Variant is null);
        var shield = picked.Manifest.Materials.Single(m => m.TagHash == ShieldMaterial && m.Variant is null);
        Check(Body(picked.Manifest) == UltraBody && body.Preview?.Recipe == "VexDye52", "The ultra pick did not reach the export.");
        Check(shield.Preview?.Program?.Outputs.ContainsKey("BaseColor") == true, "The shield has no colour at channel 1 = 0.");

        var taken = new AssetSceneLoader(catalog).Load(TakenHydra,
            new SceneLoadOptions { PreviewParameters = new Dictionary<uint, uint> { [RuntimeKey] = RuntimeLabel } });
        Check(taken.Instances.All(i => i.InitiallyVisible is not null)
            && taken.EntityParameters.Single(p => p.Key == RuntimeKey) is { Runtime: true, Value: RuntimeLabel },
            "A runtime parameter pick did not resolve the Taken Hydra.");
        PlacedTables(catalog, scene);
        Console.WriteLine($"entity_inputs spawn=major export=matches pick=ultra recipe=VexDye52 shield=colour runtime_pick=resolved placed_tables=shared");
    }

    // EDZ entities on one model config. The first rests at (0, 0), the next two at (1, 0), and the last
    // has its sequence write channel 1 to 1 at spawn, so all its channels rest at 1.
    private const uint SettledEntity = 0x80C1DD61, OpenEntity = 0x80C1DC9A, OtherOpenEntity = 0x80C1DDB2,
        SequenceEntity = 0x80C1DCED, SharedModel = 0x80F2E4CB;

    /** A placed model's table is its rest values; equal tables are one array, and an all-one table is none. */
    private static void PlacedTables(PackageCatalog catalog, SceneDocument opened)
    {
        var tables = new ObjectChannelTables(catalog);
        uint hydraModel = opened.Instances.Select(i => i.ModelSourceConfigTag).Distinct()
            .First(config => config != 0 && tables.Names(Hydra, config).Count != 0);
        var hydra = tables.Table(Hydra, hydraModel);
        Check(hydra is not null && hydra.SequenceEqual(opened.ObjectChannels.OrderBy(c => c.Index).Select(c => c.Rest.Value))
            && tables.Names(Hydra, hydraModel).SequenceEqual(opened.ObjectChannels.OrderBy(c => c.Index).Select(c => c.NameHash)),
            "A placed Hydra's channel table differs from the entity view's rest values.");
        Check(tables.Table(SettledEntity, SharedModel) is [var a, var b] && a == Vector4.Zero && b == Vector4.Zero,
            "Entity 0x80C1DD61 does not rest with both channels at 0.");
        var open = tables.Table(OpenEntity, SharedModel);
        Check(open is [var x, var y] && x == Vector4.One && y == Vector4.Zero
            && ReferenceEquals(open, tables.Table(OtherOpenEntity, SharedModel)),
            "Entities 0x80C1DC9A and 0x80C1DDB2 do not share one (1, 0) table.");
        Check(tables.Table(SequenceEntity, SharedModel) is null && tables.Names(SequenceEntity, SharedModel).Count == 2,
            "The sequence output of 0x80C1DCED did not set its channel 1 to 1.");
        Check(tables.Unread == 0, "A model's channel table did not read.");
        Check(tables.Table(Hydra, Model) is null && tables.Names(Hydra, Model).Count == 0,
            "A tag that is not a model config gave a channel table.");
    }

    private static uint Body(AssetBundleManifest manifest) =>
        manifest.Meshes.First(m => m.ModelTag == Model && m.PartIndex == BodyPart).MaterialTag;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
