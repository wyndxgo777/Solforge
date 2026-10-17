/**
 * File: The editor side of the AI self-service action tools (SolforgeTools.SelfService.cs): settings, mod details, project
 * open/create/close, closing the map or asset, and the Build menu's other commands. Runs on the UI thread after approval.
 */
using System.Text.Json.Nodes;
using Solforge.Mcp;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** StudioForm part: UI-thread side of the AI self-service tools (settings, project, close, build commands). */
public sealed partial class StudioForm
{
    /** Message for a refused switch while the Mission Graph holds an unsaved draft (the user decides; the AI never discards it). */
    private const string AiDraftOpen = "The Mission Graph has an unsaved draft; ask the user to save or discard it first.";

    /** Runs one self-service action; null when the tool is not one of them. */
    private string? RunAiSelfServiceAction(string tool, JsonObject args)
    {
        switch (tool)
        {
            case "set_settings": return AiSetSettings(args);
            case "set_mod_details": return AiSetModDetails(args);
            case "close_map":
                if (workspace.World is null) return "No map is open.";
                if (workspace.IsBusy) return "Solforge is busy (" + workspace.Status + "); try again when get_editor_state says it is idle.";
                workspace.Run(workspace.CloseWorld);
                return workspace.Error.Length > 0 ? workspace.Error : "Closed the map; its object edits stay in the project.";
            case "close_asset":
                if (workspace.Document is null) return "No asset is open.";
                if (workspace.IsBusy) return "Solforge is busy; try again when get_editor_state says it is idle.";
                workspace.Run(workspace.CloseDocument);
                editTarget = null;
                return workspace.Error.Length > 0 ? workspace.Error : "Closed the asset.";
            case "open_project":
            {
                if (workspace.IsBusy) return "Solforge is busy; try again when get_editor_state says it is idle.";
                if (missionGraph.IsDirty) return AiDraftOpen;
                string path = (McpJson.String(args, "path") ?? "").Trim().Trim('"');
                workspace.Run(() => { workspace.OpenProject(path); RememberProject(); });
                return workspace.Error.Length > 0 ? workspace.Error : $"Opened project {workspace.Project?.Name} ({workspace.Project?.DirectoryPath}).";
            }
            case "create_project":
            {
                if (workspace.IsBusy) return "Solforge is busy; try again when get_editor_state says it is idle.";
                if (missionGraph.IsDirty) return AiDraftOpen;
                string name = (McpJson.String(args, "name") ?? "").Trim();
                string folder = (McpJson.String(args, "folder") ?? "").Trim().Trim('"');
                workspace.Run(() =>
                {
                    if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                        throw new IOException("Choose a valid project name.");
                    string path = Path.Combine(Path.GetFullPath(folder), name);
                    EnsureOutsideInstall(path);
                    workspace.CreateProject(path, name);
                    RememberProject();
                });
                return workspace.Error.Length > 0 ? workspace.Error
                    : $"Created and opened project {name} at {workspace.Project?.DirectoryPath}. Set its mod details with set_mod_details.";
            }
            case "close_project":
                if (workspace.Project is null) return "No project is open.";
                if (workspace.IsBusy) return "Solforge is busy; try again when get_editor_state says it is idle.";
                if (missionGraph.IsDirty) return AiDraftOpen;
                workspace.Run(workspace.CloseProject);
                return workspace.Error.Length > 0 ? workspace.Error : "Closed the project.";
            case "build_to_folder":
            {
                if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
                string folder = (McpJson.String(args, "folder") ?? "").Trim().Trim('"');
                workspace.Run(() => workspace.Build(folder));
                return workspace.Error.Length > 0 ? workspace.Error : "Started; poll get_build_status.";
            }
            case "update_running_game":
                if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
                if (workspace.Project?.Mod is null) return "The project has no mod details; set them with set_mod_details first.";
                if (missionGraph.IsDirty) return AiDraftOpen;
                workspace.Run(workspace.UpdateRunningGame);
                return workspace.Error.Length > 0 ? workspace.Error : "Started; poll get_build_status.";
            case "reload_mission_scripts":
                if (workspace.Catalog is null) return "Open the game installation first (set_settings installation).";
                if (missionGraph.IsDirty) return AiDraftOpen;
                missionGraph.ReloadScripts(workspace.Catalog.GameRoot);
                return "Reloaded the Mission Graph scripts and API schema.";
            default:
                return RunAiCommandAction(tool, args);
        }
    }

