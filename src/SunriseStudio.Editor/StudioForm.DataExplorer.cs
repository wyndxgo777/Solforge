using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;
using Solforge.Mcp.Decomp;

namespace SunriseStudio.Editor;

/**
 * The Data explorer window (View > Data explorer): the decompiled destiny2.exe (function search, C source, callers and
 * callees, strings and their xrefs, renames) and raw package data (dump_tag, hex, class schemas, localized strings),
 * plus a console that runs any read MCP tool and exports its rows to JSON or CSV. Every button calls the same tool
 * handler an AI client gets, so what the user sees and what an agent reads always match.
 */
public sealed partial class StudioForm
{
    private bool dataExplorerOpen;
    private IReadOnlyList<McpTool>? dataTools;
    private string dataFunctionQuery = "", dataFunction = "", dataRename = "", dataStringQuery = "", dataTagHash = "", dataClassHash = "";
    private string dataConsoleTool = "decomp_status", dataConsoleArgs = "{}";
    private volatile string dataSource = "", dataOutput = "", dataStatus = "", dataTagOutput = "";
    private volatile JsonArray dataFunctionRows = [], dataCallers = [], dataCallees = [], dataStringRows = [];
    private int dataBusy;

    private IReadOnlyList<McpTool> DataToolList()
    {
        aiHost ??= new AiHost(this);
        return dataTools ??= SolforgeTools.Create(aiHost);
    }

