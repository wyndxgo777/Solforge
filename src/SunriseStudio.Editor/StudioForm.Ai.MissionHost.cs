using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/**
 * MCP check_mission_script and simulate_mission (SolforgeTools.MissionHost.cs): the Graph panel's Check script and
 * Simulate buttons for AI clients. The script runs off the UI thread in the bundled Lua 5.4 (MissionHost).
 */
public sealed partial class StudioForm
{
    private async Task<JsonObject> AiMissionHostAsync(string tool, JsonObject args, CancellationToken cancel)
    {
        string? mode = null;
        if (tool == "simulate_mission")
        {
            mode = MissionHost.NormalizeMode(McpJson.String(args, "mode"));
            if (mode is null) return AiError($"Unknown mode; use {string.Join(", ", MissionHost.Modes)}.");
        }
        var taken = await OnUi(() => (Game: workspace.Catalog?.GameRoot, Project: workspace.Project?.DirectoryPath,
            Open: missionGraph.ScriptPath, Scripts: settings.ScriptsPath)).WaitAsync(cancel).ConfigureAwait(false);
        if (taken.Game is null) return AiError("Open a Sunrise installation first; the check reads its Lua SDK (bin/x64/Sunrise/sdk/lua).");
        string? scripts = taken.Scripts is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;
        string installed = Path.Combine(taken.Game, "bin", "x64", "Sunrise", "scripts");
        string shown = McpJson.String(args, "path") ?? "";
        string? path = shown.Length == 0 ? taken.Open : AiMissionScriptPath(shown, taken.Project, scripts, installed);
        if (path is null)
            return AiError(shown.Length == 0 ? "Give a path, or open a saved script in the Graph panel."
                : "Give a project path (scripts/x/x.lua), scripts:<path> as list_mission_scripts gives it, or a full path inside the project, the server scripts folder or the installed scripts.");
        if (!File.Exists(path)) return AiError("No such script: " + path);
        string sdk = MissionHost.SdkRootOf(taken.Game);
        MissionSnapshot? mission = mode is null ? await AiMissionAsync(cancel).ConfigureAwait(false) : null;
        Func<string, bool>? known = mission is { } open ? key => open.Find(key) is not null : null;
        try
        {
            var report = await Task.Run(() => mode is null
                    ? MissionHost.Check(path, sdk, mission?.MissionTag, known)
                    : MissionHost.Simulate(path, sdk, mode), cancel)
                .WaitAsync(TimeSpan.FromMinutes(2), cancel).ConfigureAwait(false);
            return AiMissionHostJson(report, McpJson.Bool(args, "trace", true));
        }
        // lua54.dll ships with KeraLua next to Solforge; a broken install says so instead of failing the call.
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return AiError("The bundled Lua 5.4 (lua54.dll) did not load: " + error.Message);
        }
    }

    /** A project path, scripts:<path>, or a full path inside one of the script roots; null when outside them. */
    private static string? AiMissionScriptPath(string shown, string? project, string? scripts, string installed)
    {
        if (!Path.IsPathFullyQualified(shown)) return ProjectFiles.Resolve(project, [], shown, scripts);
        string full = Path.GetFullPath(shown);
        foreach (string? root in new[] { project, scripts, installed })
            if (root is { Length: > 0 } && full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                return full;
        return null;
    }

    private static JsonObject AiMissionHostJson(MissionHostReport report, bool trace)
    {
        static JsonObject Row(MissionHostRecord record) => new() { ["kind"] = record.Kind, ["line"] = record.Line > 0 ? record.Line : null, ["text"] = record.Text };
        var result = new JsonObject
        {
            ["script"] = report.ScriptPath,
            ["module"] = report.Module,
            ["mode"] = report.Mode,
            ["passed"] = report.Passed,
            ["problems"] = new JsonArray(report.Problems.Select(record => (JsonNode)Row(record)).ToArray()),
            ["mocks"] = new JsonArray(report.Mocks.Select(record => (JsonNode)Row(record)).ToArray()),
            ["modules"] = new JsonArray(report.Modules.Select(module => (JsonNode)module).ToArray()),
            ["notes"] = new JsonArray(report.Records.Where(record => record.Kind is "info" or "summary").Select(record => (JsonNode)record.Text).ToArray())
        };
        if (report.Mode != "check")
        {
            result["stalls"] = new JsonArray(report.Stalls.Select(record => (JsonNode)Row(record)).ToArray());
            if (trace)
                result["trace"] = new JsonArray(report.Trace.Select(record => (JsonNode)(record.Kind == "step"
                    ? $"STEP {record.Text} (line {record.Line})" : "  " + record.Text)).ToArray());
        }
        return result;
    }
}
