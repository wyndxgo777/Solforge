using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks that navigating to a part or material uses the owning asset context, not an unrelated world owner context. */
internal static class PartNavigationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    // Owned guard and Hob entities provide different native model/material/config identities.
    private const uint EntityA = 0x80C0FA98, EntityB = 0x80B82FE3;

    /** The material link belongs to the asset part, even while a different world object stays selected. */
    internal static void Run(string game, string temporary)
    {
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(temporary, "part-navigation-settings.json")));
        using var workspace = form.Workspace;
        var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.Catalog))!.SetValue(workspace, catalog);
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.Names))!.SetValue(workspace, StudioNameIndex.Create(catalog));
        var loader = new AssetSceneLoader(catalog);
        var sourceA = loader.Load(EntityA).Instances.First(part => part.Mesh.MaterialTag is not (0 or uint.MaxValue)
            && part.ModelSourceConfigTag != 0);
        var sourceB = loader.Load(EntityB).Instances.First(part => part.Mesh.MaterialTag is not (0 or uint.MaxValue)
            && part.Mesh.MaterialTag != sourceA.Mesh.MaterialTag && part.ModelSourceConfigTag != 0 && part.InitiallyVisible != false);
        var worldPart = Instance(sourceA, 0x80A01000, 0x30);
        var assetPart = Instance(sourceB, 0x80A01001, 0x90);
        var worldScene = new SceneDocument { TagHash = EntityA, Name = "World selection A" };
        worldScene.Instances.Add(worldPart);
        var world = WorldDocument.FromScene(worldScene);
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.World))!.SetValue(workspace, world);
        var assetScene = new SceneDocument { TagHash = EntityB, Name = "Asset part B" };
        assetScene.Instances.Add(assetPart);
        Check(catalog.TryGetEntry(EntityB, out var entry), "Owned asset B is missing.");
        var expected = AssetOpenContext.ForObject(assetPart)!;
        var other = AssetOpenContext.ForObject(worldPart)!;
        Check(expected.EntityTag != other.EntityTag && expected.ModelTag != other.ModelTag
            && expected.ConfigTag != other.ConfigTag && expected.PlacementTag != other.PlacementTag
            && expected.PlacementOffset != other.PlacementOffset, "Fixture contexts do not distinguish the two owners.");
        var document = new StudioDocument(entry, "Asset B", catalog.Read(EntityB),
            new("Entity", "Asset B", [], [], []), assetScene, null, "", OpenContext: expected);
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.Document))!.SetValue(workspace, document);
        typeof(StudioWorkspace).GetMethod("ResetAssetVisits", Private)!.Invoke(workspace, [document]);
        typeof(StudioForm).GetField("selectedObject", Private)!.SetValue(form, worldPart);
        typeof(StudioForm).GetField("selectedMesh", Private)!.SetValue(form, assetPart);
        typeof(StudioForm).GetMethod("OpenPartMaterial", Private)!.Invoke(form, [assetPart]);
        Wait(workspace);
        Check(workspace.Document?.Entry.TagHash == assetPart.Mesh.MaterialTag
            && workspace.Document.OpenContext == expected, "PART material navigation borrowed the unrelated world owner's context.");
        Check(ReferenceEquals(workspace.World, world) && ReferenceEquals(workspace.ActiveScene, worldScene)
            && ReferenceEquals(typeof(StudioForm).GetField("selectedObject", Private)!.GetValue(form), worldPart),
            "PART material navigation replaced the retained world or selection.");
        workspace.NavigateBack(); Wait(workspace);
        Check(workspace.Document?.Entry.TagHash == EntityB && workspace.Document.OpenContext == expected,
            "Back navigation lost the asset part's owner context.");
        workspace.NavigateForward(); Wait(workspace);
        Check(workspace.Document?.Entry.TagHash == assetPart.Mesh.MaterialTag && workspace.Document.OpenContext == expected,
            "Forward navigation restored the world owner's context.");
        Console.WriteLine($"editor_part_navigation world={EntityA:X8} asset={EntityB:X8} material={assetPart.Mesh.MaterialTag:X8} owner=asset_part history=retained world=retained");
    }

    private static MeshInstance Instance(MeshInstance source, uint placement, int offset) => new()
    {
        Mesh = source.Mesh, Name = source.Name, ObjectClassTag = source.ObjectClassTag,
        ModelSourceConfigTag = source.ModelSourceConfigTag, ModelSourceResourceClass = source.ModelSourceResourceClass,
        Transform = source.Transform, PlacementSourceTag = placement, PlacementSourceOffset = offset,
        InitiallyVisible = source.InitiallyVisible
    };
    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(45)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Asset navigation timed out." : workspace.Error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
