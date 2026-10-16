using System.Numerics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks scene selection through the UI: whole-placement selection, detail level, siblings, tree reveal and multi-object union. */
internal static class SceneSelectionUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Selecting one placement must not change another placement's visible level of detail. */
    internal static void Run(string root)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, 320, 240, true);
        using var viewport = new AssetViewport(renderer) { UseGameShaders = false, ShowGrid = false, ShowCursor = false };
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "selection-settings.json")));
        var scene = new SceneDocument { TagHash = 0x80A00000, Name = "Selection fixture" };
        MeshData[] meshes = [Triangle(2, 0, Vector3.Zero), Triangle(0, 0, Vector3.Zero), Triangle(0, 1, new(2, 0, 0))];
        var first = new SceneObjectPlacement(1, 0, Matrix4x4.Identity);
        var second = new SceneObjectPlacement(1, 1, Matrix4x4.CreateTranslation(8, 0, 0));
        foreach (var placed in new[] { first, second })
            foreach (var mesh in meshes)
                scene.Instances.Add(new() { Mesh = mesh, ObjectPlacement = placed, Transform = placed.WorldTransform,
                    Layer = SceneLayer.Objects, Name = placed == first ? "First placement" : "Second placement" });
        var marker = new SceneMarker { Name = "Placement box", Layer = SceneLayer.Objects, ObjectPlacement = first };
        scene.Markers.Add(marker);
        var world = WorldDocument.FromScene(scene);
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.World))!.SetValue(form.Workspace, world);
        Set("viewport", viewport); Set("inspectionWorld", world);
        viewport.SelectionMode = SceneSelectionMode.Object; viewport.SetScene(scene);
        viewport.SelectionChanged += value => Invoke("OnViewportSelection", value);
        viewport.Visibility.Filter = value => (bool)Invoke("WorldVisibility", value)!;
        Invoke("RebuildSceneTree");
        var coarse = scene.Instances[0];
        Check(viewport.Visibility.DetailLevel == 0 && scene.Instances.Count(viewport.Visibility.IsVisible) == 4,
            "Selection fixture did not start at the detailed level.");
        form.SelectSceneObject(coarse);
        Check(viewport.Visibility.DetailLevel == 0 && scene.Instances.Count(viewport.Visibility.IsVisible) == 4
            && scene.Instances.Skip(3).Count(viewport.Visibility.IsVisible) == 2,
            "Whole-placement selection changed unrelated scene detail.");
        Check(viewport.SelectedMeshParts.Count == 3 && viewport.SelectedMeshParts.All(mesh => ReferenceEquals(mesh.ObjectPlacement, first))
            && form.SelectedTreeNodeId == form.ObjectTree!.Find(coarse)!.Id,
            "Whole-placement selection lost parts, selected a sibling, or missed the tree.");
        // A multi-object selection unions each item's members and box and survives a mode change.
        var other = scene.Instances[3];
        viewport.SetSelectedItems([coarse, other]);
        var members = viewport.Selection.ObjectMembers(coarse).Concat(viewport.Selection.ObjectMembers(other)).ToHashSet();
        var box = viewport.Selection.GroupBounds(coarse);
        var otherBox = viewport.Selection.GroupBounds(other);
        box = box.Include(otherBox.Minimum).Include(otherBox.Maximum);
        Check(viewport.SelectedItems.Count == 2 && members.SetEquals(viewport.SelectedMembers)
            && viewport.SelectedMembers.Count == members.Count && viewport.SelectedBounds == box
            && viewport.SelectedMeshParts.Count == 6, "A multi-object selection did not union its items.");
        viewport.SelectionMode = SceneSelectionMode.MeshPart;
        viewport.SelectionMode = SceneSelectionMode.Object;
        Check(viewport.SelectedItems.Count == 2 && members.SetEquals(viewport.SelectedMembers) && viewport.SelectedBounds == box,
            "A selection mode change dropped part of a multi-object selection.");
        viewport.SetSelection(null);
        viewport.Camera.LookFrom(new(0, 0, 10), Vector3.Zero);
        viewport.SelectAt(new(160, 120), new(320, 240));
        Check(viewport.SelectedObject is not null && form.SelectedTreeNodeId is not null, "Viewport pick did not follow the tree.");
        viewport.SelectAt(new(160, 120), new(320, 240));
        Check(viewport.SelectedObject is null && form.SelectedTreeNodeId is null, "A second viewport click did not deselect.");
        var node = form.ObjectTree!.Find(coarse)!;
        form.SelectSceneObject(coarse); form.SetSceneNodeHidden(node.Id, true);
        Check(viewport.SelectedObject is null && node.Members.All(member => !viewport.Visibility.IsVisible(member))
            && scene.Instances.Skip(3).Count(viewport.Visibility.IsVisible) == 2,
            "Placement hiding retained selection or hid a separate placement.");
        form.SelectSceneObject(coarse);
        viewport.Visibility.HiddenOverlayLayers.Add(SceneLayer.Objects);
        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Objects);
        form.SelectSceneObject(coarse);
        Check(viewport.Visibility.IsVisible(scene.Instances[1]) && !viewport.Visibility.IsVisible(marker),
            "Mesh selection changed its independent placement-box switch.");
        viewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Objects);
        form.SelectSceneObject(marker);
        Check(viewport.Visibility.IsVisible(marker) && !viewport.Visibility.IsVisible(scene.Instances[1]),
            "Marker selection changed its independent content switch.");
        typeof(StudioWorkspace).GetProperty(nameof(StudioWorkspace.World))!.SetValue(form.Workspace, null);
        viewport.SelectionMode = SceneSelectionMode.MeshPart;
        form.SelectSceneObject(coarse);
        Check(viewport.Visibility.DetailLevel == 2 && viewport.Visibility.IsVisible(coarse)
            && viewport.SelectedMeshParts.Count == 1, "Explicit mesh-part selection no longer chooses its LOD.");
        Console.WriteLine("editor_selection object=whole-placement unrelated_lod=unchanged repeat_click=deselected hide=isolated layers=independent mesh_part=explicit");

        object? Invoke(string method, params object?[] values) => typeof(StudioForm).GetMethod(method, Private)!.Invoke(form, values);
        void Set(string field, object value) => typeof(StudioForm).GetField(field, Private)!.SetValue(form, value);
    }

    private static MeshData Triangle(int lod, int part, Vector3 offset) => new()
    {
        TagHash = 0x80A01000, LodLevel = lod, PartIndex = part, Indices = [0, 1, 2],
        Vertices = [new(new Vector3(-1, -1, 0) + offset, Vector3.UnitZ, Vector2.Zero, Vector4.One),
            new(new Vector3(1, -1, 0) + offset, Vector3.UnitZ, Vector2.UnitX, Vector4.One),
            new(new Vector3(0, 1, 0) + offset, Vector3.UnitZ, Vector2.UnitY, Vector4.One)]
    };
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
