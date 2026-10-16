using SunriseStudio.Missions;

bool keep = args.Contains("--keep");
string temporary = Path.Combine(Path.GetTempPath(), "sunrise-mission-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    LuaSyntaxChecks.Synthetic();
    ApiNodeChecks.Synthetic();
    ApiNodeChecks.InstalledSchema();
    FlowCreationChecks.Synthetic();
    SceneEventChecks.Synthetic(temporary);
    LegacyMissionChecks.Run(temporary);
    MissionGraphChecks.Synthetic(temporary);
    MissionGraphChecks.EmptyTables();
    InstallChecks.Run(temporary);
    ScriptGraphChecks.Synthetic();
    ScriptControlChecks.Run(temporary);
    TemplateChecks.Synthetic();
    int root = Array.IndexOf(args, "--game-root");
    if (root >= 0)
    {
        string game = args[root + 1];
        var catalog = new MissionScriptCatalog(game);
        var schema = MissionApiSchema.Load(ApiNodeChecks.Fixture);
        // Generated SDK modules exceed the hand-written script cap; every one must still read. Read each once.
        var references = catalog.Scripts.ToDictionary(entry => entry.Name,
            entry => catalog.ReadReferences(entry, schema.MissionTables), StringComparer.Ordinal);
        LuaSyntaxChecks.Installed(game);
        ApiNodeChecks.Installed(game, references);
        ApiNodeChecks.Templates(game);
        ApiNodeChecks.Palette(game);
        FlowCreationChecks.Owned(temporary, game);
        VisualAuthoringChecks.Run(temporary, game);
        SceneEventChecks.Owned(game, references);
        MissionGraphChecks.Installed(catalog, references, temporary);
    }
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
finally
{
    if (keep) Console.WriteLine($"temp_kept {temporary}");
    else if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(temporary, true);
}
