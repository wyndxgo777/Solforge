/**
 * File: the unified Graph panel. One panel replaces Mission Graph and Node Graph (their old panel ids map here, see
 * StudioLayout.Canonical) and shows the model for whatever was opened last: a mission script (MissionGraphPage, its
 * Lua) or a Solforge node graph (NodeGraphPanel, its .solgraph.json and compiled output). Both models draw the same
 * Nodes / source / Checks tabs (GraphViews) and keep nodes and source in two-way sync (GraphSourceSync).
 */
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Graphs;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private enum GraphModel { Mission, Node }

    private GraphModel graphModel = GraphModel.Mission;
    // What each model had open last frame; a change (an open from anywhere: toolbar, open_file, the AI) picks that model.
    private string? graphSeenScript, graphSeenNode;

    /** The model the panel shows: the one whose content changed most recently; an empty model yields to an open one. */
    private void PickGraphModel()
    {
        string? script = missionGraph.OpenScriptPath, node = nodeGraph.FilePath;
        if (script != graphSeenScript) { graphSeenScript = script; if (script is not null) graphModel = GraphModel.Mission; }
        if (node != graphSeenNode) { graphSeenNode = node; if (node is not null) graphModel = GraphModel.Node; }
        if (graphModel == GraphModel.Node && node is null && missionGraph.Document is not null) graphModel = GraphModel.Mission;
    }

    /** Undo, redo and save follow the model the panel shows. */
    private void SetGraphActive()
    {
        missionGraphActive = graphModel == GraphModel.Mission;
        nodeGraphActive = graphModel == GraphModel.Node;
    }

    /** True when neither model has anything open (the panel folds while empty). */
    private bool GraphPanelEmpty => missionGraph.ScriptPath is null && !missionGraph.IsDirty && nodeGraph.FilePath is null;

    /** The MCP graph_kind: mission_script, node_graph:material|weapon|mission, or null with nothing open. */
    private string? AiGraphKind()
    {
        PickGraphModel();
        return graphModel == GraphModel.Node
            ? nodeGraph.Kind is { } kind ? GraphModels.NodeGraph(kind) : null
            : missionGraph.Document is not null ? GraphModels.MissionScript : null;
    }

    /** get_open_documents' graph row: the active model and its file, unsaved state and problem count. */
    private JsonObject? AiGraphSummary()
    {
        if (AiGraphKind() is not { } kind) return null;
        string? path = graphModel == GraphModel.Node ? nodeGraph.FilePath : missionGraph.OpenScriptPath;
        return new JsonObject
        {
            ["graph_kind"] = kind,
            ["path"] = path is null ? null : AiShownPath(path),
            ["unsaved"] = graphModel == GraphModel.Node ? nodeGraph.IsDirty : missionGraph.IsDirty,
            ["problems"] = graphModel == GraphModel.Node ? JsonValue.Create(nodeGraph.ProblemCount) : null,
            ["tools"] = graphModel == GraphModel.Node ? "get_graph, edit_graph, compile_graph" : "read_panel mission_graph and the mission script tools"
        };
    }

    /**
     * create_mission_script (and create_graph with an activity): a template script for any activity type, saved into
     * the project's scripts (Project changes, the build) or the installed scripts, and shown in the Graph panel.
     */
    private string AiCreateMissionScript(JsonObject args)
    {
        if (workspace.Catalog is null) return "Open a Sunrise installation first.";
        bool inProject = McpJson.Bool(args, "in_project", true);
        if (inProject && (workspace.Project is null || !workspace.CanEdit)) return "No project is open, or Solforge is busy.";
        if (missionGraph.IsDirty) return AiDraftOpen;
        try
        {
            var type = SunriseStudio.Missions.MissionTemplates.Parse(McpJson.String(args, "activity"));
            string relative = missionGraph.CreateTemplate(workspace, type, McpJson.String(args, "mission") ?? "",
                McpJson.String(args, "name") ?? "", inProject);
            missionGraph.SaveCurrent(workspace);
            graphModel = GraphModel.Mission;
            focusMissionGraph = true;
            if (missionGraph.IsDirty) return $"Created {relative} in the Graph panel but it did not save; ask the user to check the Graph panel's status line.";
            var checks = missionGraph.CheckLines;
            return $"Created {relative} ({type} template) " + (inProject ? "in the project's scripts; it shows in Project changes and the next build. "
                    : "in the installed scripts. ")
                + "It is open in the Graph panel (graph_kind mission_script). "
                + (checks.Count == 0 ? "Checks: no problems." : $"Checks ({checks.Count}): " + string.Join(" | ", checks.Take(12)));
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException
            or IOException or UnauthorizedAccessException or ArgumentException)
        { return "Not created: " + error.Message; }
    }

    /** Draws the merged panel: the model switch, then that model's toolbar and Nodes / source / Checks tabs. */
    private void DrawGraphPanel(Vector2 position, Vector2 size)
    {
        PickGraphModel();
        nodeGraph.Code ??= missionGraph;
        bool focus = focusMissionGraph;
        focusMissionGraph = false;
        if (focus) ImGui.SetNextWindowFocus();
        // The window names stay the old panels' so UI automation that focuses "Mission graph area" keeps working.
        BeginPanel(graphModel == GraphModel.Mission ? "Mission graph area" : "Node graph area", position, size);
        if (focus || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) SetGraphActive();
        if (UiAuto.RadioButton("Mission script", graphModel == GraphModel.Mission)) { graphModel = GraphModel.Mission; SetGraphActive(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mission scripts (.lua): campaign and flow steps as nodes, the Lua, and Checks.");
        ImGui.SameLine();
        if (UiAuto.RadioButton("Node graph", graphModel == GraphModel.Node)) { graphModel = GraphModel.Node; SetGraphActive(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Solforge graphs (*.solgraph.json): material, weapon/perk and mission node graphs, their source and Checks.");
        if (AiGraphKind() is { } kind) { ImGui.SameLine(); ImGui.TextDisabled(kind); }
        if (graphModel == GraphModel.Mission) missionGraph.Draw(workspace);
        else nodeGraph.Draw(workspace.Project?.DirectoryPath, AiApplyGraph, (path, content) =>
            RunAiAction("apply_project_edit", new JsonObject { ["path"] = path, ["content"] = content }));
        ImGui.End();
    }
}