    /** Runs a read tool off the UI thread; done gets its text (or the error text) back on the worker thread. */
    private void DataRun(string tool, JsonObject args, Action<string, bool> done)
    {
        var handler = DataToolList().FirstOrDefault(candidate => candidate.Name == tool);
        if (handler is null) { dataStatus = "Unknown tool " + tool; return; }
        Interlocked.Increment(ref dataBusy);
        dataStatus = "Running " + tool + "...";
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await handler.Handler(args, CancellationToken.None).ConfigureAwait(false);
                done(string.Join("\n", result.Texts), result.IsError);
                dataStatus = result.IsError ? tool + ": " + string.Join(" ", result.Texts) : "";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
            { dataStatus = tool + " failed: " + exception.Message; }
            finally { Interlocked.Decrement(ref dataBusy); }
        });
    }

    private static JsonArray Rows(string text, bool error)
    {
        if (error) return [];
        try { return JsonNode.Parse(text)?["rows"] as JsonArray is { } rows ? (JsonArray)rows.DeepClone() : []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private void OpenDataFunction(string function)
    {
        dataFunction = function;
        DataRun("get_function", new JsonObject { ["function"] = function, ["max_lines"] = 2000 }, (text, _) => dataSource = text);
        DataRun("xrefs_to", new JsonObject { ["address"] = function, ["limit"] = 200 }, (text, error) => dataCallers = Rows(text, error));
        DataRun("xrefs_from", new JsonObject { ["function"] = function, ["limit"] = 500 }, (text, error) => dataCallees = Rows(text, error));
    }

    private void DrawDataExplorer()
    {
        if (!dataExplorerOpen) return;
        ImGui.SetNextWindowSize(new Vector2(1100, 720) * scale, ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Data explorer###solforge-data-explorer", ref dataExplorerOpen)) { ImGui.End(); return; }
        if (dataStatus.Length > 0 || Volatile.Read(ref dataBusy) > 0) ImGui.TextWrapped(dataStatus.Length > 0 ? dataStatus : "Working...");
        if (ImGui.BeginTabBar("data-tabs"))
        {
            if (UiAuto.BeginTabItem("Functions")) { DrawDataFunctions(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Strings")) { DrawDataStrings(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Tag data")) { DrawDataTags(); UiAuto.EndTabItem(); }
            if (UiAuto.BeginTabItem("Tool console")) { DrawDataConsole(); UiAuto.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawDataFunctions()
    {
        ImGui.SetNextItemWidth(320 * scale);
        bool go = UiAuto.InputText("##fn-query", ref dataFunctionQuery, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (UiAuto.Button("Search functions") || go)
            DataRun("search_functions", new JsonObject { ["query"] = dataFunctionQuery, ["limit"] = 500 }, (text, error) => dataFunctionRows = Rows(text, error));
        ImGui.SameLine();
        if (UiAuto.Button("Export CSV##fn")) DataExport("search_functions", new JsonObject { ["query"] = dataFunctionQuery });
        ImGui.SameLine();
        if (UiAuto.Button("Status")) DataRun("decomp_status", new JsonObject(), (text, _) => dataSource = text);

        float listWidth = 360 * scale;
        ImGui.BeginChild("fn-list", new(listWidth, 0), ImGuiChildFlags.Borders);
        DataRowList(dataFunctionRows, "fn");
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("fn-body", new(0, 0));
        if (dataFunction.Length > 0)
        {
            ImGui.TextUnformatted("Function " + dataFunction);
            ImGui.SetNextItemWidth(240 * scale);
            UiAuto.InputText("##rename", ref dataRename, 128);
            ImGui.SameLine();
            if (UiAuto.Button("Rename") && dataRename.Trim() is { Length: > 0 } name && !name.Any(char.IsWhiteSpace) && aiHost is { } host
                && SolforgeTools.DecompRoot(host) is { } root)
            {
                string function = dataFunction;
                _ = Task.Run(async () =>
                {
                    var index = await DecompIndex.LoadAsync(root, CancellationToken.None).ConfigureAwait(false);
                    if (index.Resolve(function) is { } rva) { index.Rename(rva, name); dataStatus = $"0x{rva:X8} is now {name}."; }
                });
            }
        }
        float half = ImGui.GetContentRegionAvail().X / 2 - 4 * scale;
        ImGui.BeginChild("fn-callers", new(half, 140 * scale), ImGuiChildFlags.Borders);
        ImGui.TextDisabled("Referenced by");
        DataRowList(dataCallers, "callers");
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("fn-callees", new(0, 140 * scale), ImGuiChildFlags.Borders);
        ImGui.TextDisabled("References");
        DataRowList(dataCallees, "callees");
        ImGui.EndChild();
        string source = dataSource;
        UiAuto.InputTextMultiline("##fn-source", ref source, (uint)Math.Max(1024, source.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);
        ImGui.EndChild();
    }

    /** Rows with an rva open that function on click; other rows show their text. */
    private void DataRowList(JsonArray rows, string id)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row) continue;
            string rva = row["rva"]?.ToString() ?? "";
            string label = string.Join("  ", new[] { row["kind"]?.ToString(), rva, row["name"]?.ToString(), row["string"]?.ToString() ?? row["signature"]?.ToString() ?? row["text"]?.ToString() }
                .Where(part => !string.IsNullOrEmpty(part)));
            if (UiAuto.Selectable($"{(label.Length > 160 ? label[..160] : label)}##{id}{i}", rva == dataFunction) && rva.Length > 0
                && (row["kind"]?.ToString() is null or "call" or "function" || row["function"] is not null))
                OpenDataFunction(rva);
        }
    }

    private void DrawDataStrings()
    {
        ImGui.SetNextItemWidth(320 * scale);
        bool go = UiAuto.InputText("##str-query", ref dataStringQuery, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (UiAuto.Button("Find in exe") || go)
            DataRun("find_string", new JsonObject { ["query"] = dataStringQuery, ["limit"] = 300 }, (text, error) => dataStringRows = Rows(text, error));
        ImGui.SameLine();
        if (UiAuto.Button("Find in localized text"))
            DataRun("search_strings", new JsonObject { ["query"] = dataStringQuery, ["limit"] = 200 }, (text, error) => dataStringRows = Rows(text, error));
        ImGui.SameLine();
        if (UiAuto.Button("Export CSV##str")) DataExport("find_string", new JsonObject { ["query"] = dataStringQuery });
        ImGui.BeginChild("str-rows", new(0, 0), ImGuiChildFlags.Borders);
        var rows = dataStringRows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row) continue;
            ImGui.TextWrapped($"{row["rva"] ?? row["hash"]}  {row["text"]}");
            if (row["referenced_by"] is JsonArray users)
                foreach (var user in users)
                {
                    ImGui.SameLine();
                    string name = user?.ToString() ?? "";
                    int open = name.LastIndexOf("(0x", StringComparison.Ordinal);
                    string rva = open >= 0 ? name[(open + 1)..].TrimEnd(')') : name.Replace("FUN_", "0x", StringComparison.Ordinal);
                    if (UiAuto.SmallButton($"{name}##s{i}{rva}")) { OpenDataFunction(rva); dataStatus = "Opened " + name + " in Functions."; }
                }
        }
        ImGui.EndChild();
    }

    private void DrawDataTags()
    {
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.InputText("Tag hash##tag", ref dataTagHash, 32);
        ImGui.SameLine();
        if (UiAuto.Button("Dump")) DataRun("dump_tag", new JsonObject { ["hash"] = dataTagHash }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("Hex")) DataRun("read_tag_bytes", new JsonObject { ["hash"] = dataTagHash, ["length"] = 4096 }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("String")) DataRun("get_string", new JsonObject { ["hash"] = dataTagHash }, (text, _) => dataTagOutput = text);
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.InputText("Class hash##class", ref dataClassHash, 32);
        ImGui.SameLine();
        if (UiAuto.Button("Infer schema")) DataRun("infer_tag_schema", new JsonObject { ["class"] = dataClassHash }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("Tags of class")) DataRun("list_tags_of_class", new JsonObject { ["class"] = dataClassHash, ["limit"] = 500 }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("Exe handlers")) DataRun("tag_type_handlers", new JsonObject { ["class"] = dataClassHash }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("All classes")) DataRun("list_tag_classes", new JsonObject { ["limit"] = 500 }, (text, _) => dataTagOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("Export classes CSV")) DataExport("list_tag_classes", new JsonObject());
        string output = dataTagOutput;
        UiAuto.InputTextMultiline("##tag-out", ref output, (uint)Math.Max(1024, output.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);
    }

    private void DrawDataConsole()
    {
        var names = DataToolList().Where(tool => !tool.RequiresApproval).Select(tool => tool.Name).ToArray();
        ImGui.SetNextItemWidth(260 * scale);
        if (UiAuto.BeginCombo("Tool##console", dataConsoleTool))
        {
            foreach (string name in names) if (UiAuto.Selectable(name, name == dataConsoleTool)) dataConsoleTool = name;
            UiAuto.EndCombo();
        }
        if (DataToolList().FirstOrDefault(tool => tool.Name == dataConsoleTool) is { } selected) ImGui.TextWrapped(selected.Description);
        UiAuto.InputTextMultiline("Arguments (JSON)##console-args", ref dataConsoleArgs, 4096, new(0, 80 * scale));
        JsonObject? args = null;
        try { args = JsonNode.Parse(dataConsoleArgs) as JsonObject; } catch (System.Text.Json.JsonException) { }
        if (args is null) ImGui.TextDisabled("Arguments must be a JSON object.");
        ImGui.BeginDisabled(args is null);
        if (UiAuto.Button("Run")) DataRun(dataConsoleTool, (JsonObject)args!.DeepClone(), (text, _) => dataOutput = text);
        ImGui.SameLine();
        if (UiAuto.Button("Export JSON")) DataExport(dataConsoleTool, args!, "json");
        ImGui.SameLine();
        if (UiAuto.Button("Export CSV")) DataExport(dataConsoleTool, args!, "csv");
        ImGui.EndDisabled();
        string output = dataOutput;
        UiAuto.InputTextMultiline("##console-out", ref output, (uint)Math.Max(1024, output.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);
    }

    private void DataExport(string tool, JsonObject args, string format = "csv") =>
        DataRun("export_data", new JsonObject { ["tool"] = tool, ["args"] = args.DeepClone(), ["format"] = format },
            (text, error) => dataStatus = error ? text : "Exported: " + (JsonNode.Parse(text)?["path"]?.ToString() ?? text));
}
