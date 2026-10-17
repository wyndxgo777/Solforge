// Command coverage checks (08-docs\McpCoverage.md): the EditorCommands catalog and ToolMap name only real tools, list_commands works
// while paused, and run_command, show_panel, set_viewport_options, set_camera, export_asset, import_asset, project_change and
// arm_file_dialog validate before the approve click, ask once, and pass translated arguments on. Also the "header" UI kind.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Solforge.Mcp;

static class CommandChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("commands: " + message);
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static async Task Run(string scratch, FixtureHost host)
    {
        Directory.CreateDirectory(scratch);
        var all = SolforgeTools.Create(host);
        var names = all.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Expect(names.Count == all.Count, "tool names are unique");
        var tools = all.Where(tool => SolforgeTools.CommandToolNames.Contains(tool.Name)).ToDictionary(tool => tool.Name);
        Expect(tools.Count == SolforgeTools.CommandToolNames.Length, "every command tool is registered");
        Expect(tools.Values.All(tool => tool.RequiresApproval == (tool.Name != "list_commands")), "only list_commands skips the approve click");
        Expect(SolforgeTools.Instructions.Contains("list_commands") && SolforgeTools.Instructions.Contains("arm_file_dialog"), "instructions name the tools");
        Expect(all.Single(tool => tool.Name == "get_help").Description.Contains("section tools"), "get_help says it maps tools");
        Expect(tools["set_camera"].InputSchema["properties"]!["eye"]!["items"] is JsonObject, "array arguments carry items");

        // Catalog and map name only real tools; ids are stable shapes and unique.
        var idShape = new Regex("^[a-z]+\\.[a-z_]+$");
        Expect(EditorCommands.All.Select(command => command.Id).Distinct().Count() == EditorCommands.All.Length, "command ids are unique");
        Expect(EditorCommands.All.All(command => idShape.IsMatch(command.Id)), "command ids look like area.name");
        var toolWord = new Regex("(?<![\\w.(])[a-z]+(?:_[a-z0-9]+)+(?![\\w.])");
        foreach (string text in EditorCommands.All.Select(command => command.Prefer ?? "").Concat(ToolMap.Rows.Select(row => row.Tools)))
            foreach (Match match in toolWord.Matches(text))
                Expect(names.Contains(match.Value) || match.Value is "ui_" or "local_server" or "get_client_build" or "get_sessions" || match.Value.StartsWith("local_server_") || match.Value.StartsWith("testkit_"), $"'{match.Value}' in the map is a real tool");
        Expect(ToolMap.Rows.Any(row => row.Feature.Contains("AI tab") && row.Tools.StartsWith("none")), "Settings > AI stays the user's");

        async Task<McpToolResult> Call(string name, JsonObject args) => await tools[name].Handler(args, CancellationToken.None);
        int approvals = host.Approvals;
        async Task Refused(string name, JsonObject args, string why)
        {
            var result = await Call(name, args);
            Expect(result.IsError && host.Approvals == approvals, $"{name} refuses {why} before asking: {string.Join(" ", result.Texts)}");
        }
        async Task Ran(string name, JsonObject args, string summary)
        {
            var result = await Call(name, args);
            approvals++;
            Expect(!result.IsError && result.Texts[0] == "ran" && host.Approvals == approvals && host.LastSummary?.Contains(summary) == true,
                $"{name} asks once and runs ({host.LastSummary}): {string.Join(" ", result.Texts)}");
        }

        // list_commands: whole catalog, query filter, works while paused.
        host.Paused = true;
        var listed = JsonNode.Parse((await Call("list_commands", new JsonObject())).Texts[0])!;
        Expect((int)listed["total"]! == EditorCommands.All.Length, "list_commands lists the catalog while paused");
        var imports = JsonNode.Parse((await Call("list_commands", new JsonObject { ["query"] = "import delta" })).Texts[0])!["commands"]!.AsArray();
        Expect(imports.Count == 1 && (string?)imports[0]!["id"] == "assets.import_delta" && (string?)imports[0]!["input"] == "path (existing file)", "list_commands query");
        await Refused("run_command", new JsonObject { ["id"] = "edit.undo" }, "while paused");
        host.Paused = false;

        string file = Path.Combine(scratch, "delta.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        host.ApproveAll = true;
        await Refused("run_command", new JsonObject { ["id"] = "edit.nothing" }, "an unknown id");
        await Refused("run_command", new JsonObject { ["id"] = "assets.import_delta" }, "a picker command without path");
        await Refused("run_command", new JsonObject { ["id"] = "assets.import_delta", ["path"] = Path.Combine(scratch, "missing.png") }, "a missing file");
        await Refused("run_command", new JsonObject { ["id"] = "assets.import_delta", ["path"] = "delta.png" }, "a relative path");
        await Refused("run_command", new JsonObject { ["id"] = "view.grid" }, "a toggle without on");
        await Refused("run_command", new JsonObject { ["id"] = "toolbar.quality", ["value"] = "Ultra" }, "an unknown choice");
        await Refused("run_command", new JsonObject { ["id"] = "build.to_folder", ["path"] = Path.Combine(scratch, "no", "such", "out") }, "a folder without a parent");
        await Ran("run_command", new JsonObject { ["id"] = "assets.import_delta", ["path"] = file }, "Assets > Import as delta");
        await Ran("run_command", new JsonObject { ["id"] = "EDIT.UNDO" }, "Edit > Undo");
        await Ran("run_command", new JsonObject { ["id"] = "view.grid", ["on"] = false }, "View > Grid off");
        await Ran("run_command", new JsonObject { ["id"] = "window.layout", ["value"] = "solforge (ai)" }, "= solforge (ai)");
        await Ran("run_command", new JsonObject { ["id"] = "build.to_folder", ["path"] = Path.Combine(scratch, "out") }, "Build to folder");

        await Refused("show_panel", new JsonObject { ["panel"] = "ai_settings" }, "an unknown panel");
        await Ran("show_panel", new JsonObject { ["panel"] = "local_server" }, "Show local_server");

        await Refused("set_viewport_options", new JsonObject(), "no options");
        await Refused("set_viewport_options", new JsonObject { ["quality"] = "Ultra" }, "a bad quality");
        await Refused("set_viewport_options", new JsonObject { ["wireframe"] = "yes" }, "a non-boolean");
        await Refused("set_viewport_options", new JsonObject { ["fov"] = 90 }, "an unknown option");
        await Refused("set_viewport_options", new JsonObject { ["grid"] = true, ["viewport"] = "both" }, "a bad viewport");
        await Ran("set_viewport_options", new JsonObject { ["wireframe"] = true, ["quality"] = "high", ["view"] = "Top" }, "wireframe=true");

        await Refused("set_camera", new JsonObject(), "nothing to do");
        await Refused("set_camera", new JsonObject { ["eye"] = new JsonArray(1, 2, 3) }, "eye without look_at");
        await Refused("set_camera", new JsonObject { ["look_at"] = new JsonArray(1, 2) }, "a short vector");
        await Refused("set_camera", new JsonObject { ["distance"] = -4 }, "a negative distance");
        await Ran("set_camera", new JsonObject { ["eye"] = new JsonArray(0, 0, 10), ["look_at"] = new JsonArray(5, 5, 0) }, "to [0,0,10]");
        await Ran("set_camera", new JsonObject { ["yaw"] = 90, ["pitch"] = -20.5 }, "Move the camera");

        await Refused("export_asset", new JsonObject { ["kind"] = "fbx", ["path"] = Path.Combine(scratch, "a.fbx") }, "an unknown kind");
        await Refused("export_asset", new JsonObject { ["kind"] = "file" }, "no path");
        await Ran("export_asset", new JsonObject { ["kind"] = "blender_asset", ["path"] = Path.Combine(scratch, "bundle") }, "Export blender_asset");
        await Refused("import_asset", new JsonObject { ["kind"] = "delta", ["path"] = Path.Combine(scratch, "missing.glb") }, "a missing file");
        await Ran("import_asset", new JsonObject { ["kind"] = "delta", ["path"] = file }, "Import delta");

        await Refused("project_change", new JsonObject { ["action"] = "revert" }, "revert without tag");
        await Refused("project_change", new JsonObject { ["action"] = "nuke", ["tag"] = "0x80A1B2C3" }, "an unknown action");
        await Refused("project_change", new JsonObject { ["action"] = "export", ["id"] = "x" }, "export without path");
        await Ran("project_change", new JsonObject { ["action"] = "compare", ["tag"] = "0x80A1B2C3" }, "compare project change 0x80A1B2C3");

        await Refused("arm_file_dialog", new JsonObject { ["path"] = "relative.wav" }, "a relative path");
        await Ran("arm_file_dialog", new JsonObject { ["path"] = file }, "next file picker");

        host.ApproveAll = false;
        var declined = await Call("show_panel", new JsonObject { ["panel"] = "items" });
        approvals++;
        Expect(declined.IsError && declined.Texts[0].Contains("declined"), "a declined click is an error");

        // The new UI control kind for collapsing headers and tree nodes, and colour parsing.
        Expect(UiControls.Kinds.Contains("header") && UiControls.Allows("header", UiOperation.Invoke) && UiControls.Allows("header", UiOperation.SetChecked)
            && !UiControls.Allows("header", UiOperation.SetValue), "headers open with ui_invoke or ui_set_checked");
        Expect(EditorCommands.Problem(new JsonObject { ["id"] = "file.open_recent", ["value"] = "anything" }) is null, "free choices take any value");
    }
}
