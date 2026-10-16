/**
 * File: MCP action tools that do what the user would otherwise click for the AI: write Settings (Paths, rendering, interface),
 * set the project's mod details, open, create or close projects, close the open map or asset, and the Build menu's other
 * commands. Each waits for the same approve click as every other action tool.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Partial: self-service tools for settings, mod details and project setup. */
public static partial class SolforgeTools
{
    /** set_settings keys: name, JSON type, what it is. get_settings returns the same names. */
    public static readonly (string Name, string Type, string Description)[] WritableSettings =
    [
        ("serverScripts", "string", "Settings > Paths > Server mission scripts: an existing folder, e.g. ...\\Destiny-Sol-main\\mission-scripts; \"\" clears"),
        ("blender", "string", "Settings > Paths > Blender: an existing blender.exe; \"\" clears (the playbooks then look for it)"),
        ("installation", "string", "Game install folder (File > Sunrise location): reopens the packages; the open project stays"),
        ("viewportRendering", "boolean", "3D viewport rendering"),
        ("pauseWhileGameRuns", "boolean", "Pause rendering while Destiny 2 runs"),
        ("lowMemoryMode", "boolean", "Low memory mode (16 GB PCs); applies to the next map or shader load"),
        ("lowMemoryWithGame", "boolean", "Low memory mode while Destiny 2 runs or Live is attached"),
        ("textureBudgetMiB", "integer", "Texture budget in MiB while Low memory mode is on (64-2048)"),
        ("textureCacheMiB", "integer", "Reload texture cache in MiB (0-2048)"),
        ("fpsFocused", "integer", "FPS cap while focused (0 = no cap, up to 240)"),
        ("fpsBackground", "integer", "FPS cap in the background (0 = no cap, up to 240)"),
        ("uiScale", "number", "Interface size (0.8-1.5)")
    ];

    /** The ranges set_settings accepts for its numeric keys. */
    private static readonly Dictionary<string, (double Minimum, double Maximum)> SettingRanges = new()
    {
        ["textureBudgetMiB"] = (64, 2048), ["textureCacheMiB"] = (0, 2048), ["fpsFocused"] = (0, 240), ["fpsBackground"] = (0, 240),
        ["uiScale"] = (.8, 1.5)
    };

    /** A JSON number (parsed or built in process) as a double, or null. */
    public static double? SettingNumber(JsonValue value) =>
        value.TryGetValue(out double real) ? real : value.TryGetValue(out int whole) ? whole : value.TryGetValue(out long wide) ? wide
            : value.TryGetValue(out float single) ? single : null;

