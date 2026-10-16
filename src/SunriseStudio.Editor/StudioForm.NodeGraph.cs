using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Graphs;
using Solforge.Mcp;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** The Node Graph panel: material, weapon/item and mission graphs (*.solgraph.json) on the Blueprint canvas. */
public sealed partial class StudioForm
{
    private readonly NodeGraphPanel nodeGraph = new();
    private bool nodeGraphActive;

    private void DrawNodeGraphPanel(Vector2 position, Vector2 size)
    {
        BeginPanel("Node graph area", position, size);
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) { nodeGraphActive = true; missionGraphActive = false; }
        nodeGraph.Draw(workspace.Project?.DirectoryPath, AiApplyGraph, (path, content) =>
            RunAiAction("apply_project_edit", new JsonObject { ["path"] = path, ["content"] = content }));
        ImGui.End();
    }

    /**
     * apply_graph: compiles a material or weapon graph and runs it through the existing pipelines. Weapon graphs
     * become apply_item_edit calls on the target item; material graphs patch the material's interchange manifest
     * (constants and texture slots) and import it, so the edit shows in the viewport and Project changes.
     */
    private string AiApplyGraph(JsonObject args)
    {
        if (workspace.Project?.DirectoryPath is not { } root) return "No project is open.";
        string relative = McpJson.String(args, "path") ?? "";
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return "No such graph in the project.";
        GraphDocument document;
        try { document = GraphDocument.Load(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { return "Cannot read the graph: " + exception.Message; }
        if (document.Kind == GraphKind.Weapon) return AiApplyWeaponGraph(document);
        if (document.Kind == GraphKind.Material) return AiApplyMaterialGraph(document);
        return "Mission graphs apply as a Lua file (apply_project_edit).";
    }

    private string AiApplyWeaponGraph(GraphDocument document)
    {
        var result = WeaponGraphCompiler.Compile(document);
        if (result.Diagnostics.FirstOrDefault(problem => problem.Severity == DiagnosticSeverity.Error) is { } error) return "Not applied: " + error;
        if (result.Target.Length == 0) return "Set the item on the Weapon Output node.";
        var lines = new List<string>();
        foreach (var op in result.Ops)
        {
            string reply = AiApplyItemEdit(op.ToArguments(result.Target));
            lines.Add(reply);
            // A loading or reading reply means the item is not open yet: stop and let the caller try again.
            if (!reply.StartsWith("Applied", StringComparison.Ordinal)) break;
        }
        bool opsApplied = lines.Count == 0 || lines[^1].StartsWith("Applied", StringComparison.Ordinal);
        if (result.WorkflowFields.Count > 0)
            lines.Add("Set in the item's creation workflow (not apply_item_edit): "
                + string.Join("; ", result.WorkflowFields.Select(pair => pair.Key + " = " + pair.Value)) + ".");
        if (result.Definitions.Count > 0 && opsApplied)
            lines.Add(AiInstallCustomPerks(result));
        return lines.Count == 0 ? "The graph has no edits." : string.Join("\n", lines);
    }

    /** Packs the graph's Custom Perks into new sandbox perks and plugs on the target weapon (Studio.md Phase 27). */
    private string AiInstallCustomPerks(WeaponCompileResult result)
    {
        var data = workspace.Investment;
        uint tag = data is not null && AiFindItem(data, result.Target) is { } item ? item.Tag : SolforgeTools.ParseHash(result.Target) ?? 0;
        if (AiInvestmentReady(tag, vendor: false) is { } wait) return wait;
        if (!workspace.CanEdit) return "Solforge is busy (" + workspace.Status + "); apply the graph again when get_editor_state says it is idle.";
        if (workspace.CustomPerkPackage() is not { } package)
            return "Custom perks need a project package; the default 0AA0 is installed. Create an item first so the project has its own package.";
        workspace.Run(() => workspace.CreateCustomPerks(tag, result.Definitions, package));
        if (workspace.Error.Length != 0) return "Custom perks not installed: " + workspace.Error;
        return $"Packing {result.Definitions.Count} custom perk(s) ({string.Join(", ", result.Definitions.Select(perk => perk.Name))}) into package "
            + $"{package:X4} in the background. get_editor_state shows 'Custom perks installed' when done; get_item then lists the new plug "
            + "in its socket. Build the project, deploy the packages and build_data.bin, then get a fresh drop or /perk force the old copy.";
    }

    private string AiApplyMaterialGraph(GraphDocument document)
    {
        var result = MaterialGraphCompiler.Compile(document);
        if (result.Diagnostics.FirstOrDefault(problem => problem.Severity == DiagnosticSeverity.Error) is { } error) return "Not applied: " + error;
        if (SolforgeTools.ParseHash(result.Target) is not { } tag) return "Set the material tag (0x...) on the Material Output node.";
        if (workspace.Catalog is not { } catalog || !workspace.CanEdit) return "No project is open, or Solforge is busy.";
        MaterialDocument material;
        try { material = MaterialInterchange.Describe(catalog, tag); }
        catch (Exception exception)
        { return "Cannot read that material: " + exception.Message; }

        var constants = material.Constants.ToList();
        int written = 0, missing = 0;
        foreach (var write in result.Constants)
        {
            var vector = constants.Select((scalar, index) => (scalar, index))
                .Where(row => row.scalar.Stage == write.Stage && row.scalar.Bank == write.Bank && row.scalar.Vector == write.Vector)
                .OrderBy(row => row.scalar.Offset).ToList();
            if (vector.Count == 0) { missing++; continue; }
            foreach (int component in write.Components)
            {
                if (component >= vector.Count || component >= write.Values.Length) continue;
                var (scalar, index) = vector[component];
                constants[index] = scalar with { Value = write.Values[component].ToString("R", CultureInfo.InvariantCulture) };
                written++;
            }
        }
        var slots = material.Slots.ToList();
        foreach (var texture in result.Textures)
        {
            int index = slots.FindIndex(slot => slot.Stage == texture.Stage && slot.Slot == texture.Slot);
            if (index < 0 || SolforgeTools.ParseHash(texture.Texture) is not { } textureTag) { missing++; continue; }
            slots[index] = slots[index] with { TextureTag = textureTag };
            written++;
        }
        if (written == 0) return missing > 0 ? "None of the graph's outputs exist on that material." : "The graph writes nothing.";

        string folder = Path.Combine(Path.GetTempPath(), "solforge-graphs", $"{tag:X8}");
        Directory.CreateDirectory(folder);
        string manifest = Path.Combine(folder, "material.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(material with { Constants = constants, Slots = slots }, new JsonSerializerOptions { WriteIndented = true }));
        workspace.ImportEdits(() => MaterialInterchange.Import(catalog, manifest, importTextures: false));
        return $"Applying {written} material value(s) to {tag:X8}{(missing > 0 ? $"; {missing} output(s) have no slot on this material" : "")}. It shows in Project changes with undo.";
    }
}

/** File handling, toolbar and details for the node graph panel; the canvas draws and edits the graph. */
internal sealed class NodeGraphPanel
{
    private readonly BlueprintCanvas canvas = new();
    private GraphEditor? editor;
    private string? path;
    private DateTime stamp;
    private int validated = -1;
    private List<GraphDiagnostic> diagnostics = [];
    private string output = "", newName = "new_graph", root = "";
    private int newKind;
    private MaterialPreview? preview;
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);
    private double nextScan;
    private List<string> files = [];
    private string lastRoot = "";

    /**
     * Live material preview; StudioForm sets it once the renderer exists. It stays while the graph is open, also with
     * the panel hidden behind the viewport; Revert, Close, another graph or another project put the material back.
     */
    public NodeGraphLivePreview? Live { get; set; }

    public bool CanUndo => editor?.CanUndo == true;
    public bool CanRedo => editor?.CanRedo == true;
    public bool CanSave => editor is not null && path is not null;
    public void Undo() => editor?.Undo();
    public void Redo() => editor?.Redo();

    public void Save()
    {
        if (editor is null || path is null) return;
        editor.Document.Save(path);
        editor.Dirty = false;
        stamp = File.GetLastWriteTimeUtc(path);
    }

    private void Open(string file)
    {
        try
        {
            var next = new GraphEditor(GraphDocument.Load(file));
            Live?.Clear();
            drafts.Clear();
            editor = next;
            path = file;
            stamp = File.GetLastWriteTimeUtc(file);
            validated = -1;
            output = "";
            canvas.FrameAll();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { canvas.Say("Cannot open: " + exception.Message); }
    }

    /** Drops unsaved changes: the saved graph comes back and the viewports show the authored material until the next edit. */
    private void Revert()
    {
        if (editor is null || path is null || !File.Exists(path)) return;
        try
        {
            editor.Replace(GraphDocument.Load(path), "Revert");
            editor.Dirty = false;
            stamp = File.GetLastWriteTimeUtc(path);
            drafts.Clear();
            Live?.Hold(editor.Revision);
            canvas.Say("Reverted to the saved graph.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { canvas.Say("Cannot revert: " + exception.Message); }
    }

    /** Closes the graph (unsaved changes are dropped) and puts the viewports back on the authored material. */
    private void Close()
    {
        Live?.Clear();
        editor = null;
        path = null;
        drafts.Clear();
        output = "";
        preview = null;
        diagnostics = [];
        validated = -1;
    }

    /** An MCP edit_graph call rewrites the file: take it when there are no unsaved changes here. */
    private void Reload()
    {
        if (editor is null || path is null || editor.Dirty || !File.Exists(path)) return;
        var now = File.GetLastWriteTimeUtc(path);
        if (now == stamp) return;
        stamp = now;
        try { editor.Replace(GraphDocument.Load(path)); editor.Dirty = false; canvas.Say("Reloaded changes from disk."); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { canvas.Say("Cannot reload: " + exception.Message); }
    }

    public void Draw(string? projectRoot, Func<JsonObject, string> applyGraph, Func<string, string, string> writeFile)
    {
        root = projectRoot ?? "";
        // A graph belongs to its project: another project (or none) closes it.
        if (root != lastRoot)
        {
            if (lastRoot.Length > 0) Close();
            lastRoot = root;
        }
        if (ImGui.GetTime() > nextScan)
        {
            nextScan = ImGui.GetTime() + 2;
            Reload();
        }
        DrawToolbar(applyGraph, writeFile);
        if (editor is null)
        {
            ImGui.TextDisabled(root.Length == 0 ? "Open a project, then create or open a graph." : "Create or open a graph (material, weapon or mission).");
            return;
        }
        if (editor.Revision != validated)
        {
            validated = editor.Revision;
            diagnostics = GraphValidation.Validate(editor.Document).ToList();
            string? scope = editor.Scope?.Id;
            canvas.Problems = diagnostics.Where(problem => problem.NodeId is not null && problem.Scope == scope)
                .GroupBy(problem => problem.NodeId!).ToDictionary(group => group.Key, group => group.ToList());
            preview = editor.Document.Kind == GraphKind.Material ? MaterialGraphCompiler.Compile(editor.Document).Preview : null;
        }
        Live?.Update(editor.Document, editor.Revision, drafts, ImGui.GetTime());
        float details = MathF.Min(300, ImGui.GetContentRegionAvail().X * .35f);
        ImGui.BeginChild("##graph-canvas", new Vector2(-details - 6, 0));
        canvas.Draw(editor);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##graph-details", new Vector2(details, 0));
        DrawDetails(editor);
        ImGui.EndChild();
    }

    private void DrawToolbar(Func<JsonObject, string> applyGraph, Func<string, string, string> writeFile)
    {
        string label = editor is null ? "No graph" : Path.GetFileName(path) + (editor.Dirty ? " *" : "");
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##graph-file", label))
        {
            if (ImGui.IsWindowAppearing())
                files = root.Length > 0 && Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*" + GraphDocument.Extension, SearchOption.AllDirectories).Take(500).ToList() : [];
            foreach (string file in files)
                if (ImGui.Selectable(Path.GetRelativePath(root, file).Replace('\\', '/'), file == path)) Open(file);
            if (files.Count == 0) ImGui.TextDisabled("No graphs in the project yet.");
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(root.Length == 0);
        if (ImGui.Button("New")) ImGui.OpenPopup("new-graph");
        ImGui.EndDisabled();
        if (ImGui.BeginPopup("new-graph"))
        {
            ImGui.Combo("Kind", ref newKind, "Material\0Weapon / item\0Mission\0");
            ImGui.InputText("Name", ref newName, 64);
            if (ImGui.Button("Create") && newName.Trim().Length > 0)
            {
                var kind = newKind switch { 0 => GraphKind.Material, 1 => GraphKind.Weapon, _ => GraphKind.Mission };
                string safe = string.Concat(newName.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
                string file = Path.Combine(root, "graphs", safe + GraphDocument.Extension);
                if (File.Exists(file)) canvas.Say("That graph exists; opened it.");
                else { Directory.CreateDirectory(Path.GetDirectoryName(file)!); GraphDocument.Create(kind, newName.Trim()).Save(file); }
                Open(file);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (editor is null) return;
        ImGui.SameLine();
        if (ImGui.Button("Save")) Save();
        ImGui.SameLine();
        ImGui.BeginDisabled(!editor.Dirty || path is null);
        if (ImGui.Button("Revert")) Revert();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Drop unsaved changes and the live preview; the viewport shows the material as it is.");
        ImGui.SameLine();
        ImGui.BeginDisabled(editor.Dirty);
        bool close = ImGui.Button("Close");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(editor.Dirty ? "Save or Revert first." : "Close the graph; the viewport shows the material as it is.");
        if (close)
        {
            Close();
            return;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!editor.CanUndo);
        if (ImGui.Button("Undo")) editor.Undo();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && editor.UndoLabel.Length > 0) ImGui.SetTooltip("Undo " + editor.UndoLabel);
        ImGui.SameLine();
        ImGui.BeginDisabled(!editor.CanRedo);
        if (ImGui.Button("Redo")) editor.Redo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Compile")) output = GraphCommands.Compile(editor.Document).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ImGui.SameLine();
        ImGui.BeginDisabled(diagnostics.Any(problem => problem.Severity == DiagnosticSeverity.Error));
        if (ImGui.Button("Apply"))
        {
            Save();
            string relative = Path.GetRelativePath(root, path!).Replace('\\', '/');
            output = editor.Document.Kind == GraphKind.Mission
                ? writeFile(relative[..^GraphDocument.Extension.Length] + ".lua", MissionGraphCompiler.Compile(editor.Document).Lua)
                : applyGraph(new JsonObject { ["path"] = relative });
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        bool map = canvas.ShowMinimap;
        if (ImGui.Checkbox("Minimap", ref map)) canvas.ShowMinimap = map;
        if (editor.Scope is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Back to graph")) { editor.Enter(null); canvas.FrameAll(); }
        }
    }

    private void DrawDetails(GraphEditor graph)
    {
        if (preview is { } swatch)
        {
            ImGui.SeparatorText("Preview");
            if (Live is { } live)
            {
                bool on = live.Enabled;
                if (ImGui.Checkbox("Live", ref on))
                {
                    live.Enabled = on;
                    if (!on) live.Clear();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show the graph in the viewports as you edit. Nothing is written until Apply.");
                ImGui.SameLine();
                uint? picked = live.SelectedMaterial();
                ImGui.BeginDisabled(picked is null);
                if (ImGui.Button("From selection") && picked is { } tag) SetTarget(graph, tag);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(picked is { } chosen ? $"Edit the selected part's material, 0x{chosen:X8}." : "Select a part in the viewport first.");
            }
            float width = ImGui.GetContentRegionAvail().X;
            if (Live is not { Enabled: true } livePreview || !livePreview.DrawPreviewMesh(new Vector2(width, MathF.Min(width * .75f, 220))))
                DrawSwatch(swatch);
            string summary = Live?.Summary() ?? "Apply writes it to the material in the project; the viewport shows it then.";
            if (summary.Length > 0)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(summary);
                ImGui.PopTextWrapPos();
            }
        }

        var selected = graph.Selection.Select(graph.Node).OfType<GraphNode>().ToList();
        if (selected.Count == 1 && NodeLibrary.Find(selected[0].Type) is { } type)
        {
            var node = selected[0];
            ImGui.SeparatorText(type.Title);
            if (type.Description.Length > 0) ImGui.TextWrapped(type.Description);
            foreach (var property in type.Properties) DrawProperty(graph, node, property.Id, property.Name, property.Kind, property.Default, property.Choices);
            var linked = graph.Links.Where(link => link.ToNode == node.Id).Select(link => link.ToPin).ToHashSet();
            foreach (var pin in graph.PinsOf(node).Where(pin => !pin.Output && !linked.Contains(pin.Id)
                && pin.Type is not (PinType.Exec or PinType.Any or PinType.Numeric or PinType.Entity or PinType.Texture)))
            {
                var kind = pin.Type switch
                {
                    PinType.Bool => PropertyKind.Bool, PinType.Int => PropertyKind.Integer, PinType.Float => PropertyKind.Number,
                    PinType.Color => PropertyKind.Color, _ => PropertyKind.Text
                };
                DrawProperty(graph, node, pin.Id, pin.Name.Length > 0 ? pin.Name : pin.Id, kind, pin.Default ?? "", null);
            }
        }
        else if (selected.Count > 1) ImGui.TextDisabled($"{selected.Count} nodes selected. Right-click to align, distribute or collapse to a function.");

        ImGui.SeparatorText($"Problems ({diagnostics.Count})");
        foreach (var problem in diagnostics.Take(40))
        {
            var color = problem.Severity switch
            {
                DiagnosticSeverity.Error => new Vector4(1, .4f, .4f, 1), DiagnosticSeverity.Warning => new Vector4(1, .78f, .35f, 1), _ => StudioTheme.Muted
            };
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            if (ImGui.Selectable($"{problem.Message}##{problem.GetHashCode()}") && problem.NodeId is { } id)
            {
                if (graph.Scope?.Id != problem.Scope) graph.Enter(problem.Scope);
                graph.Selection.Clear();
                graph.Selection.Add(id);
            }
            ImGui.PopStyleColor();
        }
        if (output.Length > 0)
        {
            ImGui.SeparatorText("Output");
            ImGui.InputTextMultiline("##graph-output", ref output, 1 << 20, new Vector2(-1, 220), ImGuiInputTextFlags.ReadOnly);
        }
        ImGui.SeparatorText("Keys");
        ImGui.TextDisabled("Tab / right-click: add node\nDrag a pin into space: add linked node\nDouble-click wire: reroute\nC: comment  Ctrl+C/V/D: copy, paste, duplicate\nShift+W/A/S/D: align  Shift+H/V: distribute\nAlt+click pin: break links  Home: frame all");
    }

    /** The preview pins as a shaded ball, when there is no preview mesh. */
    private static void DrawSwatch(MaterialPreview swatch)
    {
        var draw = ImGui.GetWindowDrawList();
        var at = ImGui.GetCursorScreenPos();
        float radius = 46;
        var center = at + new Vector2(radius + 4, radius + 4);
        var color = new Vector4((float)swatch.BaseColor[0], (float)swatch.BaseColor[1], (float)swatch.BaseColor[2], (float)swatch.Opacity);
        var emissive = new Vector4((float)swatch.Emissive[0], (float)swatch.Emissive[1], (float)swatch.Emissive[2], 0);
        // A shaded ball: base colour with a soft key light, a roughness-sized highlight and emissive on top.
        for (int ring = 12; ring >= 1; ring--)
        {
            float t = ring / 12f;
            var shade = Vector4.Clamp(color * (.35f + .65f * (1 - t * t)) + emissive, Vector4.Zero, Vector4.One) with { W = color.W };
            draw.AddCircleFilled(center - new Vector2(radius * .25f, radius * .25f) * (1 - t), radius * t, ImGui.GetColorU32(shade), 48);
        }
        float highlight = radius * (.12f + .45f * (float)swatch.Roughness);
        var spec = Vector4.Lerp(Vector4.One, color, (float)swatch.Metalness) with { W = (float)(.55 * (1 - swatch.Roughness)) };
        draw.AddCircleFilled(center - new Vector2(radius * .38f, radius * .38f), highlight, ImGui.GetColorU32(spec), 32);
        ImGui.Dummy(new Vector2(radius * 2 + 8, radius * 2 + 8));
        ImGui.SameLine();
        ImGui.TextDisabled($"rough {swatch.Roughness:0.##}\nmetal {swatch.Metalness:0.##}\nopacity {swatch.Opacity:0.##}");
    }

    /** Points Material Output at a material, as an undoable edit. */
    private static void SetTarget(GraphEditor graph, uint tag)
    {
        if (graph.Scope is not null) graph.Enter(null);
        string value = $"0x{tag:X8}";
        if (graph.Nodes.FirstOrDefault(node => node.Type == NodeLibrary.MaterialOutput) is { } output) graph.SetProperty(output.Id, "target", value);
        else graph.AddNode(NodeLibrary.MaterialOutput, 0, 0, new Dictionary<string, string> { ["target"] = value });
    }

    private void DrawProperty(GraphEditor graph, GraphNode node, string key, string name, PropertyKind kind, string fallback, IReadOnlyList<string>? choices)
    {
        string id = node.Id + "/" + key, value = node.Get(key, fallback);
        ImGui.PushID(id);
        switch (kind)
        {
            case PropertyKind.Bool:
                bool on = value is "true" or "1" or "True";
                if (ImGui.Checkbox(name, ref on)) graph.SetProperty(node.Id, key, on ? "true" : "false");
                break;
            case PropertyKind.Choice when choices is { Count: > 0 }:
                if (ImGui.BeginCombo(name, value))
                {
                    foreach (string choice in choices)
                        if (ImGui.Selectable(choice, choice == value)) graph.SetProperty(node.Id, key, choice);
                    ImGui.EndCombo();
                }
                break;
            case PropertyKind.Color:
                var parsed = GraphValue.Parse(PinType.Color, value);
                var color = new Vector4((float)parsed.X, (float)parsed.Y, (float)parsed.Z, (float)parsed.W);
                if (ImGui.ColorEdit4(name, ref color, ImGuiColorEditFlags.Float))
                    drafts[id] = string.Create(CultureInfo.InvariantCulture, $"{color.X},{color.Y},{color.Z},{color.W}");
                if (ImGui.IsItemDeactivatedAfterEdit() && drafts.Remove(id, out var picked)) graph.SetProperty(node.Id, key, picked);
                break;
            default:
                string text = drafts.TryGetValue(id, out var draft) ? draft : value;
                bool changed = kind == PropertyKind.Code
                    ? ImGui.InputTextMultiline(name, ref text, 1 << 16, new Vector2(-1, 120))
                    : ImGui.InputText(name, ref text, 512, kind is PropertyKind.Number or PropertyKind.Integer ? ImGuiInputTextFlags.CharsScientific : ImGuiInputTextFlags.None);
                if (changed) drafts[id] = text;
                if (ImGui.IsItemDeactivatedAfterEdit() && drafts.Remove(id, out var typed) && typed != value) graph.SetProperty(node.Id, key, typed);
                break;
        }
        ImGui.PopID();
    }
}
