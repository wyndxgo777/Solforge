using System.Numerics;
using System.Reflection;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks the live tree UI: runtime meshes enter the inspector index and selecting them exposes observed state and reveals their node. */
internal static class LiveTreeUiChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "sunrise-live-tree-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install"); WorldFixture.Create(install);
        string settings = Path.Combine(root, "settings.json");
        new StudioSettings { ShowSceneTree = true, BrowserWidth = 220, SceneTreeWidth = 220, InspectorWidth = 280 }.Save(settings);
        int captureAt = Array.IndexOf(args, "--capture-live");
        string? capture = captureAt >= 0 ? args[captureAt + 1] : null;
        try
        {
            using var form = new StudioForm(new(Game: install, Map: WorldFixture.Base, Warp: true,
                Smoke: true, Frames: 30, SettingsPath: settings, Capture: capture));
            int phase = 0, waits = 0; long selectionRevision = 0;
            string original = "", actorId = "", childId = ""; Array? stableRows = null;
            const uint tag = WorldFixture.Base + 5;
            var row = new LivePlacement(0, 0, 0, 0x1234, 7, 0xCAFE, Vector3.Zero,
                Quaternion.Identity, (ulong)Environment.TickCount64, true, tag, true);
            var prototype = new SceneDocument { Name = "Runtime fixture" };
            var mesh = new MeshData { TagHash = tag,
                Vertices = [new(new(-1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
                    new(new(0, 0, 1), -Vector3.UnitY, Vector2.Zero, Vector4.One),
                    new(new(1, 0, -1), -Vector3.UnitY, Vector2.Zero, Vector4.One)], Indices = [0, 1, 2] };
            prototype.Instances.Add(new() { Mesh = mesh });
            prototype.Instances.Add(new() { Mesh = mesh, Transform = Matrix4x4.CreateTranslation(2, 0, 0) });
            form.FrameRendered += () =>
            {
                if (!form.EditorReady || form.Workspace.IsBusy) return;
                var viewport = form.SceneViewport!; var index = Field<LiveInspectionIndex>(form, "liveInspection");
                if (phase == 0)
                {
                    form.ClientSize = new(Math.Max(form.ClientSize.Width, (int)(1600 * Field<float>(form, "scale"))), form.ClientSize.Height);
                    original = JsonSerializer.Serialize(form.Workspace.World!.Scene, new JsonSerializerOptions { IncludeFields = true });
                    viewport.RegisterLivePrototype(new(tag, prototype, null, []));
                    Publish(form, row);
                    viewport.Camera.LookFrom(new(0, -7, 0), Vector3.Zero);
                    Set(form, "focusHierarchy", true); Set(form, "focusWorldScene", true);
                    phase++;
                }
                else if (phase == 1)
                {
                    var live = viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row))!;
                    Check(index.Find(live.Meshes[0]) is not null, "Runtime mesh never entered the inspector index.");
                    Set(form, "hierarchySearch", "unmatched search");
                    Field<HashSet<string>>(form, "expandedTreeNodes").Clear();
                    viewport.Visibility.DetailLevel = 3;
                    var child = index.FindNode(live.Meshes[1], SceneSelectionMode.MeshPart)!;
                    Invoke(form, "SelectLiveTreeNode", child);
                    actorId = index.Find(live.Meshes[0])!.Node.Id; childId = child.Id;
                    Check(viewport.SelectionMode == SceneSelectionMode.Object && viewport.SelectedMeshParts.Count == 2
                        && viewport.Visibility.DetailLevel == 3, "Runtime child selection changed whole-object mode or map LOD.");
                    Check(form.SelectedLiveEntityKey == LiveDynamicBinding.Key(row) && form.SelectedObservedPlacement?.DatumFlags == 0xCAFE
                        && form.SelectedTreeNodeId == actorId && Field<string>(form, "hierarchySearch") == "",
                        "Runtime selection did not expose observed state or reveal its tree node.");
                    selectionRevision = Field<long>(form, "treeSelectionRevision"); phase++;
                }
                else if (phase == 2)
                {
                    var consumed = Field<Dictionary<string, long>>(form, "treeScrollRevisions");
                    bool scrolled = consumed.GetValueOrDefault("browser-tree", -1) == selectionRevision
                        && consumed.GetValueOrDefault("scene-tree", -1) == selectionRevision;
                    if (!scrolled && ++waits < 5) return;
                    Check(scrolled, "Runtime selection did not scroll both tree panels.");
                    stableRows = Field<Array>(form, "displayTreeRows"); phase++;
                }
                else if (phase == 3)
                {
                    Check(ReferenceEquals(stableRows, Field<Array>(form, "displayTreeRows")), "Unchanged live tree rebuilt every frame.");
                    form.SetSceneNodeHidden(childId, true);
                    Check(viewport.SelectedObject is not null, "Hiding one part cleared whole-instance selection.");
                    form.SetSceneNodeHidden(actorId, true);
                    Check(viewport.SelectedObject is null && form.SelectedLiveEntityKey is null, "Hiding runtime parent retained selection.");
                    form.SetSceneNodeHidden(actorId, false);
                    var live = viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row))!;
                    Check(viewport.IsVisible(live.Meshes[0]) && !viewport.IsVisible(live.Meshes[1]), "Runtime hide flags did not reach viewport filtering.");
                    Publish(form, null); phase++;
                }
                else if (phase == 4)
                {
                    Check(index.Objects.Count == 0 && index.Find(actorId) is null && form.SelectedObservedPlacement is null,
                        "Despawn retained stale tree or inspector state.");
                    row = row with { Generation = 8, Time = (ulong)Environment.TickCount64 };
                    Publish(form, row); phase++;
                }
                else if (phase == 5)
                {
                    var live = viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row))!;
                    form.SelectSceneObject(live.Meshes[0]);
                    Check(form.SelectedLiveEntityKey == LiveDynamicBinding.Key(row) && live.Meshes.All(viewport.IsVisible),
                        "Reused generation inherited hiding or could not be selected through the host.");
                    var page = Field<LivePage>(form, "livePage");
                    Set(page, "filteredViewport", viewport);
                    Field<Dictionary<uint, LivePlacement>>(page, "byHandle")[row.Handle] = row;
                    var childMethod = typeof(StudioForm).GetMethod("ObservedChild", Private)!;
                    var matching = (LivePlacement?)childMethod.Invoke(form, [new LiveObjectChild(row.Handle, row.Generation)]);
                    Check(ReferenceEquals(matching, row) && page.SelectRow(matching!)
                        && form.SelectedLiveEntityKey == LiveDynamicBinding.Key(row),
                        "A matching attachment lifetime lost its navigation target.");
                    Check(childMethod.Invoke(form, [new LiveObjectChild(row.Handle, row.Generation - 1)]) is null
                        && childMethod.Invoke(form, [new LiveObjectChild(row.Handle ^ 0x2000, row.Generation)]) is null,
                        "An attachment answer named a different child lifetime.");
                    Check(JsonSerializer.Serialize(form.Workspace.World!.Scene, new JsonSerializerOptions { IncludeFields = true }) == original,
                        "Live inspector mutated authored scene data.");
                    Invoke(form, "OpenObjectAsset", tag);
                    phase++;
                }
                else if (phase == 6)
                {
                    Check(form.Workspace.Document?.Entry.TagHash == tag && form.Workspace.World is not null
                        && form.SceneViewport!.LiveInstanceCount == 1, "Open asset lost runtime or authored world context.");
                    form.SelectSceneObject(viewport.TryGetLiveInstance(LiveDynamicBinding.Key(row))!.Meshes[0]);
                    var page = Field<LivePage>(form, "livePage"); string reports = Path.Combine(root, "reports");
                    typeof(LivePage).GetMethod("SaveReport", Private)!.Invoke(page, [form.Workspace, viewport, reports]);
                    using (var report = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(reports).Single())))
                        Check(report.RootElement.GetProperty("spawned").GetProperty("observed").GetInt32() == 1
                            && report.RootElement.GetProperty("spawned").GetProperty("previews").GetInt32() == 1
                            && report.RootElement.GetProperty("frame").ValueKind == JsonValueKind.Null
                            && report.RootElement.GetProperty("studio").GetProperty("map").GetString() == WorldFixture.Base.ToString("X8"),
                            "Live report did not record the observed state.");
                    phase++;
                }
            };
            Application.Run(form);
            if (form.Failure is { } error) throw new InvalidDataException(error);
            Check(phase == 7, "Runtime tree GUI did not finish its checks.");
            Console.WriteLine("live_tree_ui inspector=observed tree=revealed scroll=both mode=whole-instance lod=unchanged hide=recursive despawn=cleared");
            return 1;
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(root, true);
        }
    }

    private static void Publish(StudioForm form, LivePlacement? row)
    {
        var page = Field<LivePage>(form, "livePage"); var binding = Field<LiveDynamicBinding>(page, "dynamic");
        var actors = Field<Dictionary<string, LivePlacement>>(binding, "actors"); actors.Clear();
        if (row is not null) actors.Add(LiveDynamicBinding.Key(row), row);
        typeof(LiveDynamicBinding).GetProperty("Revision")!.SetValue(binding, binding.Revision + 1);
        typeof(LiveDynamicBinding).GetProperty("Owners")!.SetValue(binding, row is null ? Array.Empty<LivePlacement>() : new[] { row });
        form.SceneViewport!.SetLiveInstances(row is null ? [] : [new(LiveDynamicBinding.Key(row), row.ClassTag,
            Matrix4x4.CreateFromQuaternion(row.Orientation) * Matrix4x4.CreateTranslation(row.Position))]);
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Set(object value, string name, object contents) => value.GetType().GetField(name, Private)!.SetValue(value, contents);
    private static void Invoke(object value, string name, object argument) => value.GetType().GetMethod(name, Private)!.Invoke(value, [argument]);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
