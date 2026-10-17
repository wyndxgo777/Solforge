/**
 * File: MCP tools that reach every editor command without clicking through menus or native file pickers (McpCoverage.md):
 * list_commands and run_command over the EditorCommands catalog, show_panel, set_viewport_options, set_camera, export_asset,
 * import_asset, project_change and arm_file_dialog. Each action waits for the same approve click as every other action tool;
 * file and folder paths replace the native pickers, and the editor still refuses paths inside the install or the project.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Partial: editor command, panel, viewport, camera, import/export and project change tools. */
public static partial class SolforgeTools
{
    /** Shared by the server instructions and get_help: the command tools and what they replace. */
    public const string CommandsHint =
        "Every menu command and toolbar toggle has a stable id: list_commands shows them (with enabled state and the dedicated tool " +
        "to prefer) and run_command runs one, taking path instead of a file picker. show_panel opens any panel or dialog so " +
        "list_ui_controls can see its controls; set_viewport_options (wireframe, grid, shaders, quality, lighting, projection, view " +
        "presets, overlays) and set_camera (eye, look_at, yaw, pitch, distance) drive the 3D view; export_asset and import_asset cover " +
        "the Assets menu; project_change reverts, removes, compares or exports project changes; arm_file_dialog answers the next file " +
        "picker a ui_invoke opens (Browse buttons, page exports). get_help section tools maps each menu to its tool.";

