using System.Numerics;
using ImGuiNET;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

/**
 * The mission script's coverage report (in the Checks tab: squads, directives, dialogue lines, cutscenes, interactables and
 * scenes the SDK data has that the script never names; mission_coverage gives the same) and the Retail tab, which shows a
 * retail reference timeline's beats next to the script's steps with a covered checkbox per beat, saved in the project.
 */
public sealed partial class MissionGraphPage
{
    private const string RetailTabLabel = "Retail";
    private Task<MissionCoverageReport>? coverageTask;
    private string coverageKey = "";
    private bool coverageAll;
    // The Retail tab: the reference folder per (project, script), the loaded timeline and the marks file.
    private string retailFolderKey = "\0";
    private string? retailFolder;
    private RetailTimeline? retailTimeline;
    private RetailCoverage? retailMarks;
    private string retailError = "";

    /** The open script's SDK module file, or null when it names none. */
    private string? SdkModulePath => catalog is not null && script?.SdkModule is { } module
        ? Path.Combine(catalog.SdkRoot, module.Replace('.', Path.DirectorySeparatorChar) + ".lua") : null;

    /** The coverage section under the Checks tab's problems; it reads the SDK module off the UI thread. */
    private void DrawCoverage()
    {
        if (graph is null || script is null) return;
        ImGui.Separator();
        if (SdkModulePath is not { } path || !File.Exists(path)) { ImGui.TextDisabled("Coverage: this script names no installed SDK mission"); return; }
        string text = graph.Source;
        string key = $"{path}|{coverageAll}|{text.Length}|{text.GetHashCode(StringComparison.Ordinal)}";
        if (key != coverageKey || coverageTask is null)
        {
            coverageKey = key;
            string scriptPath = script.FullPath;
            bool all = coverageAll;
            coverageTask = Task.Run(() => MissionInsight.Coverage(MissionSdkData.Load(path),
                new MissionScriptFile(scriptPath, text, MissionInsight.RetailName(text)), all));
        }
        if (!coverageTask.IsCompleted) { ImGui.TextDisabled("Coverage: reading the SDK mission..."); return; }
        if (coverageTask.IsFaulted || coverageTask.IsCanceled)
        {
            ImGui.TextDisabled("Coverage did not read: " + (coverageTask.Exception?.GetBaseException().Message ?? "cancelled"));
            return;
        }
        var report = coverageTask.Result;
        if (!ImGui.CollapsingHeader($"Coverage: {report.UnusedCount} unused in the mission's data###mission-coverage")) return;
        if (UiAuto.Checkbox("Every prefab on the map##coverage-all", ref coverageAll)) coverageKey = "";
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: squads and interactables count only prefabs the script already names. On: every prefab in the SDK module.");
        if (!report.HasDialogueText) ImGui.TextDisabled("This mission's data has no dialogue text.");
        foreach (var category in report.Categories)
        {
            string scope = category.OutOfScope > 0 ? $", {category.OutOfScope} in other prefabs" : "";
            string label = $"{category.Name}: {category.Used}/{category.Total} used, {category.Unused.Count} unused{scope}###coverage-{category.Name}";
            if (category.Unused.Count == 0) { ImGui.BulletText(label[..label.IndexOf("###", StringComparison.Ordinal)]); continue; }
            if (!ImGui.TreeNode(label)) continue;
            foreach (var item in category.Unused.Take(400))
            {
                ImGui.TextWrapped(item.Label);
                ImGui.SameLine();
                ImGui.TextDisabled(item.Key);
                if (item.Detail is { Length: > 0 } detail)
                {
                    if (detail.StartsWith("no squad data", StringComparison.Ordinal)) Problem("    " + detail);
                    else ImGui.TextDisabled("    " + detail);
                }
            }
            if (category.Unused.Count > 400) ImGui.TextDisabled($"... {category.Unused.Count - 400} more (mission_coverage lists them all)");
            ImGui.TreePop();
        }
    }

