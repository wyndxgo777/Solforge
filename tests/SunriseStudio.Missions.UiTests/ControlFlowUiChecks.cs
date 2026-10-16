using System.Collections;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Missions;
using SunriseStudio.Projects;
using static MissionUi;

/** UI check of the control-flow editor: edits to Lua branches and loops keep comments and apply to the script. */
internal static class ControlFlowUiChecks
{
    private const string Source = "-- Keep controller comment\nreturn {on_start = function(context, state)\n"
        + "    local count = 0\n    if state.ready then\n        context:set_variable('count', 1)\n"
        + "    else\n        while count < 2 do count = count + 1 end\n    end\n"
        + "    local holder = {}\n    holder[state.key] = count -- complex target stays Lua\n"
        + "    return count\nend}\n";

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(string game, string? capture)
    {
        string file = "", temporary = "";
        int phase = 0, restoredFrames = 0;
        string edited = "", unsaved = "", conditionKey = "", ownerKey = "";
        return MissionUi.Run("script-ui", root =>
        {
            temporary = root;
            var project = StudioProject.Create(Path.Combine(temporary, "project"), "Lua control flow");
            file = MissionProject.Save(project, "control_flow/control_flow.lua", Source);
            bool unboundRefused = false;
            try { new MissionGraphPage().OpenScript(file, game); }
            catch (InvalidDataException error) when (error.Message.Contains("open project", StringComparison.Ordinal)) { unboundRefused = true; }
            Check(unboundRefused, "Opening a project script without a workspace did not refuse.");
            return new(Game: game, Project: project.ManifestPath, Warp: true, Smoke: true,
                Frames: 32, Page: "Mission Graph", MissionScript: file, Capture: capture);
        }, (form, page) =>
        {
            if (!page.CanSave || page.Document is null) return;
            if (phase == 0)
            {
                // Also runs the pending node Lua checks.
                PendingLuaChecks.Run(form.Workspace, temporary);
                Check(form.EditorLayout.Find(StudioPanelKind.MissionGraph) is not null,
                    "Mission Graph did not open as its own editor area.");
                Set(page, "catalog", new MissionScriptCatalog(temporary));
                page.OpenScript(file, form.Workspace);
                Check(((MissionScriptCatalog)Field(page, "catalog")!).GameRoot
                    == form.Workspace.Catalog!.GameRoot, "Opening a script retained another installation's catalog.");
                var view = MissionScriptGraph.Parse(page.Document.Source, page.Document.Groups);
                var owner = view.Nodes.Single(node => node.Function is not null);
                var condition = view.Body(owner).Nodes.Single(node => node.Kind == MissionScriptNodeKind.If);
                conditionKey = condition.Key; ownerKey = owner.Key;
                Open(page, ownerKey, conditionKey);
                bool changed = Apply(page, conditionKey, [("Condition", "state.enabled")]);
                Check(changed && page.CanUndo && page.Document.Source.Contains("if state.enabled then", StringComparison.Ordinal),
                    "Typed condition action did not change the selected callback.");
                edited = page.Document.Source;
                page.Undo(); Check(page.Document.Source == Source && !page.CanUndo, "Typed input created more than one undo step.");
                page.Redo(); Check(page.Document.Source == edited, "Typed input redo changed another source span.");
                Check(!Apply(page, conditionKey, [("Condition", "true then injected()")]) && page.Document.Source == edited,
                    "Invalid condition action changed source.");
                Set(page, "sourceDraft", edited + "-- pending Lua\n");
                Check(!Apply(page, conditionKey, [("Condition", "false")]) && page.Document.Source == edited
                    && (string)Field(page, "sourceDraft")! == edited + "-- pending Lua\n",
                    "Typed action overwrote or retargeted a pending Lua draft.");
                Set(page, "sourceDraft", edited);
                page.SaveCurrent(form.Workspace);
                Check(File.ReadAllText(file) == edited && !page.IsDirty, "Typed graph save did not preserve the exact Lua source.");
                var model = Invoke(page, "BuildCanvasModel", true)!;
                var nodes = (IEnumerable)model.GetType().GetField("Item1")!.GetValue(model)!;
                int code = 0, plain = 0;
                foreach (var node in nodes)
                {
                    if (node.GetType().GetProperty("Code")!.GetValue(node) is null) plain++; else code++;
                }
                Check(code == 1 && plain >= 7, $"Basic nodes still display full code blocks: code={code}, plain={plain}.");
                Check(Apply(page, conditionKey, [("Condition", "false")]), "Second graph input edit failed.");
                unsaved = page.Document.Source;
                page.OpenScript(file, form.Workspace);
                Check(page.Document.Source == unsaved && page.IsDirty && Field(page, "pendingSwitch") is not null,
                    "Opening with workspace context discarded the current draft.");
                Set(page, "pendingSwitch", null);
                Set(page, "showSwitch", false);
                ImGui.SetWindowFocus("Inspector");
                phase++;
            }
            else if (phase == 1)
            {
                Check(!(bool)Field(form, "missionGraphActive")!, "Inspector focus retained Mission Graph shortcut ownership.");
                Invoke(form, "UndoActive");
                Invoke(form, "SaveActiveDocument");
                Check(page.Document.Source == unsaved && page.IsDirty && File.ReadAllText(file) == edited,
                    "Inspector Undo or Save changed the mission script.");
                ImGui.SetWindowFocus("Mission graph area");
                phase++;
            }
            else if (phase == 2)
            {
                Check((bool)Field(form, "missionGraphActive")!, "Mission Graph focus did not reclaim its document commands.");
                Invoke(form, "UndoActive");
                Check(page.Document.Source == edited, "Graph-focused Undo used another document.");
                Invoke(form, "RedoActive");
                Check(page.Document.Source == unsaved, "Graph-focused Redo used another document.");
                Invoke(form, "SaveActiveDocument");
                edited = unsaved;
                Check(File.ReadAllText(file) == edited && !page.IsDirty, "Graph-focused Save did not save its script.");
                var area = form.EditorLayout.Find(StudioPanelKind.MissionGraph)!;
                form.EditorLayout.SetPanel(area.Id, StudioPanelKind.Empty);
                phase++;
            }
            else if (phase == 3)
            {
                Check(page.Document.Source == edited && page.CanUndo, "Changing an area discarded the graph document or undo.");
                var area = form.EditorLayout.Show(StudioPanelKind.MissionGraph);
                Set(form, "maximizedPanel", area.Id);
                Open(page, ownerKey, conditionKey);
                Set(page, "status", "");
                phase++;
            }
            else if (phase == 4) restoredFrames++;
        }, (form, _) =>
        {
            Check(phase == 4 && restoredFrames >= 2,
                $"Control-flow UI did not render after restoring its area: phase={phase}, frames={restoredFrames}, error={form.Workspace.Error}.");
            Check(File.ReadAllText(file) == edited, "Rendering changed the saved mission source.");
        }, () => $"mission_script_ui modular_area=pass inputs=pass undo=single redo=exact raw_draft=retained focus=pass fallback=local restore=pass capture={capture}");
    }

    private static void Open(MissionGraphPage page, string owner, string selected)
    {
        Invoke(page, "OpenGroup", "body:" + owner);
        Set(page, "selected", selected);
    }

    private static bool Apply(MissionGraphPage page, string key, IReadOnlyList<(string Name, string Value)> inputs) =>
        (bool)Invoke(page, "ApplyScriptInputs", key, inputs)!;
}