    /** set_settings: applies each key as its Settings row would, saves once, and says what changed. */
    private string AiSetSettings(JsonObject args)
    {
        // The MCP side validated names, types, ranges and paths before the approve click; recheck paths (they may have moved).
        if (SolforgeTools.SettingsProblem(args) is { } problem) return problem;
        var done = new List<string>();
        bool scriptsChanged = false;
        foreach (var (name, node) in args)
        {
            var value = (JsonValue)node!;
            switch (name)
            {
                case "serverScripts":
                    settings.ScriptsPath = Full(value.GetValue<string>());
                    scriptsChanged = true;
                    done.Add("serverScripts = " + (settings.ScriptsPath.Length > 0 ? settings.ScriptsPath : "(none)"));
                    break;
                case "blender":
                    settings.BlenderPath = Full(value.GetValue<string>());
                    done.Add("blender = " + (settings.BlenderPath.Length > 0 ? settings.BlenderPath : "(found automatically)"));
                    break;
                case "installation":
                {
                    if (workspace.IsBusy) { done.Add("installation not changed: Solforge is busy"); break; }
                    if (missionGraph.IsDirty) { done.Add("installation not changed: " + AiDraftOpen); break; }
                    string root;
                    try { root = StudioSettings.ResolveGameRoot(value.GetValue<string>()); }
                    catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException) { done.Add("installation not changed: " + error.Message); break; }
                    workspace.Run(() => workspace.OpenInstallation(root, settings.KeySource));
                    if (workspace.Error.Length > 0) { done.Add("installation not changed: " + workspace.Error); break; }
                    settings.SunrisePath = root;
                    scriptsChanged = true;
                    done.Add($"installation = {root} (reopening packages; poll get_editor_state until busy is false)");
                    break;
                }
                case "viewportRendering":
                    SetViewportRendering(value.GetValue<bool>());
                    done.Add("viewportRendering = " + settings.ViewportRendering);
                    break;
                case "pauseWhileGameRuns": settings.PauseRenderWhenGameRunning = value.GetValue<bool>(); done.Add(name + " = " + settings.PauseRenderWhenGameRunning); break;
                case "lowMemoryMode": settings.LowMemoryMode = value.GetValue<bool>(); done.Add(name + " = " + settings.LowMemoryMode + " (next map or shader load)"); break;
                case "lowMemoryWithGame": settings.LowMemoryWithGame = value.GetValue<bool>(); done.Add(name + " = " + settings.LowMemoryWithGame); break;
                case "textureBudgetMiB": settings.TextureBudgetMiB = (int)SolforgeTools.SettingNumber(value)!.Value; done.Add(name + " = " + settings.TextureBudgetMiB); break;
                case "textureCacheMiB": settings.TextureCacheMiB = (int)SolforgeTools.SettingNumber(value)!.Value; done.Add(name + " = " + settings.TextureCacheMiB); break;
                case "fpsFocused": settings.FpsFocused = (int)SolforgeTools.SettingNumber(value)!.Value; done.Add(name + " = " + settings.FpsFocused); break;
                case "fpsBackground": settings.FpsBackground = (int)SolforgeTools.SettingNumber(value)!.Value; done.Add(name + " = " + settings.FpsBackground); break;
                case "uiScale":
                    settings.UiScale = (float)SolforgeTools.SettingNumber(value)!.Value;
                    // Fonts rebuild between frames, as when the Interface size slider is let go.
                    pendingDpi = DeviceDpi;
                    done.Add(name + " = " + settings.UiScale);
                    break;
            }
        }
        SaveSettings();
        // The Settings window reseeds its path fields, and the Mission Graph re-reads its lists, so nothing needs a restart.
        preferencesSeeded = false;
        if (scriptsChanged && workspace.Catalog is { } catalog && !missionGraph.IsDirty) missionGraph.ReloadScripts(catalog.GameRoot);
        return "Saved: " + string.Join("; ", done) + ".";

        /** A trimmed absolute path, or "" to clear. */
        static string Full(string text)
        {
            text = text.Trim().Trim('"');
            return text.Length == 0 ? "" : Path.GetFullPath(text);
        }
    }

    /** set_mod_details: merges the given fields into the project's mod details, validates them as the dialog does, and saves. */
    private string AiSetModDetails(JsonObject args)
    {
        if (!workspace.CanEdit) return "No project is open, or Solforge is busy.";
        var project = workspace.Project!;
        var current = project.Mod;
        /** A list argument as trimmed non-blank strings, or the fallback when absent. */
        IReadOnlyList<string>? ListArg(string name, IReadOnlyList<string>? fallback) => args[name] is JsonArray array
            ? (array.Select(item => item?.ToString().Trim() ?? "").Where(item => item.Length > 0).ToArray() is { Length: > 0 } items ? items : null)
            : fallback;
        /** An optional text argument: absent keeps the fallback, blank clears it. */
        string? OptionalArg(string name, string? fallback) => args.ContainsKey(name)
            ? (McpJson.String(args, name) is { } text && text.Trim().Length > 0 ? text.Trim() : null) : fallback;
        IReadOnlyList<ModDependency>? dependencies = current?.Dependencies;
        if (args["dependencies"] is JsonArray rows)
        {
            var parsed = new List<ModDependency>();
            foreach (var row in rows)
            {
                if (row is not JsonObject item || McpJson.String(item, "id") is not { Length: > 0 } id)
                    return "Each dependency needs an id (and minVersion).";
                parsed.Add(new(id.Trim(), (McpJson.String(item, "minVersion") ?? "1.0.0").Trim(),
                    McpJson.String(item, "maxVersion") is { Length: > 0 } max ? max.Trim() : null));
            }
            dependencies = parsed.Count == 0 ? null : parsed;
        }
        var next = new ModMetadata(
            (McpJson.String(args, "id") ?? current?.Id ?? "").Trim(),
            (McpJson.String(args, "version") ?? current?.Version ?? "1.0.0").Trim(),
            (McpJson.String(args, "name") ?? current?.Name ?? project.Name).Trim(),
            OptionalArg("description", current?.Description), ListArg("authors", current?.Authors), OptionalArg("source", current?.Src),
            dependencies, ListArg("incompatible", current?.Incompatible), McpJson.Bool(args, "cheat", current?.Cheat ?? false), current?.Config);
        if (ModManifest.Validate(next) is { Count: > 0 } problems) return "Not saved: " + string.Join(" ", problems);
        workspace.Run(() => workspace.SetModMetadata(next));
        return workspace.Error.Length > 0 ? workspace.Error : $"Saved mod details: {next.Id} {next.Version} \"{next.Name}\". Build and install can run now.";
    }
}