    /** The Retail tab: beats of the mission's retail timeline beside its steps; checking a beat saves it in the project. */
    private void DrawRetail()
    {
        if (graph is null || script is null) return;
        string folderKey = (project?.DirectoryPath ?? "") + "|" + script.FullPath;
        if (folderKey != retailFolderKey)
        {
            retailFolderKey = folderKey;
            retailFolder = RetailTimeline.ReferenceFolder(project?.DirectoryPath, script.FullPath);
            retailTimeline = null; retailMarks = null; retailError = "";
            if (retailFolder is not null) retailMarks = RetailCoverage.Open(Path.GetDirectoryName(retailFolder)!);
        }
        if (retailFolder is null || retailMarks is null)
        {
            ImGui.TextDisabled("No .solforge\\reference folder in the project; retail timelines (<mission>-retail-timeline.md) live there.");
            return;
        }
        var marks = retailMarks;
        string folder = retailFolder;
        string scriptKey = Path.GetFileNameWithoutExtension(script.FullPath);
        string text = graph.Source;
        string? chosen = marks.Timeline(scriptKey) is { } picked ? Path.Combine(folder, picked + RetailTimeline.Suffix)
            : RetailTimeline.Find(folder, MissionInsight.RetailName(text));
        if (retailTimeline?.Path != chosen)
        {
            retailTimeline = null; retailError = "";
            if (chosen is not null)
                try { retailTimeline = RetailTimeline.Load(chosen); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { retailError = error.Message; }
        }
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X * .3f, 120, 240));
        if (UiAuto.BeginCombo("Timeline##retail-timeline", retailTimeline?.Key ?? "Choose a timeline"))
        {
            foreach (string file in RetailTimeline.Available(folder))
            {
                string name = Path.GetFileName(file)[..^RetailTimeline.Suffix.Length];
                if (UiAuto.Selectable(name + "##retail-" + name, string.Equals(file, chosen, StringComparison.OrdinalIgnoreCase)))
                    SaveRetail(() => marks.SetTimeline(scriptKey, name.ToLowerInvariant()));
            }
            UiAuto.EndCombo();
        }
        if (retailError.Length > 0) { Problem("Timeline did not read: " + retailError); return; }
        if (retailTimeline is not { } timeline)
        {
            ImGui.TextDisabled("No timeline matches this script's first comment ('-- Six. ...'); choose one.");
            return;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"{marks.CoveredCount(scriptKey, timeline)}/{timeline.Beats.Count} beats covered");
        if (retailError.Length == 0 && saveError.Length > 0) Problem("Not saved: " + saveError);

        var steps = RetailTimeline.Steps(text);
        float stepsWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * .22f, 120, 260);
        ImGui.BeginChild("retail-steps", new Vector2(stepsWidth, 0), ImGuiChildFlags.Borders);
        ImGui.TextDisabled($"Steps ({steps.Count})");
        foreach (string step in steps)
        {
            int beats = timeline.Beats.Count(beat => marks.Mark(scriptKey, beat.Time).Step == step);
            ImGui.TextUnformatted(beats > 0 ? $"{step}  ({beats})" : step);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("retail-beats", Vector2.Zero, ImGuiChildFlags.Borders);
        if (ImGui.BeginTable("retail-beat-table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Covered", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Beat", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Step", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();
            for (int i = 0; i < timeline.Beats.Count; i++)
            {
                var beat = timeline.Beats[i];
                var mark = marks.Mark(scriptKey, beat.Time);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool covered = mark.Covered;
                if (UiAuto.Checkbox($"##retail-covered-{i}", ref covered))
                    SaveRetail(() => marks.SetMark(scriptKey, beat.Time, mark with { Covered = covered }));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(beat.Time);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(beat.Beat);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (UiAuto.BeginCombo($"##retail-step-{i}", mark.Step ?? "-"))
                {
                    if (UiAuto.Selectable($"-##retail-none-{i}", mark.Step is null))
                        SaveRetail(() => marks.SetMark(scriptKey, beat.Time, mark with { Step = null }));
                    foreach (string step in steps)
                        if (UiAuto.Selectable($"{step}##retail-{i}-{step}", mark.Step == step))
                            SaveRetail(() => marks.SetMark(scriptKey, beat.Time, new RetailMark(true, step)));
                    UiAuto.EndCombo();
                }
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    // Why the last save of the marks failed; empty after a good save.
    private string saveError = "";

    /** Applies one change to the marks and writes the project's retail-coverage.json. */
    private void SaveRetail(Action change)
    {
        change();
        try { retailMarks?.Save(); saveError = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { saveError = error.Message; }
    }
}
