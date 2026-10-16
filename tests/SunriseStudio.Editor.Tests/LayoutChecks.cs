using System.Text.Json;
using SunriseStudio.Editor;

internal static class LayoutChecks
{
    internal static void Run(string root)
    {
        var layout = StudioLayout.Default();
        var workspace = layout.Find(StudioPanelKind.Workspace)!;
        int inspectorId = layout.Find(StudioPanelKind.Inspector)!.Id;
        layout.SetPanel(workspace.Id, StudioPanelKind.Inspector);
        Check(layout.Find(StudioPanelKind.Workspace)!.Id == inspectorId
            && layout.Find(StudioPanelKind.Inspector)!.Id == workspace.Id, "Area type change lost the displaced editor.");
        var extra = layout.Split(inspectorId, StudioSplitAxis.Columns)!;
        layout.SetPanel(extra.Id, StudioPanelKind.ObjectTree);
        layout.Root.Ratio = .3f;
        string path = Path.Combine(root, "layout-preferences.json");
        new StudioSettings { SunrisePath = "fixture", GameShaders = false, Layout = layout.Save() }.Save(path);
        var settings = StudioSettings.Load(path);
        var restored = StudioLayout.Restore(settings.Layout);
        Check(restored.Save().GetRawText() == layout.Save().GetRawText()
            && settings.SunrisePath == "fixture" && !settings.GameShaders, "Saved layout lost structure or other settings.");
        // A layout saved before Local Server was its own panel restores as it was; Local Server is one Show away.
        var old = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.7,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"Changes","Axis":"Columns","Ratio":0.5}}""").RootElement);
        Check(old.Find(StudioPanelKind.Changes) is not null && old.Find(StudioPanelKind.LocalServer) is null
            && old.Show(StudioPanelKind.LocalServer).Panel == StudioPanelKind.LocalServer, "An old layout did not restore.");
        Check(StudioLayout.Preset(StudioLayout.SolforgePreset).Find(StudioPanelKind.LocalServer) is not null,
            "The default layout has no Local Server panel.");
        foreach (var name in new[] { StudioLayout.SolforgePreset, StudioLayout.AiPreset, StudioLayout.ScenePreset, StudioLayout.ClassicPreset })
        {
            var preset = StudioLayout.Preset(name);
            Check(preset.Find(StudioPanelKind.AI) is not null && preset.Find(StudioPanelKind.LocalServer) is not null,
                $"Preset {name} lacks the AI or Local Server panel.");
        }
        Check(StudioLayout.Preset(StudioLayout.ScenePreset).Find(StudioPanelKind.Changes) is null,
            "The scene preset still docks Project changes in Local Server's slot.");
        // A saved layout with no AI area gets it once on the right; an existing one is left alone.
        var noAi = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"Empty","Axis":"Rows","Ratio":0.7,"First":{"Id":2,"Panel":"Workspace","Axis":"Columns","Ratio":0.5},"Second":{"Id":3,"Panel":"Changes","Axis":"Columns","Ratio":0.5}}""").RootElement);
        Check(noAi.AddAiRight() && noAi.Root.Second!.Panel == StudioPanelKind.AI && noAi.Root.Axis == StudioSplitAxis.Columns
            && noAi.Find(StudioPanelKind.Workspace) is not null && noAi.Find(StudioPanelKind.Changes) is not null, "AI was not docked on the right.");
        Check(!noAi.AddAiRight() && noAi.Panels.Count(node => node.Panel == StudioPanelKind.AI) == 1, "AI was docked twice.");
        Check(new StudioSettings().AiPanelOffered == false && !StudioSettings.Load(path).AiPanelOffered, "The AI offer flag does not start unset.");
        // A panel name this build does not know falls back to the default layout instead of failing.
        var unknown = StudioLayout.Restore(JsonDocument.Parse("""{"Id":1,"Panel":"NoSuchPanel","Axis":"Rows","Ratio":0.5}""").RootElement);
        Check(unknown.Find(StudioPanelKind.Workspace) is not null, "An unknown panel name broke the layout.");
        int survivingId = restored.Find(StudioPanelKind.Workspace)!.Id;
        Check(restored.Close(extra.Id) && restored.Find(survivingId) is not null
            && restored.Panels.Count == 4, "Joining an area discarded its sibling.");
        foreach (var node in restored.Panels.Skip(1).ToArray()) Check(restored.Close(node.Id), "Nested join failed.");
        Check(restored.Root.IsLeaf && !restored.Close(restored.Root.Id), "Closing the last area emptied the window.");
        for (int i = 1; i < StudioLayout.MaximumPanels; i++)
            Check(restored.Split(restored.Panels.Last().Id, StudioSplitAxis.Rows) is not null, "A valid split failed.");
        Check(restored.Split(restored.Panels.Last().Id, StudioSplitAxis.Rows) is null, "Area capacity was not bounded.");
        var min = StudioLayout.MinimumSize(restored.Root);
        Check(float.IsFinite(min.X) && float.IsFinite(min.Y) && min.X > 0 && min.Y > 0, "Nested area bounds are invalid.");
        foreach (string invalid in new[]
        {
            "null", "[]", "{\"Id\":0}", "{\"Id\":1,\"Panel\":\"Missing\"}",
            "{\"Id\":1,\"Ratio\":0}", "{\"Id\":1,\"First\":{\"Id\":2}}",
            "{\"Id\":1,\"First\":{\"Id\":2,\"Panel\":\"Workspace\"},\"Second\":{\"Id\":2}}",
            "{\"Id\":1,\"First\":{\"Id\":2,\"Panel\":\"Workspace\"},\"Second\":{\"Id\":3,\"Panel\":\"Workspace\"}}"
        })
        {
            using var json = JsonDocument.Parse(invalid);
            Check(StudioLayout.Restore(json.RootElement).Panels.Count == 4, "Corrupt layout did not fall back.");
        }
        var reopened = restored.Show(StudioPanelKind.Workspace);
        Check(reopened.Panel == StudioPanelKind.Workspace && restored.Panels.Count <= StudioLayout.MaximumPanels,
            "Showing a hidden editor failed in a full layout.");
        Console.WriteLine("editor_layout persistence=pass swap=pass split=pass join=pass bounds=pass corrupt=pass");
    }

    internal static void Ui(string root, string output)
    {
        string install = Path.Combine(root, "layout-install");
        WorldFixture.Create(install);
        string path = Path.Combine(root, "layout-ui-settings.json");
        var layout = StudioLayout.Default(220, 240, 140);
        var panel = layout.Split(layout.Find(StudioPanelKind.Browser)!.Id, StudioSplitAxis.Rows)!;
        layout.SetPanel(panel.Id, StudioPanelKind.ObjectTree);
        new StudioSettings { Layout = layout.Save(), ShowSceneTree = false }.Save(path);
        Exception? failure = null;
        int phase = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Map: WorldFixture.Base,
                    Warp: true, Smoke: true, Frames: 30, SettingsPath: path, Capture: output));
                form.FrameRendered += () =>
                {
                    if (!form.EditorReady || form.Workspace.IsBusy) return;
                    if (phase == 0)
                    {
                        Check(form.EditorLayout.Panels.Count == 5 && form.SceneViewport!.Scene is not null,
                            "Saved five-area layout did not draw its scene.");
                        var first = form.EditorLayout.Find(StudioPanelKind.Browser)!;
                        form.EditorLayout.SetPanel(first.Id, StudioPanelKind.Inspector);
                        phase++;
                    }
                    else if (phase == 1)
                    {
                        form.EditorLayout.Close(form.EditorLayout.Find(StudioPanelKind.Workspace)!.Id);
                        phase++;
                    }
                    else if (phase == 2)
                    {
                        Check(form.SceneViewport!.Scene is not null, "Closing an area destroyed its loaded scene.");
                        form.ShowAssetPage("Scene");
                        phase++;
                    }
                    else if (phase == 3)
                    {
                        Check(form.EditorLayout.Find(StudioPanelKind.Workspace) is not null,
                            "Opening an asset did not restore the hidden workspace.");
                        form.ResetLayout();
                        phase++;
                    }
                    else if (phase == 4)
                    {
                        Check(form.EditorLayout.Save().GetRawText() == StudioLayout.Preset(StudioLayout.SolforgePreset).Save().GetRawText(),
                            "Reset did not restore the full Solforge preset tree.");
                        var tree = form.EditorLayout.Split(form.EditorLayout.Find(StudioPanelKind.Browser)!.Id, StudioSplitAxis.Rows)!;
                        form.EditorLayout.SetPanel(tree.Id, StudioPanelKind.ObjectTree);
                        phase++;
                    }
                    else if (phase == 5)
                    {
                        CheckTitleBar(form, "default width");
                        // Narrower than the window allows by hand, so the title bar must fold groups into its menu.
                        form.MinimumSize = new System.Drawing.Size(400, 300);
                        form.ClientSize = new System.Drawing.Size(640, form.ClientSize.Height);
                        phase++;
                    }
                    else if (phase == 6)
                    {
                        CheckTitleBar(form, "640 px");
                        Check(form.TitleBarOverflow.Count > 0, "A 640 px title bar moved nothing into its overflow menu.");
                        phase++;
                    }
                };
                System.Windows.Forms.Application.Run(form);
                Check(form.Failure is null && phase == 7, form.Failure ?? "Layout UI did not finish.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(90)), "Layout UI timed out.");
        if (failure is not null) throw new InvalidOperationException("Layout UI failed.", failure);
        Console.WriteLine("editor_layout_ui saved=pass swap=pass close=pass reopen=pass reset=pass titlebar=pass");
    }

    /**
     * Every control of the two bars before the one-row title bar (696fa0c: the menu bar and the toolbar), and the
     * title bar group that now holds it. Each must be on the row or in its overflow menu, never both, never neither.
     */
    private static readonly (string Control, string Group)[] OldBarControls =
    [
        ("File", "File"), ("Edit", "Edit"), ("Assets", "Assets"), ("Build", "Build"), ("View", "View"),
        ("Window", "Window"), ("Window > Layouts", "Window"), ("Window > Reset layout", "Window"), ("Help", "Help"),
        ("navigate-back", "Navigation"), ("navigate-forward", "Navigation"), ("project name", "Project"),
        ("tb-new", "Project"), ("tb-add", "Project"), ("tb-build", "Project"), ("tb-render", "Render"),
        ("tb-shaders", "Shaders"), ("##quality", "Quality"), ("tb-detail", "Detail"), ("##lighting", "Lighting"),
        ("tb-settings", "Settings"), ("window buttons", "WindowButtons"), ("tb-relay", "Relay")
    ];

    private static void CheckTitleBar(StudioForm form, string width)
    {
        var reachable = form.TitleBarReachable;
        foreach (var (control, group) in OldBarControls)
            Check(reachable.Contains(group), $"Title bar at {width}: '{control}' ({group}) is not reachable.");
        Check(form.TitleBarOverflow.Distinct().Count() == form.TitleBarOverflow.Count
            && form.TitleBarOverflow.All(reachable.Contains), $"Title bar at {width}: the overflow menu repeats or invents a group.");
        Check(!form.TitleBarOverflow.Contains("WindowButtons"), $"Title bar at {width}: the window buttons left the row.");
    }

    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
}
