using SunriseStudio.Editor;
using SunriseStudio.Missions;
using static MissionUi;

/** A temp install with no mission_api.json, no lib/flow.lua and a damaged module: the page says why and keeps working. */
internal static class SdkProblemUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static int Run(string game, string? capture)
    {
        string empty = "", broken = "", script = "", source = "";
        int phase = 0, drawn = 0;
        return MissionUi.Run("sdk-ui", root =>
        {
            empty = TestInstall.Write(Path.Combine(root, "empty-game"), scripts: false);
            broken = TestInstall.Write(Path.Combine(root, "game"));
            script = TestInstall.ScriptFile(broken);
            source = File.ReadAllText(script);
            return new StudioOptions(Game: game, Warp: true, Smoke: true, Frames: 24, Page: "Mission Graph", Capture: capture);
        }, (form, page) =>
        {
            Check(((string)Field(page, "status")!).Length == 0 && form.Workspace.Error.Length == 0,
                $"The page failed at phase {phase}: {Field(page, "status")} {form.Workspace.Error}");
            if (phase == 0)
            {
                // The page keeps a catalog it did not build while the installation root is the one it tried.
                Set(page, "catalog", new MissionScriptCatalog(empty));
                Set(page, "catalogTried", form.Workspace.Catalog!.GameRoot);
                phase++;
            }
            else if (phase == 1)
            {
                var catalog = (MissionScriptCatalog)Field(page, "catalog")!;
                // The Installed combo then reads "No scripts folder in this installation".
                Check(catalog.GameRoot == Path.GetFullPath(empty) && page.Document is null && catalog.Scripts.Count == 0
                    && !Directory.Exists(catalog.ScriptsRoot), "The empty installation listed scripts or has a scripts folder.");
                Set(page, "catalog", new MissionScriptCatalog(broken));
                page.OpenScript(script, broken);
                phase++;
            }
            else if (phase == 2)
            {
                Check(page.Document is { } graph && page.IsInstalledScript && page.CanEdit && graph.Diagnostics.Count == 0,
                    "The script did not open as an editable installed script.");
                // The toolbar draws the warning icon, not a button, when a script is open without a schema.
                Check(Field(page, "apiSchema") is null
                    && (string)Field(page, "apiStatus")! == "mission_api.json is missing. Start the game once, then reload.",
                    $"The missing schema did not reach the toolbar: \"{Field(page, "apiStatus")}\".");
                var view = (MissionScriptGraph)Field(page, "scriptGraph")!;
                var calls = (IReadOnlyList<string>)Invoke(page, "ApiChecks")!;
                string sdk = (string)page.GetType().GetProperty("SdkProblem", Access)!.GetValue(page)!;
                var scenes = (IReadOnlyList<MissionGraphDiagnostic>)Field(page, "sceneChecks")!;
                Check(view.Api is null && calls.Count == 0 && sdk.Length == 0 && scenes.Count == 0 && ((string)Field(page, "scriptError")!).Length == 0,
                    $"The clean script showed problems: sdk=\"{sdk}\", calls={calls.Count}, scenes={scenes.Count}.");
                // No problems and no schema: the Checks tab reads "No problems found; API calls not checked".
                Set(page, "showNewScript", true);
                phase++;
            }
            else if (phase == 3)
            {
                Check(!(bool)Field(page, "showNewScript")!, "The New script popup did not open.");
                Check((string)Field(page, "newFlowProblem")! == "scripts/lib/flow.lua is missing. Update the mission scripts.",
                    $"The popup did not say lib/flow.lua is missing: \"{Field(page, "newFlowProblem")}\".");
                // Picks the damaged activity as the popup's combo does.
                var catalog = (MissionScriptCatalog)Field(page, "catalog")!;
                var activity = catalog.Activities.Single(value => value.Key == TestInstall.DamagedKey);
                var entry = catalog.EntryForActivity(activity);
                var states = catalog.ReadReferences(entry, catalog.Api?.MissionTables)
                    .Where(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.State)).ToArray();
                Set(page, "newStates", states); Set(page, "newActivity", activity); Set(page, "newModule", entry.SdkModule);
                Set(page, "newGraphKey", entry.Name);
                Check(states.Length > 0 && catalog.SdkProblem(entry.SdkModule)
                    .StartsWith($"SDK file {TestInstall.DamagedModule} is damaged (", StringComparison.Ordinal),
                    $"The popup did not name the damaged module: \"{catalog.SdkProblem(entry.SdkModule)}\".");
                phase++;
            }
            else if (phase == 4 && ++drawn >= 3) phase++;
        }, (form, _) =>
        {
            Check(phase == 5, $"The SDK problem checks stopped at phase {phase} of 5: error={form.Workspace.Error}.");
            Check(File.ReadAllText(script) == source, "The SDK problem checks wrote the script.");
        }, () => $"mission_sdk_ui empty_list=no_scripts_folder schema=missing_status checks=no_problems_api_unchecked new_script=flow_missing,module_damaged capture={capture}");
    }
}
