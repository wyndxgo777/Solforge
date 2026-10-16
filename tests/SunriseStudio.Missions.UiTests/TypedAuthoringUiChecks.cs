using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Missions;
using SunriseStudio.Projects;
using static MissionUi;

/** Typed edits, a new flow, project save and legacy recovery; the installed script is never saved. */
internal static class TypedAuthoringUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(string game, string? capture)
    {
        string script = Path.Combine(game, "bin/x64/Sunrise/scripts/mission_lights/mission_lights.lua");
        string original = "", before = "", newAsset = "", root = "";
        StudioProject project = null!;
        int phase = 0;
        return MissionUi.Run("mission-ui", temporary =>
        {
            root = temporary;
            project = StudioProject.Create(Path.Combine(root, "project"), "Mission authoring");
            original = MissionScriptCatalog.ReadSource(script);
            return new(Game: game, Project: project.ManifestPath, Warp: true, Smoke: true, Frames: 24,
                MissionScript: script, Page: "Mission Graph", Capture: capture);
        }, (form, page) =>
        {
            if (phase == 0)
            {
                if (page.Document is not { } graph || !page.CanSave) return;
                var steps = graph.Groups.Single(group => group.Collection == "steps");
                string selected = steps.Nodes[0].Key;
                Set(page, "groupKey", steps.Key);
                Set(page, "selected", selected);
                var references = (IReadOnlyList<MissionSdkReference>)Field(page, "references")!;
                var target = references.Single(value => value.Expression == "mission.Directive.RESTORE_POWER_TO_THE_STATION");
                before = graph.Source;
                Check(page.IsInstalledScript && page.CanEdit && page.ScriptPath == Path.GetFullPath(script),
                    "An installed script did not open as editable in place.");
                var change = (Func<MissionGraphDocument, MissionGraphDocument>)(document =>
                    new MissionVisualAuthoring(document, references).SetReference(selected, "directive", MissionReferenceKind.Directive, target));
                Invoke(page, "Change", change);
                Check(page.IsDirty && page.CanUndo, "Typed edit did not enter mission undo.");
                page.Undo(); Check(page.Document!.Source == before, "Typed edit undo changed source.");
                page.Redo(); Check(page.Document!.Source != before, "Typed edit redo lost the edit.");
                // The installed file is real game data; the check discards instead of saving it.
                page.DiscardDraft();
                phase++;
            }
            else if (phase == 1)
            {
                Check(MissionScriptCatalog.ReadSource(script) == original, "GUI modified the installed mission.");
                var sdk = new MissionScriptCatalog(game);
                var activity = sdk.Activities.Single(value => value.Key == "MISSION_LIGHTS");
                var entry = sdk.EntryForActivity(activity);
                var available = sdk.ReadReferences(entry);
                var initial = available.First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.State));
                page.CreateFlow(form.Workspace, activity, initial, "studio_ui", true);
                Check(page.IsDirty && page.ScriptPath is null && !page.IsInstalledScript, "New mission did not start as an unsaved mod script.");
                string steps = page.Document!.Groups.Single(value => value.Collection == "steps").Key;
                string facts = page.Document.Groups.Single(value => value.Collection == "facts").Key;
                var triggers = available.Where(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.Trigger)).DistinctBy(value => value.Identity).Take(2).ToArray();
                var directive = available.First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.Directive));
                var sensor = available.First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.DirectiveSensor));
                var change = (Func<MissionGraphDocument, MissionGraphDocument>)(document =>
                {
                    var next = new MissionVisualAuthoring(document, available).CreateNode(facts, "left", MissionNodeTemplate.TriggerFact, triggers[0]);
                    next = new MissionVisualAuthoring(next, available).CreateNode(facts, "right", MissionNodeTemplate.TriggerFact, triggers[1]);
                    next = new MissionVisualAuthoring(next, available).CreateNode(steps, "objective", MissionNodeTemplate.FlowObjective, directive, sensor: sensor);
                    next = new MissionVisualAuthoring(next, available).SetCondition(steps + "/objective", "when",
                        new MissionConditionGroup(false, [new MissionFactCondition("left"), new MissionFactCondition("right")]));
                    return next.Connect(steps + "/start", steps + "/objective");
                });
                Invoke(page, "Change", change);
                string applied = page.Document!.Source, draft = applied + "-- preserve unapplied Lua draft\n";
                Set(page, "sourceDraft", draft);
                page.Undo(); Check(page.Document!.Source == applied, "Undo did not retain the applied graph before a Lua draft.");
                page.Redo(); Check(page.Document!.Source == draft, "Redo lost an unapplied Lua draft.");
                page.Undo();
                string branch = applied + "-- new Lua branch\n";
                Set(page, "sourceDraft", branch); page.Redo();
                Check(page.Document!.Source == applied && (string)Field(page, "sourceDraft")! == branch,
                    "Redo discarded a new Lua draft.");
                string invalid = applied + "local unfinished = '";
                Set(page, "sourceDraft", invalid); page.Undo();
                Check(page.Document!.Source == applied && (string)Field(page, "sourceDraft")! == invalid,
                    "Undo discarded an invalid Lua draft.");
                Set(page, "sourceDraft", draft);
                var toggle = (Func<MissionGraphDocument, MissionGraphDocument>)(document => document.SetField(steps + "/objective", "checkpoint", "true"));
                Invoke(page, "Change", toggle);
                Check(page.Document!.Source.EndsWith("-- preserve unapplied Lua draft\n", StringComparison.Ordinal),
                    "A visual edit discarded an unapplied Lua draft.");
                page.SaveCurrent(form.Workspace); newAsset = page.ScriptPath ?? "";
                Check(!page.IsDirty && newAsset == MissionProject.PathFor(project, "mission_lights/mission_lights.lua")
                    && form.Workspace.ProjectRevision > 0, "New graph did not save into the project's scripts folder.");
                Set(page, "groupKey", steps);
                Set(page, "selected", steps + "/objective");
                phase++;
            }
            else if (phase == 2)
            {
                var reopened = StudioProject.Open(project.DirectoryPath);
                var sdk = new MissionScriptCatalog(game);
                var listed = MissionProject.List(reopened).Single();
                var entry = sdk.Describe(listed.Name, listed.RelativePath, listed.FullPath);
                string saved = MissionScriptCatalog.ReadSource(entry.FullPath);
                Check(reopened.AddedAssets.Count == 0 && reopened.Changes.Count == 0,
                    "A mod script was stored as a project asset or delta.");
                Check(saved == page.Document!.Source && sdk.ReadReferences(entry).Count > 100,
                    "New project graph lost its source or SDK references on reopen.");
                string exported = MissionProject.Export(entry.RelativePath, saved, Path.Combine(root, "review"), game);
                Check(File.ReadAllText(exported) == saved && MissionScriptCatalog.ReadSource(script) == original,
                    "New mission export changed source or wrote the installation.");
                phase++;
            }
            else if (phase == 3)
            {
                var currentProject = form.Workspace.Project!;
                var legacy = new LegacyMissionDocument("sunrise-mission-graph", 1, "Recovered mission",
                    "recovered/recovered.lua", original,
                    new Dictionary<string, LegacyMissionPosition> { ["legacy-node"] = new(317, 221) });
                byte[] archive = JsonSerializer.SerializeToUtf8Bytes(legacy);
                var asset = currentProject.AddAsset("Recovered.sunrise-mission.json", archive);
                Invoke(page, "OpenLegacy", asset.Id);
                Check(page.IsDirty && page.CanSave && !page.IsInstalledScript && page.ScriptPath is null
                    && page.Document!.Source == original, "Legacy source did not open as a recoverable project draft.");
                page.LayoutDirectory = Path.Combine(root, "layouts");
                page.SaveCurrent(form.Workspace);
                string recovered = MissionProject.PathFor(currentProject, legacy.RelativePath);
                Check(!page.IsDirty && page.ScriptPath == recovered && MissionScriptCatalog.ReadSource(recovered) == original,
                    "Legacy source did not migrate to a plain project script.");
                Check(currentProject.ReadAddedAsset(asset.Id).SequenceEqual(archive), "Migration changed the legacy archive.");
                page.DiscardDraft(); page.OpenScript(recovered, game);
                Check(page.Document!.Source == original && !page.IsDirty, "Migrated script did not reopen cleanly.");
                var canvas = Field(page, "canvas")!;
                var positions = (Dictionary<string, Vector2>)canvas.GetType().GetProperty("Positions")!.GetValue(canvas)!;
                Check(positions["legacy-node"] == new Vector2(317, 221), "Legacy node positions did not reopen with the migrated script.");
                Check(MissionScriptCatalog.ReadSource(script) == original, "Legacy recovery changed the installed script.");
                phase++;
            }
            else if (phase == 4)
            {
                page.OpenScript(script, game);
                form.Workspace.CloseProject();
                ImGui.SetWindowFocus("Mission graph area");
                phase++;
            }
            else if (phase == 5)
            {
                Check(form.Workspace.Project is null && page.IsInstalledScript && page.CanSave
                    && (bool)typeof(StudioForm).GetProperty("CanSaveActive", Access)!.GetValue(form)!,
                    $"The installed script's Save context was not active: project={form.Workspace.Project is not null}, installed={page.IsInstalledScript}, page_save={page.CanSave}, graph_focus={Field(form, "missionGraphActive")}.");
                Check(MissionScriptCatalog.ReadSource(script) == original, "View-only checks wrote the installed script.");
                phase++;
            }
        }, (form, _) =>
        {
            var page = Page(form);
            Check(phase == 6, $"Mission GUI stopped at phase {phase} of 6: status={Field(page, "status")}, "
                + $"error={form.Workspace.Error}, document={page.Document is not null}, can_save={page.CanSave}.");
        }, () => "mission_gui typed_edit=pass new_flow=pass compound_condition=pass draft=preserved draft_history=pass undo=pass redo=pass save=pass reopen=pass export=pass legacy=pass installed_source=unchanged");
    }
}
