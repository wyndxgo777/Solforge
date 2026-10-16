using System.Reflection;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** Checks the object tree UI: publication of the tree, search, and scene selection tracking its node. */
internal static class TreeUiChecks
{
    /** Both tree panels must follow selection without consuming each other's scroll request. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "tree-install");
        WorldFixture.Create(install);
        var project = StudioProject.Create(Path.Combine(root, "tree-project"), "Tree fixture");
        var added = project.AddAsset("tree.obj", Encoding.UTF8.GetBytes(
            "o Tree\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"));
        string preferences = Path.Combine(root, "tree-settings.json");
        new StudioSettings
        { ShowSceneTree = true, SceneTreeWidth = 220, BrowserWidth = 220, InspectorWidth = 240 }.Save(preferences);
        Exception? failure = null;
        int phase = 0, steadyFrames = 0, scrollFrames = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath,
                    Map: WorldFixture.Base, Mission: WorldFixture.Base + 4, Warp: true,
                    Smoke: true, Frames: 38, SettingsPath: preferences));
                string? selectedId = null, hiddenId = null;
                SceneHierarchyNode[]? stableRows = null;
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    var work = form.Workspace;
                    if (phase == 0)
                    {
                        float scale = Field<float>(form, "scale");
                        form.ClientSize = new(Math.Max(form.ClientSize.Width, (int)Math.Ceiling(1400 * scale)),
                            form.ClientSize.Height);
                        var tree = form.ObjectTree ?? throw new Exception("Object tree was not published.");
                        var marker = work.World!.Scene.Markers.OrderByDescending(x => tree.Find(x)!.Depth).First();
                        Set(form, "hierarchySearch", "text absent from every node");
                        Field<HashSet<string>>(form, "expandedTreeNodes").Clear();
                        Set(form, "focusHierarchy", true);
                        Set(form, "focusWorldScene", true);
                        form.SelectSceneObject(marker);
                        var node = tree.Find(marker)!;
                        selectedId = node.Id;
                        hiddenId = tree.PathTo(node).Single(x => x.Kind == SceneHierarchyKind.Bubble).Id;
                        Require(form.SelectedTreeNodeId == node.Id && Field<string>(form, "hierarchySearch") == "",
                            "Scene selection failed to track its node or clear incompatible search.");
                        var expanded = Field<HashSet<string>>(form, "expandedTreeNodes");
                        Require(tree.PathTo(node).All(x => expanded.Contains(x.Id)),
                            "Scene selection did not expand its complete ancestor path.");
                        phase++;
                    }
                    else if (phase == 1)
                    {
                        var consumed = Field<Dictionary<string, long>>(form, "treeScrollRevisions");
                        long revision = Field<long>(form, "treeSelectionRevision");
                        bool both = consumed.GetValueOrDefault("browser-tree", -1) == revision
                            && consumed.GetValueOrDefault("scene-tree", -1) == revision;
                        if (!both && ++scrollFrames < 4) return;
                        Require(both, $"Tree scroll expected={revision} browser={consumed.GetValueOrDefault("browser-tree", -1)} "
                            + $"scene={consumed.GetValueOrDefault("scene-tree", -1)} width={form.ClientSize.Width} "
                            + $"scale={Field<float>(form, "scale")} rows={Field<SceneHierarchyNode[]>(form, "treeRows").Length}");
                        stableRows = Field<SceneHierarchyNode[]>(form, "treeRows");
                        Require(stableRows.Any(x => x.Id == selectedId), "Selected node is absent from visible tree rows.");
                        phase++;
                    }
                    else if (phase == 2)
                    {
                        Require(ReferenceEquals(stableRows, Field<SceneHierarchyNode[]>(form, "treeRows")),
                            "Unchanged UI rebuilt the complete tree row list every frame.");
                        if (++steadyFrames < 3) return;
                        form.SetSceneNodeHidden(hiddenId!, true);
                        Require(form.SceneViewport!.SelectedObject is null && form.SelectedTreeNodeId is null,
                            "Hiding an ancestor retained an invisible selection.");
                        var tree = form.ObjectTree!;
                        Require(tree.DescendantMembers(tree.Find(hiddenId!)!)
                            .All(source => !tree.IsVisible(source)),
                            "Ancestor hiding left a descendant eligible for rendering.");
                        work.ReloadWorld();
                        phase++;
                    }
                    else if (phase == 3)
                    {
                        var tree = form.ObjectTree!;
                        Require(tree.IsHidden(hiddenId!)
                            && tree.Find(selectedId!) is { Visible: false },
                            "Reload discarded stable branch hide state.");
                        work.IndexNames();
                        phase++;
                    }
                    else if (phase == 4)
                    {
                        var tree = form.ObjectTree!;
                        Require(tree.IsHidden(hiddenId!)
                            && tree.Find(selectedId!) is { Visible: false },
                            "Name-only refresh discarded branch hide state.");
                        work.CloseWorld();
                        phase++;
                    }
                    else if (phase == 5)
                    {
                        Require(form.ObjectTree is null, "Closing a world retained its object tree.");
                        work.SelectAdded(added.Id);
                        phase++;
                    }
                    else if (phase == 6)
                    {
                        var mesh = work.Document!.Scene!.Instances.Single();
                        Require(form.ObjectTree is { HiddenNodeIds.Count: 0 },
                            "World hide choices leaked into another asset.");
                        var visibility = form.SceneViewport!.Visibility;
                        visibility.HiddenOverlayLayers.Add(mesh.Layer);
                        visibility.HiddenGeometryLayers.Add(mesh.Layer);
                        form.SelectSceneObject(mesh);
                        Require(visibility.HiddenOverlayLayers.Contains(mesh.Layer)
                            && !visibility.HiddenGeometryLayers.Contains(mesh.Layer),
                            "Selecting geometry changed its independent overlay visibility choice.");
                        work.OpenMission(WorldFixture.Base, WorldFixture.Base + 4);
                        phase++;
                    }
                    else if (phase == 7)
                    {
                        var tree = form.ObjectTree!;
                        Require(tree.HiddenNodeIds.Count == 0
                            && tree.Find(selectedId!) is { Visible: true },
                            "Opening a new world session restored another document's hide choices.");
                        phase++;
                    }
                };
                Application.Run(form);
                if (form.Failure is { } error) throw new Exception(error);
                Require(phase == 8, "Tree GUI checks did not reach every phase.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Tree GUI checks timed out.");
        if (failure is not null) throw failure;
        Require(StudioSettings.Load(preferences) is { ShowSceneTree: true, SceneTreeWidth: 220 },
            "Object tree visibility or width preference did not survive a GUI session.");
        Console.WriteLine("editor_tree selection=expanded scroll=both-panels cache=stable hide=recursive reload=retained new-document=reset layers=independent");
    }

    private static T Field<T>(StudioForm form, string name) =>
        (T)(typeof(StudioForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)
            ?? throw new Exception($"Tree test field {name} is null."));

    private static void Set<T>(StudioForm form, string name, T value) =>
        typeof(StudioForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