    /** export_asset kinds and the command each runs. */
    public static readonly Dictionary<string, string> ExportKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file"] = "assets.export", ["blender_asset"] = "assets.export_full_blender", ["blender_world"] = "assets.export_world_blender",
        ["blender_material"] = "assets.export_material_blender", ["animation"] = "assets.export_animation",
        ["editable_animation"] = "assets.export_editable_animation", ["collision"] = "assets.export_collision"
    };

    /** import_asset kinds and the command each runs. */
    public static readonly Dictionary<string, string> ImportKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blender_asset"] = "assets.import_blender", ["skinned_model"] = "assets.import_skinned_model",
        ["animation_controls"] = "assets.import_animation_controls", ["collision"] = "assets.import_collision", ["delta"] = "assets.import_delta"
    };

    /** project_change actions. */
    public static readonly string[] ChangeActions = ["revert", "remove", "compare", "export"];

    /** set_viewport_options keys: name, JSON type, what it does. */
    public static readonly (string Name, string Type, string Description)[] ViewportOptions =
    [
        ("wireframe", "boolean", "View > Wireframe"), ("grid", "boolean", "View > Grid"), ("game_shaders", "boolean", "Toolbar > Game shaders"),
        ("full_detail", "boolean", "Toolbar > Full detail"), ("quality", "string", "Toolbar > Quality: " + string.Join(", ", EditorCommands.Quality)),
        ("lighting", "string", "Toolbar > Lighting: " + string.Join(", ", EditorCommands.Lighting)),
        ("projection", "string", "View overlay: perspective or orthographic"),
        ("view", "string", "View overlay preset: " + string.Join(", ", EditorCommands.Views) + " (orthographic, like the numpad keys)"),
        ("axis_gizmo", "boolean", "View overlay > Axis gizmo"), ("statistics", "boolean", "View overlay > Statistics")
    ];

    /** The command tool names. */
    public static readonly string[] CommandToolNames =
        ["list_commands", "run_command", "show_panel", "set_viewport_options", "set_camera", "export_asset", "import_asset", "project_change", "arm_file_dialog"];

    /** Yields the command tools. */
    private static IEnumerable<McpTool> CommandTools(ISolforgeHost host)
    {
        // An approved action: validate, ask, then run as `tool` with the (possibly translated) arguments.
        McpTool Act(string name, string description, JsonObject schema, Func<JsonObject, (string? Problem, string Tool, JsonObject Args)> prepare,
            Func<JsonObject, string> summary) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (host.Paused) return McpToolResult.Error("The user paused context publishing in Solforge's AI workspace.");
                var (problem, tool, translated) = prepare(args);
                if (problem is not null) return McpToolResult.Error(problem);
                if (!await host.ApproveAsync(name, summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                string text = await host.RunActionAsync(tool, translated, cancel).ConfigureAwait(false);
                return text.StartsWith("Refused:", StringComparison.Ordinal) ? McpToolResult.Error(text) : McpToolResult.Text(text);
            });
        static (string?, string, JsonObject) Command(string id, JsonObject args)
        {
            var translated = new JsonObject { ["id"] = id };
            foreach (string key in new[] { "path", "on", "value" })
                if (args[key] is { } node) translated[key] = node.DeepClone();
            return (EditorCommands.Problem(translated), "run_command", translated);
        }
        static string Kinds(IEnumerable<string> names) => string.Join(", ", names);

        yield return new McpTool("list_commands", "Every Solforge menu command, toolbar toggle and panel opener by stable id (file.*, edit.*, " +
            "assets.*, build.*, view.*, toolbar.*, window.*, nav.*, help.*): the menu path, what input run_command needs (path, on, " +
            "value), the dedicated tool to prefer, and whether it is enabled right now. Works while context publishing is paused.",
            McpJson.Schema(("query", "string", "Optional words of the id, menu or description", false)), false, async (args, cancel) =>
            {
                JsonObject? states = null;
                try
                {
                    var answer = await host.QueryAsync("list_commands", new JsonObject(), cancel).ConfigureAwait(false);
                    states = answer["enabled"] as JsonObject;
                }
                catch (Exception exception) when (exception is not OperationCanceledException) { }
                var rows = EditorCommands.ToJson(McpJson.String(args, "query"), states);
                return McpToolResult.Json(new JsonObject { ["total"] = rows.Count, ["commands"] = rows, ["hint"] = CommandsHint });
            });

        yield return Act("run_command", "Run a Solforge menu command by id from list_commands, as if the user picked it. Commands that " +
            "open a file or folder picker take path instead (absolute; exports go outside the project and the install). Toggles take " +
            "on, choices take value. Dialog commands open the dialog; then list_ui_controls and ui_set_value fill it.",
            McpJson.Schema(("id", "string", "Command id, e.g. edit.undo, assets.import_delta, window.layout", true),
                ("path", "string", "File or folder for picker commands", false), ("on", "boolean", "For toggles", false),
                ("value", "string", "For choices (quality, lighting, layout, panel, recent project, snapshot stamp, tag)", false)),
            args => Command(McpJson.String(args, "id") ?? "", args),
            args => $"Run {EditorCommands.Find(McpJson.String(args, "id"))?.Menu ?? McpJson.String(args, "id")}"
                + (McpJson.String(args, "path") is { } path ? " with " + path : "") + (McpJson.String(args, "value") is { } value ? " = " + value : "")
                + (args["on"] is JsonValue on ? " " + (on.TryGetValue(out bool flag) && flag ? "on" : "off") : ""));

        yield return Act("show_panel", "Open or focus a Solforge panel, editor window or dialog so its controls are drawn (then " +
            "list_ui_controls with window narrows to it): " + Kinds(EditorCommands.Panels) + ". The AI tab of Settings stays the user's.",
            McpJson.Schema(("panel", "string", "Panel name: " + Kinds(EditorCommands.Panels), true)),
            args => EditorCommands.Panels.Contains(McpJson.String(args, "panel")?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
                ? (null, "show_panel", args) : ($"Unknown panel; one of {Kinds(EditorCommands.Panels)}.", "show_panel", args),
            args => $"Show {McpJson.String(args, "panel")}");

        yield return Act("set_viewport_options", "Set the 3D view's display options in one call: " +
            string.Join("; ", ViewportOptions.Select(option => option.Name + " (" + option.Description + ")")) + ". viewport picks scene " +
            "(the open map) or asset (the asset preview); default is the one showing. get_editor_state reads rendering state.",
            McpJson.Schema([.. ViewportOptions.Select(option => (option.Name, option.Type, option.Description, false)),
                ("viewport", "string", "scene or asset (default: the visible one)", false)]),
            args => (ViewportOptionsProblem(args), "set_viewport_options", args),
            args => "Viewport: " + string.Join(", ", args.Where(pair => pair.Key != "viewport").Select(pair => $"{pair.Key}={pair.Value}")));

        var camera = McpJson.Schema(("eye", "array", "Camera position [x, y, z] in world units", false),
            ("look_at", "array", "Point to face and orbit around [x, y, z]; alone it keeps the view direction and distance", false),
            ("yaw", "number", "Heading in degrees (0 = +X, 90 = +Y)", false), ("pitch", "number", "Pitch in degrees (-89..89)", false),
            ("distance", "number", "Orbit distance from the pivot", false), ("viewport", "string", "scene or asset (default: the visible one)", false));
        foreach (string key in new[] { "eye", "look_at" })
            camera["properties"]![key]!["items"] = new JsonObject { ["type"] = "number" };
        yield return Act("set_camera", "Move the 3D view's camera: eye + look_at places it, look_at alone re-centres the orbit, " +
            "yaw/pitch turn it, distance zooms. focus_camera frames an object instead; get_context shows where the camera is.",
            camera, args => (CameraProblem(args), "set_camera", args),
            args => "Move the camera" + (args["eye"] is JsonArray eye ? " to " + eye.ToJsonString() : "")
                + (args["look_at"] is JsonArray at ? " facing " + at.ToJsonString() : ""));

        yield return Act("export_asset", "Export from the Assets menu without a file picker. kind: file (the open asset; the path's " +
            "extension picks .dds/.png, .glb/.gltf/.obj, .bin or .json), blender_asset, blender_world, blender_material (path is an " +
            "empty folder), animation, editable_animation (.gltf), collision (.glb/.gltf/.obj). Paths must be outside the project and the install.",
            McpJson.Schema(("kind", "string", "One of " + Kinds(ExportKinds.Keys), true), ("path", "string", "Absolute output file or folder", true)),
            args => ExportKinds.TryGetValue(McpJson.String(args, "kind") ?? "", out string? id) ? Command(id, args)
                : ($"kind must be one of {Kinds(ExportKinds.Keys)}.", "run_command", args),
            args => $"Export {McpJson.String(args, "kind")} to {McpJson.String(args, "path")}");

        yield return Act("import_asset", "Import into the project from the Assets menu without a file picker. kind: blender_asset " +
            "(*.sunrise-bundle.json), skinned_model (.gltf/.glb over the open entity), animation_controls (.gltf/.glb into the selected " +
            "clip), collision (.glb/.gltf), delta (.bin/.dds/.png/.obj/.gltf/.glb/.json over the open asset).",
            McpJson.Schema(("kind", "string", "One of " + Kinds(ImportKinds.Keys), true), ("path", "string", "Absolute path of an existing file", true)),
            args => ImportKinds.TryGetValue(McpJson.String(args, "kind") ?? "", out string? id) ? Command(id, args)
                : ($"kind must be one of {Kinds(ImportKinds.Keys)}.", "run_command", args),
            args => $"Import {McpJson.String(args, "kind")} from {McpJson.String(args, "path")}");

        yield return Act("project_change", "Act on a Project changes row (list_project_changes lists them): revert an asset change " +
            "(tag), remove an added asset (id), compare an asset change with the base game (tag; opens the tag diff that " +
            "get_open_documents then reads), or export an added asset to path. Object edits revert with revert_object_edit.",
            McpJson.Schema(("action", "string", "revert, remove, compare or export", true), ("tag", "string", "Changed tag hash (revert, compare)", false),
                ("id", "string", "Added asset id (remove, export)", false), ("path", "string", "Absolute output file (export)", false)),
            args => (ChangeProblem(args), "project_change", args),
            args => $"{McpJson.String(args, "action")} project change {McpJson.String(args, "tag") ?? McpJson.String(args, "id")}"
                + (McpJson.String(args, "path") is { } path ? " to " + path : ""));

        yield return Act("arm_file_dialog", "Answer the next Solforge file or folder picker with this path instead of showing it (for " +
            "pickers a ui_invoke opens: dialog Browse buttons, audio, weapon and mission page imports and exports). It expires after " +
            "seconds (default 120) or one use. run_command, export_asset and import_asset take path directly instead.",
            McpJson.Schema(("path", "string", "Absolute file or folder path", true), ("seconds", "integer", "How long it waits (10-600, default 120)", false)),
            args => McpJson.String(args, "path") is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                ? (null, "arm_file_dialog", args) : ("Give an absolute path.", "arm_file_dialog", args),
            args => $"Use {McpJson.String(args, "path")} for the next file picker");
    }

    /** Error text for set_viewport_options, or null. */
    public static string? ViewportOptionsProblem(JsonObject args)
    {
        var known = ViewportOptions.ToDictionary(option => option.Name, option => option.Type, StringComparer.Ordinal);
        int count = 0;
        foreach (var (name, node) in args)
        {
            if (name == "viewport")
            {
                if (McpJson.String(args, name) is not ("scene" or "asset")) return "viewport is scene or asset.";
                continue;
            }
            if (!known.TryGetValue(name, out string? type)) return $"Unknown option {name}; options: {string.Join(", ", known.Keys)}.";
            count++;
            if (type == "boolean" && (node is not JsonValue flag || !flag.TryGetValue(out bool _))) return $"{name} needs true or false.";
            string? text = McpJson.String(args, name);
            string[]? choices = name switch
            {
                "quality" => EditorCommands.Quality, "lighting" => EditorCommands.Lighting, "view" => EditorCommands.Views,
                "projection" => ["perspective", "orthographic"], _ => null
            };
            if (choices is not null && !choices.Contains(text?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                return $"{name} must be one of {string.Join(", ", choices)}.";
        }
        return count == 0 ? "Give at least one option: " + string.Join(", ", known.Keys) + "." : null;
    }

    /** A JSON [x, y, z] of finite numbers, or null. */
    public static System.Numerics.Vector3? Vector(JsonNode? node)
    {
        if (node is not JsonArray { Count: 3 } array) return null;
        var values = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (array[i] is not JsonValue value || SettingNumber(value) is not { } number || !double.IsFinite(number)) return null;
            values[i] = (float)number;
        }
        return new(values[0], values[1], values[2]);
    }

    /** Error text for set_camera, or null. */
    public static string? CameraProblem(JsonObject args)
    {
        if (args["eye"] is not null && Vector(args["eye"]) is null) return "eye must be [x, y, z] numbers.";
        if (args["look_at"] is not null && Vector(args["look_at"]) is null) return "look_at must be [x, y, z] numbers.";
        if (args["eye"] is not null && args["look_at"] is null) return "eye needs look_at (the point to face).";
        foreach (string key in new[] { "yaw", "pitch", "distance" })
            if (args[key] is { } node && (node is not JsonValue value || SettingNumber(value) is not { } number || !double.IsFinite(number)))
                return key + " must be a number.";
        if (args["distance"] is JsonValue far && SettingNumber(far) is <= 0) return "distance must be above 0.";
        if (McpJson.String(args, "viewport") is { } viewport && viewport is not ("scene" or "asset")) return "viewport is scene or asset.";
        return args["eye"] is null && args["look_at"] is null && args["yaw"] is null && args["pitch"] is null && args["distance"] is null
            ? "Give eye and look_at, look_at, yaw/pitch or distance." : null;
    }

    /** Error text for project_change, or null. */
    public static string? ChangeProblem(JsonObject args)
    {
        string action = McpJson.String(args, "action")?.Trim().ToLowerInvariant() ?? "";
        if (!ChangeActions.Contains(action)) return "action must be revert, remove, compare or export.";
        if (action is "revert" or "compare" && ParseHash(McpJson.String(args, "tag")) is null) return action + " needs tag: the changed tag hash.";
        if (action is "remove" or "export" && string.IsNullOrWhiteSpace(McpJson.String(args, "id"))) return action + " needs id: the added asset id.";
        if (action == "export" && (McpJson.String(args, "path") is not { Length: > 0 } path || !Path.IsPathFullyQualified(path)
            || !Directory.Exists(Path.GetDirectoryName(path)))) return "export needs path: an absolute file path in an existing folder.";
        return null;
    }
}
