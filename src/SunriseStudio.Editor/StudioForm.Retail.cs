using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;
using Solforge.Retail;
using Solforge.Retail.Decoding;
using Solforge.Retail.Porting;

namespace SunriseStudio.Editor;

/**
 * The Retail workspace window (View > Retail workspace): the live build (245048) dump through the same retail_* tools an
 * AI client gets (activities and destinations, tags in hex / annotated / decoded form, references, strings, port plans,
 * mission previews), the retail map viewer (a bubble opens read-only in the main viewport with a retail inspector for the
 * selection) and the porting buttons that write into the open project. Nothing here touches game packages.
 */
public sealed partial class StudioForm
{
    private bool retailOpen;
    private string retailStatus = "", retailQuery = "", retailTag = "", retailStringContainer = "", retailStringQuery = "", retailBubble = "";
    private string retailActivity = "";
    private bool retailStatics = true, retailTerrain = true;
    private volatile string retailOutput = "", retailStatusText = "", retailTagOutput = "", retailStringOutput = "", retailPortOutput = "";
    private volatile JsonArray retailRows = [], retailBubbles = [];
    private int retailBusy;

    /** Runs a retail read tool off the UI thread; done gets the text on the worker. */
    private void RetailRun(string tool, JsonObject args, Action<string, bool> done)
    {
        var handler = DataToolList().FirstOrDefault(candidate => candidate.Name == tool);
        if (handler is null) { retailStatus = "Unknown tool " + tool; return; }
        Interlocked.Increment(ref retailBusy);
        retailStatus = "Running " + tool + "...";
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await handler.Handler(args, CancellationToken.None).ConfigureAwait(false);
                string text = string.Join("\n", result.Texts);
                done(text, result.IsError);
                retailStatus = result.IsError ? tool + ": " + text : "";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException or InvalidDataException)
            { retailStatus = tool + " failed: " + exception.Message; }
            finally { Interlocked.Decrement(ref retailBusy); }
        });
    }

    private void DrawRetailWorkspace()
    {
        if (!retailOpen) return;
        ImGui.SetNextWindowSize(new Vector2(1100, 720) * scale, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Retail workspace###solforge-retail", ref retailOpen)) { ImGui.End(); return; }
        ImGui.TextDisabled($"Live build {RetailOptions.Build} (read-only). Root {RetailSource.Shared.Options.Root}");
        ImGui.SameLine();
        if (UiAuto.SmallButton("Status")) RetailRun("retail_status", new JsonObject(), (text, _) => retailStatusText = text);
        if (retailStatus.Length > 0 || Volatile.Read(ref retailBusy) > 0) ImGui.TextWrapped(retailStatus.Length > 0 ? retailStatus : "Working...");
        if (ImGui.BeginTabBar("retail-tabs"))
        {
            if (UiAuto.BeginTabItem("Activities")) { DrawRetailActivities(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Map viewer")) { DrawRetailMaps(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Tags")) { DrawRetailTags(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Strings")) { DrawRetailStrings(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Port")) { DrawRetailPort(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Status##retail")) { RetailText("##retail-status", retailStatusText); UiAuto.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private static void RetailText(string id, string text) =>
        UiAuto.InputTextMultiline(id, ref text, (uint)Math.Max(1024, text.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);

    private static JsonArray RetailRowsOf(string text, bool error, string key = "rows")
    {
        if (error) return [];
        try { return JsonNode.Parse(text)?[key] as JsonArray is { } rows ? (JsonArray)rows.DeepClone() : []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private void DrawRetailActivities()
    {
        if (UiAuto.Button("Destinations")) RetailRun("retail_activity", new JsonObject { ["limit"] = 500 }, (text, error) => retailRows = RetailRowsOf(text, error));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * scale);
        bool go = UiAuto.InputTextWithHint("##retail-query", "activity name (schism, mission_alice...)", ref retailQuery, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (UiAuto.Button("Find activities") || go)
            RetailRun("retail_activity", new JsonObject { ["query"] = retailQuery, ["limit"] = 500 }, (text, error) => retailRows = RetailRowsOf(text, error));
        ImGui.BeginChild("retail-list", new(380 * scale, 0), ImGuiChildFlags.Borders);
        var rows = retailRows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row) continue;
            string tag = row["tag"]?.ToString() ?? "", name = row["name"]?.ToString() ?? "";
            if (!UiAuto.Selectable($"{tag}  {name}##retail-row{i}", tag == retailActivity)) continue;
            retailActivity = tag;
            // Destinations and activities share the list; the decoder picks by name shape (activities have a dot).
            bool activity = name.Contains('.', StringComparison.Ordinal);
            RetailRun("retail_activity", new JsonObject { [activity ? "activity" : "destination"] = tag }, (text, _) => retailOutput = text);
            if (activity) RetailRun("retail_map", new JsonObject { ["activity"] = tag }, (text, error) => retailBubbles = RetailRowsOf(text, error, "bubbles"));
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("retail-detail", new(0, 0));
        if (retailActivity.Length > 0)
        {
            if (UiAuto.Button("Preview mission port")) { retailPortOutput = ""; RetailRun("retail_preview_mission", new JsonObject { ["activity"] = retailActivity }, (text, _) => retailPortOutput = text); retailOutput = "See the Port tab."; }
            ImGui.SameLine();
            if (UiAuto.Button("Port plan")) RetailRun("retail_port_plan", new JsonObject { ["tag"] = retailActivity, ["limit"] = 300 }, (text, _) => retailOutput = text);
            var bubbles = retailBubbles;
            for (int i = 0; i < bubbles.Count; i++)
            {
                if (bubbles[i] is not JsonObject bubble || bubble["tag"]?.ToString() is not { } bubbleTag) continue;
                ImGui.SameLine();
                if (UiAuto.SmallButton($"Open bubble {bubbleTag}##retail-open{i}")) RetailOpenBubble(bubbleTag);
            }
        }
        RetailText("##retail-detail-text", retailOutput);
        ImGui.EndChild();
    }

    private void DrawRetailMaps()
    {
        ImGui.TextWrapped("Opens a retail bubble read-only in the main viewport: every map node as a marker by component kind, plus " +
            "untextured terrain and static meshes. Textures, materials, entity models and dynamic objects are not drawn. Get bubble " +
            "tags from an activity in the Activities tab.");
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.InputTextWithHint("##retail-bubble", "bubble tag (0x...)", ref retailBubble, 32);
        ImGui.SameLine();
        UiAuto.Checkbox("Statics##retail", ref retailStatics);
        ImGui.SameLine();
        UiAuto.Checkbox("Terrain##retail", ref retailTerrain);
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.IsBusy);
        if (UiAuto.Button("Open in viewport")) RetailOpenBubble(retailBubble);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button("Bubble nodes")) RetailRun("retail_map", new JsonObject { ["bubble"] = retailBubble, ["limit"] = 200 }, (text, _) => retailOutput = text);
        ImGui.Separator();
        if (workspace.RetailView is { } view && workspace.RetailDocumentOpen)
        {
            ImGui.TextUnformatted($"Showing {view.Name} ({view.Markers.Count:N0} markers, {view.Meshes.Count:N0} meshes)");
            foreach (var (key, value) in view.Stats.Take(16)) { ImGui.TextDisabled($"{key}: {value:N0}"); }
            ImGui.Separator();
            ImGui.TextUnformatted("Selection");
            if (viewport?.SelectedObject is { } selected && workspace.RetailPicks.TryGetValue(selected, out var pick))
            {
                ImGui.TextUnformatted(pick.Label);
                foreach (var (name, value) in pick.Fields) ImGui.TextWrapped($"{name}: {value}");
                if (pick.Tag is { } tag)
                {
                    string hex = "0x" + tag.ToString("X8", CultureInfo.InvariantCulture);
                    if (UiAuto.SmallButton("Inspect retail tag##retail-pick"))
                    {
                        retailTag = hex;
                        RetailRun("retail_read", new JsonObject { ["tag"] = hex }, (text, _) => retailTagOutput = text);
                        retailStatus = "Opened " + hex + " in the Tags tab.";
                    }
                }
            }
            else ImGui.TextDisabled("Click a marker or mesh in the viewport.");
            if (view.Notes.Count > 0 && UiAuto.CollapsingHeader("Decode notes"))
                foreach (string note in view.Notes.Take(50)) ImGui.TextWrapped(note);
        }
        else ImGui.TextDisabled("No retail bubble is open.");
    }

    private void RetailOpenBubble(string text)
    {
        if (RetailTag.Parse(text) is not { } bubble) { retailStatus = "Give a bubble tag (0x...)."; return; }
        retailBubble = text;
        workspace.Run(() => workspace.OpenRetailBubble(RetailSource.Shared, bubble, new RetailSceneOptions { Statics = retailStatics, Terrain = retailTerrain }));
        retailStatus = workspace.Error.Length > 0 ? workspace.Error : $"Opening retail bubble {RetailTag.Hex(bubble)}...";
    }

    private void DrawRetailTags()
    {
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.InputTextWithHint("##retail-tag", "retail tag (0x... or pkg:index)", ref retailTag, 32);
        void Read(string label, string tool, JsonObject args)
        {
            ImGui.SameLine();
            if (UiAuto.Button(label)) RetailRun(tool, args, (text, _) => retailTagOutput = text);
        }
        Read("Info", "retail_tag", new JsonObject { ["tag"] = retailTag });
        Read("Decoded", "retail_read", new JsonObject { ["tag"] = retailTag, ["mode"] = "decoded" });
        Read("Annotated", "retail_read", new JsonObject { ["tag"] = retailTag, ["mode"] = "annotated" });
        Read("Hex", "retail_read", new JsonObject { ["tag"] = retailTag, ["mode"] = "hex", ["length"] = 4096 });
        Read("References", "retail_references", new JsonObject { ["tag"] = retailTag, ["limit"] = 500 });
        Read("Referenced by", "retail_references", new JsonObject { ["tag"] = retailTag, ["direction"] = "incoming" });
        Read("Port plan##tag", "retail_port_plan", new JsonObject { ["tag"] = retailTag, ["limit"] = 500 });
        RetailText("##retail-tag-out", retailTagOutput);
    }

    private void DrawRetailStrings()
    {
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.InputTextWithHint("##retail-container", "container (0x...)", ref retailStringContainer, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260 * scale);
        UiAuto.InputTextWithHint("##retail-string-query", "text", ref retailStringQuery, 256);
        ImGui.SameLine();
        if (UiAuto.Button("Search"))
        {
            var args = new JsonObject { ["limit"] = 500 };
            if (retailStringContainer.Length > 0) args["container"] = retailStringContainer;
            if (retailStringQuery.Length > 0) args["query"] = retailStringQuery;
            RetailRun("retail_strings", args, (text, _) => retailStringOutput = text);
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.Project is null || retailStringContainer.Length == 0);
        if (UiAuto.Button("Write to project")) RetailWriteStrings(retailStringContainer);
        ImGui.EndDisabled();
        RetailText("##retail-strings-out", retailStringOutput);
    }

    private void DrawRetailPort()
    {
        ImGui.SetNextItemWidth(260 * scale);
        UiAuto.InputTextWithHint("##retail-port-activity", "activity tag or name", ref retailActivity, 128);
        ImGui.SameLine();
        if (UiAuto.Button("Preview")) RetailRun("retail_preview_mission", new JsonObject { ["activity"] = retailActivity }, (text, _) => retailPortOutput = text);
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.Project is null || retailActivity.Length == 0);
        if (UiAuto.Button("Port mission into project")) RetailWriteMission(retailActivity);
        ImGui.EndDisabled();
        if (workspace.Project is null) { ImGui.SameLine(); ImGui.TextDisabled("Open a project to write ports."); }
        ImGui.TextWrapped("A port writes content/activity/<id>.json (one objective per retail phase) and retail_ports/<id>/ (a mission " +
            "script skeleton with CHANGE_ME placeholders and report.json). Encounter logic, AI, loot and dialogue are not ported.");
        RetailText("##retail-port-out", retailPortOutput);
    }

    /** The project folder, or null with the status set. */
    private string? RetailProjectFolder()
    {
        if (workspace.Project?.DirectoryPath is { Length: > 0 } folder) return folder;
        retailStatus = "Open a project first.";
        return null;
    }

    private void RetailWriteMission(string text)
    {
        if (RetailProjectFolder() is not { } root) return;
        var source = RetailSource.Shared;
        retailStatus = "Porting " + text + "...";
        _ = Task.Run(async () =>
        {
            try
            {
                await source.WaitForIndexAsync(TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
                uint? tag = RetailTag.Parse(text) ?? RetailActivity.Activities(source, text).FirstOrDefault(row => row.Name.Equals(text, StringComparison.OrdinalIgnoreCase))?.Tag;
                if (tag is null) { retailStatus = "No activity " + text + "."; return; }
                var result = RetailMissionPort.Port(source, tag.Value, out string? problem);
                if (result is null) { retailStatus = problem ?? "The activity did not decode."; return; }
                var files = RetailMissionPort.Write(root, result, overwrite: true);
                retailPortOutput = "Wrote:\n" + string.Join("\n", files) + "\n\n" + result.Report.ToJsonString(McpJson.Indented);
                retailStatus = $"Ported {result.Activity.Id} into the project.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { retailStatus = "Port failed: " + error.Message; }
        });
    }

    private void RetailWriteStrings(string text)
    {
        if (RetailProjectFolder() is not { } root || RetailTag.Parse(text) is not { } tag) { retailStatus = "Give a string container tag."; return; }
        var source = RetailSource.Shared;
        _ = Task.Run(() =>
        {
            try
            {
                var strings = RetailStrings.Read(source, tag, out string? problem);
                if (strings.Count == 0) { retailStatus = problem ?? "The container has no strings."; return; }
                var rows = RetailConverters.ToBankTexts(strings, out var notes);
                string folder = Path.Combine(root, "retail_ports", "strings");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, tag.ToString("x8", CultureInfo.InvariantCulture) + ".json");
                var document = new JsonObject
                {
                    ["container"] = RetailTag.Hex(tag), ["build"] = RetailOptions.Build, ["count"] = rows.Count,
                    ["strings"] = new JsonArray(rows.Select(row => (JsonNode)new JsonObject { ["hash"] = RetailTag.Hex(row.Hash), ["text"] = row.Text }).ToArray()),
                    ["notes"] = new JsonArray(notes.Select(note => (JsonNode)note).ToArray())
                };
                File.WriteAllText(path, document.ToJsonString(McpJson.Indented));
                retailStatus = $"Wrote {rows.Count:N0} strings to {path}.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { retailStatus = "Writing strings failed: " + error.Message; }
        });
    }

    /** retail_open_map (approved in the AI workspace). */
    private string AiRetailOpenMap(JsonObject args)
    {
        if (RetailTag.Parse(McpJson.String(args, "bubble")) is not { } bubble) return "Give a bubble tag (0x...) from retail_map.";
        if (workspace.IsBusy) return "Solforge is busy (" + workspace.Status + "); try again when get_editor_state says it is idle.";
        var options = new RetailSceneOptions { Statics = McpJson.Bool(args, "statics", true), Terrain = McpJson.Bool(args, "terrain", true) };
        workspace.Run(() => workspace.OpenRetailBubble(RetailSource.Shared, bubble, options));
        if (workspace.Error.Length > 0) return workspace.Error;
        retailOpen = true;
        retailBubble = RetailTag.Hex(bubble);
        return $"Opening retail bubble {RetailTag.Hex(bubble)} read-only in the viewport (any open map was closed); poll get_editor_state " +
            "until busy is false, then get_viewport_frame shows it. Markers per node kind and untextured terrain/statics; no textures or entity models.";
    }
}
