using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/**
 * The Checks tab's "Check script" and "Simulate" buttons (MissionHost): Lua syntax, keys and hashes against the game
 * data, the script library's check(), and a step walk through a fake game host in one of four modes. Both run the
 * saved file off the UI thread; results list with line numbers under the buttons. MCP: check_mission_script and
 * simulate_mission.
 */
public sealed partial class MissionGraphPage
{
    private static readonly string[] HostModeLabels = ["Play", "Skip cutscenes", "Cutscene never loads", "Player rejoins mid-mission"];
    private Task<MissionHostReport>? hostTask;
    private MissionHostReport? hostReport;
    private string hostError = "";
    private int hostMode;
    private bool hostShowTrace;

    /** Starts Check script or a simulation of the open script's saved file; false when nothing can run. */
    public bool StartHostRun(string? mode)
    {
        if (hostTask is { IsCompleted: false } || OpenScriptPath is not { } path || !stored || catalog is null) return false;
        string sdk = catalog.SdkRoot;
        hostError = "";
        hostTask = Task.Run(() => mode is null ? MissionHost.Check(path, sdk) : MissionHost.Simulate(path, sdk, mode));
        return true;
    }

    /** Buttons, mode picker and the last result; drawn at the top of the Checks tab. */
    private void DrawHost()
    {
        if (hostTask is { IsCompleted: true } done)
        {
            hostTask = null;
            if (done.IsCompletedSuccessfully) hostReport = done.Result;
            else hostError = done.Exception?.GetBaseException().Message ?? "The run failed.";
        }
        bool running = hostTask is not null, ready = !running && stored && catalog is not null;
        if (!ready) ImGui.BeginDisabled();
        if (UiAuto.Button("Check script")) StartHostRun(null);
        ImGui.SameLine();
        if (UiAuto.Button("Simulate")) StartHostRun(MissionHost.Modes[hostMode]);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        UiAuto.Combo("##host-mode", ref hostMode, HostModeLabels, HostModeLabels.Length);
        if (!ready) ImGui.EndDisabled();
        if (running) { ImGui.SameLine(); ImGui.TextDisabled("Running..."); }
        else if (!stored) { ImGui.SameLine(); ImGui.TextDisabled("Save the script first"); }
        else if (IsDirty) { ImGui.SameLine(); ImGui.TextDisabled("Runs the saved file; save to include edits"); }
        if (hostError.Length > 0) Problem(hostError);
        if (hostReport is { } report && string.Equals(report.ScriptPath, OpenScriptPath, StringComparison.OrdinalIgnoreCase))
            DrawHostReport(report);
        ImGui.Separator();
    }

    private void DrawHostReport(MissionHostReport report)
    {
        int problems = report.Problems.Count();
        string title = report.Mode == "check" ? "Check script" : "Simulate (" + HostModeLabels[Math.Max(0, Array.IndexOf(MissionHost.Modes, report.Mode))] + ")";
        if (problems == 0) ImGui.TextDisabled($"{title}: no problems");
        else Problem($"{title}: {problems} problem{(problems == 1 ? "" : "s")}");
        int row = 0;
        foreach (var record in report.Records)
        {
            if (record.Kind is "trace" or "step" or "module") continue;
            string text = $"{record.Kind,-8} {record}";
            if (record.IsProblem) ImGui.PushStyleColor(ImGuiCol.Text, ProblemColor);
            else if (record.Kind is "info" or "summary") ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            else ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            if (ImGui.Selectable($"{text}##host{row++}")) ImGui.SetClipboardText(record.ToString());
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy");
            ImGui.PopStyleColor();
        }
        if (report.Mode == "check") return;
        ImGui.Checkbox("Show step trace", ref hostShowTrace);
        if (!hostShowTrace) return;
        foreach (var record in report.Trace)
            ImGui.TextUnformatted(record.Kind == "step" ? $"{record.Text}  (line {record.Line})" : "    " + record.Text);
    }
}