    /** Error text for a set_settings call, or null when every key is known, typed right and its path exists. */
    public static string? SettingsProblem(JsonObject args)
    {
        var known = WritableSettings.ToDictionary(setting => setting.Name, setting => setting.Type, StringComparer.Ordinal);
        string allowed = string.Join(", ", WritableSettings.Select(setting => setting.Name));
        if (args.Count == 0) return "Give at least one setting: " + allowed + ".";
        foreach (var (name, node) in args)
        {
            if (!known.TryGetValue(name, out string? type))
                return name.StartsWith("ai", StringComparison.OrdinalIgnoreCase) || name is "mcp"
                    ? $"{name} is in Settings > AI, which only the user changes." : $"Unknown setting {name}; writable: {allowed}.";
            if (node is not JsonValue value) return $"{name} needs a {type}.";
            switch (type)
            {
                case "boolean" when !value.TryGetValue(out bool _):
                    return $"{name} needs true or false.";
                case "integer" or "number":
                {
                    if (SettingNumber(value) is not { } number || !double.IsFinite(number) || type == "integer" && number != Math.Floor(number))
                        return $"{name} needs a{(type == "integer" ? "n integer" : " number")}.";
                    var (minimum, maximum) = SettingRanges[name];
                    if (number < minimum || number > maximum) return $"{name} must be {minimum} to {maximum}.";
                    break;
                }
                case "string":
                {
                    if (!value.TryGetValue(out string? text)) return $"{name} needs a path string.";
                    text = text.Trim().Trim('"');
                    if (text.Length == 0)
                    {
                        if (name == "installation") return "installation cannot be blank.";
                        break;
                    }
                    if (!Path.IsPathFullyQualified(text)) return $"{name} needs an absolute path.";
                    if (name == "blender")
                    {
                        if (!File.Exists(text) || !text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return $"No blender .exe at {text}.";
                    }
                    else if (!Directory.Exists(text)) return $"No folder at {text}.";
                    else if (name == "serverScripts" && !Directory.EnumerateFiles(text, "*.lua", SearchOption.AllDirectories).Any())
                        return $"{text} holds no .lua files; give the mission-scripts folder itself.";
                    break;
                }
            }
        }
        return null;
    }

    /** Yields the self-service action tools (settings, mod details, project, close, build commands). */
    private static IEnumerable<McpTool> SelfServiceTools(
        Func<string, string, JsonObject, Func<JsonObject, string?>, Func<JsonObject, string>, McpTool> act)
    {
        /** String argument or empty text. */
        static string Arg(JsonObject args, string name) => McpJson.String(args, name) ?? "";
        /** A path argument that names an existing absolute folder or file, else the error text. */
        static string? NeedPath(JsonObject args, string name, bool file, string what)
        {
            string text = Arg(args, name).Trim().Trim('"');
            if (text.Length == 0) return $"Give {name}: {what}.";
            if (!Path.IsPathFullyQualified(text)) return $"{name} needs an absolute path.";
            return (file ? File.Exists(text) || Directory.Exists(text) : Directory.Exists(text)) ? null : $"Nothing at {text}.";
        }

        var settingsSchema = McpJson.Schema(WritableSettings.Select(setting => (setting.Name, setting.Type, setting.Description, false)).ToArray());
        yield return act("set_settings", "Change Solforge settings (the ones get_settings reads), saved at once: Paths (serverScripts, " +
            "blender, installation), rendering, memory, FPS caps and interface size. Paths must exist; serverScripts must hold .lua " +
            "files and is picked up at once by list_mission_scripts, read_project_file (scripts:<path>), the mission-object Lua " +
            "lookups and the Mission Graph. Settings > AI (engine, MCP, auto-allow) stays the user's.",
            settingsSchema, SettingsProblem,
            args => "Set " + string.Join(", ", args.Select(pair => $"{pair.Key} = {pair.Value?.ToJsonString()}")));
        var modSchema = McpJson.Schema(("id", "string", "Mod id, e.g. sol.my-mod (required when the project has none)", false),
            ("name", "string", "Display name", false), ("version", "string", "Version, e.g. 1.0.0", false),
            ("description", "string", "Description; \"\" clears", false),
            ("authors", "array", "Author names", false), ("source", "string", "Source URL; \"\" clears", false),
            ("cheat", "boolean", "Cheat mod: no progress saves while it is loaded", false),
            ("incompatible", "array", "Incompatible mod ids", false),
            ("dependencies", "array", "Dependencies as {id, minVersion, maxVersion?}; replaces the list", false));
        var modProperties = (JsonObject)modSchema["properties"]!;
        modProperties["authors"]!["items"] = new JsonObject { ["type"] = "string" };
        modProperties["incompatible"]!["items"] = new JsonObject { ["type"] = "string" };
        modProperties["dependencies"]!["items"] = new JsonObject { ["type"] = "object" };
        yield return act("set_mod_details", "Set the open project's mod details (Build > Mod details), which Build and install, Update " +
            "running game and .solmod packing need. Omitted fields keep their current values (new projects start from the project " +
            "name and version 1.0.0). id: lowercase, at least two dot-separated parts, e.g. sol.my-mod. Config entries are kept.",
            modSchema,
            args => args.Count == 0 ? "Give at least one mod detail." : args["authors"] is not null and not JsonArray || args["incompatible"] is not null and not JsonArray
                || args["dependencies"] is not null and not JsonArray ? "authors, incompatible and dependencies are arrays." : null,
            args => "Set mod details: " + string.Join(", ", args.Select(pair => $"{pair.Key} = {pair.Value?.ToJsonString()}")));
        yield return act("close_map", "Close the open map (File > Close world); its object edits stay saved in the project. Also " +
            "closes the open asset.", McpJson.Schema(), _ => null, _ => "Close the open map");
        yield return act("close_asset", "Close the open asset (item, vendor, material, any tag) in the inspector; the map stays open.",
            McpJson.Schema(), _ => null, _ => "Close the open asset");
        yield return act("open_project", "Open a Solforge project (its folder or sunrise.project.json); refuses while a mission draft " +
            "is unsaved. Remembered in File > Recent projects.",
            McpJson.Schema(("path", "string", "Absolute project folder or sunrise.project.json", true)),
            args => NeedPath(args, "path", file: true, "the project folder or its sunrise.project.json"), args => $"Open project {Arg(args, "path")}");
        yield return act("create_project", "Create a new empty project (File > New project) as <folder>\\<name> and open it. The folder " +
            "must exist and sit outside the game install. Set its mod details next with set_mod_details.",
            McpJson.Schema(("folder", "string", "Absolute parent folder", true), ("name", "string", "Project name (a valid folder name)", true)),
            args => NeedPath(args, "folder", file: false, "the parent folder") ?? (Arg(args, "name").Trim() is { Length: > 0 } name
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not ".." ? null : "Give a valid project name."),
            args => $"Create project {Arg(args, "name")} in {Arg(args, "folder")}");
        yield return act("close_project", "Close the open project (File > Close project); refuses while a mission draft is unsaved.",
            McpJson.Schema(), _ => null, _ => "Close the open project");
        yield return act("build_to_folder", "Build the project's packages into a folder of your choice (Build > Build to folder). Poll " +
            "get_build_status for the result.",
            McpJson.Schema(("folder", "string", "Absolute output folder (it must exist)", true)),
            args => NeedPath(args, "folder", file: false, "the output folder"), args => $"Build the project packages to {Arg(args, "folder")}");
        yield return act("update_running_game", "Build > Update running game: replace the changed mission scripts in the installed mod and " +
            "reload them in the running game. Needs mod details and a saved mission draft.", McpJson.Schema(), _ => null,
            _ => "Update the running game's mod scripts");
        yield return act("reload_mission_scripts", "Re-read the Mission Graph's script lists and API schema (its toolbar Reload), e.g. " +
            "after a git pull or a path change. Refuses while a mission draft is unsaved.", McpJson.Schema(), _ => null,
            _ => "Reload the Mission Graph scripts");
    }
}
