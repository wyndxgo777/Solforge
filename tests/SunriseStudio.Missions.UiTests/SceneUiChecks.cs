using SunriseStudio.Editor;
using SunriseStudio.Missions;
using static MissionUi;

/** Draws a campaign step's scene, inputs and output fields; the installed script is never saved. */
internal static class SceneUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(string game, string? capture)
    {
        string script = Path.Combine(game, "bin/x64/Sunrise/scripts/adventure_vod/adventure_vod.lua");
        var catalog = new MissionScriptCatalog(game);
        var refs = catalog.ReadReferences(catalog.Scripts.Single(value => value.Name == "adventure_vod"));
        if (!refs.Any(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.SceneInput)))
        { Console.Error.WriteLine("The installed SDK has no scene tables."); return 1; }
        string original = MissionScriptCatalog.ReadSource(script);
        int phase = 0; string step = "";
        return MissionUi.Run("scene-ui", _ => new StudioOptions(Game: game, Warp: true, Smoke: true, Frames: 24,
            MissionScript: script, Page: "Mission Graph", Capture: capture), (form, page) =>
        {
            if (page.Document is not { } graph) return;
            if (phase == 0)
            {
                Set(page, "references", refs);
                Invoke(page, "Refresh");
                var node = graph.Nodes.Single(value => value.Kind == "steps" && value.Id == "safety");
                step = node.Key;
                Set(page, "groupKey", node.GroupKey);
                Set(page, "selected", node.Key);
                phase++;
            }
            else if (phase < 6) phase++;
            else if (phase == 6)
            {
                string status = (string)Field(page, "status")!;
                var checks = (IReadOnlyList<MissionGraphDiagnostic>)Field(page, "sceneChecks")!;
                string drawn = (string)Field(page, "typedNode")!;
                Check(drawn == step && status.Length == 0 && checks.Count == 0 && !page.IsDirty,
                    "The scene fields did not draw cleanly: " + status);
                phase++;
            }
        }, (form, _) =>
        {
            var page = Page(form);
            Check(phase == 7, $"Mission scene UI stopped at phase {phase} of 7: status={Field(page, "status")}, "
                + $"error={form.Workspace.Error}, document={page.Document is not null}.");
            Check(MissionScriptCatalog.ReadSource(script) == original, "Scene UI checks wrote the installed script.");
        }, () => $"mission_scene_ui step={step} inspector=drawn checks=clean installed_source=unchanged");
    }
}
