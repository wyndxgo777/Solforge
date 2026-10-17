using System.Numerics;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** Draws any workflow as one modal: limits, required fields, optional fields, problems and Create. */
public sealed class CreationDialog
{
    // The title bar shows the workflow title; the id after ### keeps one popup identity.
    private const string PopupId = "###creation-workflow";
    private CreationWorkflow? workflow;
    private CreationFollowUp? followUp;
    private bool open, anchoring, working, closing;

    public bool IsOpen => workflow is not null;

    /** Opens the modal on a workflow. */
    public void Open(CreationWorkflow value)
    {
        workflow = value; followUp = null;
        open = true; anchoring = working = closing = false;
        CreationWidgets.Reset();
    }

    /**
     * Draws the modal and finishes deferred steps.
     * @param pickFile Shows a file dialog for a filter and hands back the chosen path.
     * @param adopt Opens what a finished workflow asks for; runs only while the workspace is idle.
     */
    public void Draw(StudioWorkspace workspace, float scale, Action<string, Action<string>> pickFile,
        Action<CreationFollowUp> adopt)
    {
        CreationWidgets.Scale = scale;
        if (workflow is null)
        {
            if (followUp is { } next && !workspace.IsBusy) { followUp = null; adopt(next); }
            return;
        }
        if (open) { ImGui.OpenPopup(PopupId); open = false; }
        var display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(display * .5f, ImGuiCond.Appearing, new Vector2(.5f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(720 * scale, display.X - 40), Math.Min(700 * scale, display.Y - 40)),
            ImGuiCond.Appearing);
        // Only Cancel and finish end a workflow; a loading frame that closed the popup reopens it with the draft.
        if (!ImGui.BeginPopupModal(workflow.Title + PopupId, ImGuiWindowFlags.NoSavedSettings)) { open = true; return; }
        Finish(workspace);
        if (closing) { closing = false; workflow = null; ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        // A failed read waits for Retry instead of rereading every frame.
        IReadOnlyList<string> problems;
        try
        {
            if (workflow.LoadError.Length == 0) workflow.Update(workspace);
            problems = workflow.Problems(workspace);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { workflow.LoadError = error.Message; problems = [error.Message]; }
        string loadError = workflow.LoadError;
        // Lists are empty while their data loads or after it failed; say which.
        string? emptyList = loadError.Length != 0 ? "Could not read this list. Press Retry below."
            : workflow.IsLoading(workspace) ? "Loading..." : null;
        bool showError = workspace.Error.Length != 0 && workspace.Error != loadError;
        int empty = problems.Count(IsEmptyRequired);
        string? first = problems.FirstOrDefault(problem => !IsEmptyRequired(problem));
        string allProblems = string.Join("\n", problems);
        float footer = ((first is null ? 0 : 2) + (loadError.Length != 0 ? 1 : 0) + (empty != 0 ? 1 : 0) + (showError ? 2 : 0) + 3)
            * ImGui.GetTextLineHeightWithSpacing();
        ImGui.BeginChild("##creation-fields", new Vector2(0, -footer), ImGuiChildFlags.None);
        ImGui.PushTextWrapPos(0);
        var limits = workflow.Limits;
        if (limits.Count != 0 && UiAuto.CollapsingHeader($"Limits ({limits.Count})###limits"))
            foreach (string limit in limits) ImGui.BulletText(limit);
        bool any = false;
        foreach (var field in workflow.Fields.Where(field => field.Visible && field.Required))
        {
            if (!any) { ImGui.SeparatorText("REQUIRED"); any = true; }
            DrawField(workspace, field, pickFile, emptyList);
        }
        any = false;
        foreach (var field in workflow.Fields.Where(field => field.Visible && !field.Required))
        {
            if (!any) { ImGui.SeparatorText("OPTIONAL"); any = true; }
            DrawField(workspace, field, pickFile, emptyList);
        }
        ImGui.PopTextWrapPos();
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.PushTextWrapPos(0);
        if (first is not null)
        {
            int more = problems.Count - empty - 1;
            ImGui.TextColored(StudioTheme.Accent, more > 0 ? $"{first} (+{more})" : first);
            StudioUi.Tip(allProblems);
        }
        if (loadError.Length != 0)
        {
            ImGui.BeginDisabled(workspace.IsBusy);
            if (UiAuto.Button("Retry", new Vector2(120 * scale, 0)))
            { if (workspace.Error == loadError) workspace.Error = ""; workflow.Retry(); }
            ImGui.EndDisabled();
        }
        if (empty != 0)
        {
            ImGui.TextDisabled(empty == 1 ? "1 required field empty" : $"{empty} required fields empty");
            StudioUi.Tip(allProblems);
        }
        if (showError) ImGui.TextUnformatted(workspace.Error);
        ImGui.PopTextWrapPos();
        ImGui.BeginDisabled(problems.Count != 0 || workspace.IsBusy || anchoring || working);
        if (UiAuto.Button(workflow.CreateLabel, new Vector2(170 * scale, 0))) Start(workspace);
        StudioUi.Tip(allProblems);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Button(working ? "Close" : "Cancel", new Vector2(120 * scale, 0)))
        { anchoring = working = false; followUp = null; workflow = null; ImGui.CloseCurrentPopup(); }
        if (working && ImGui.IsItemHovered()) ImGui.SetTooltip("The import continues in the background");
        if (anchoring || working) { ImGui.SameLine(); ImGui.TextDisabled("Working..."); }
        ImGui.EndPopup();
    }

    /** Matches the text CreationField.Problems gives an empty required field. */
    private static bool IsEmptyRequired(string problem) => problem.EndsWith(" is required.", StringComparison.Ordinal);

    /** ApplyAssetTransaction needs an open document, so the anchor opens first when none is. */
    private void Start(StudioWorkspace workspace)
    {
        workspace.Error = "";
        if (workflow!.AnchorTag is { } anchor && workspace.Document is null)
        {
            workspace.Run(() => workspace.Select(anchor));
            anchoring = workspace.Error.Length == 0;
            return;
        }
        Commit(workspace);
    }

    private void Commit(StudioWorkspace workspace)
    {
        CreationFollowUp? result = null;
        workspace.Run(() => result = workflow!.Create(workspace));
        if (workspace.Error.Length != 0) return;
        followUp = result;
        if (result is { Background: true }) working = true;
        else closing = true;
    }

    /** Waits for the anchor to open, and for background imports to report success or failure. */
    private void Finish(StudioWorkspace workspace)
    {
        if (workspace.IsBusy) return;
        if (anchoring)
        {
            anchoring = false;
            if (workspace.Document is null) { if (workspace.Error.Length == 0) workspace.Error = "The base asset did not open."; }
            else Commit(workspace);
        }
        else if (working)
        {
            working = false;
            if (workspace.Error.Length == 0) closing = true;
            else followUp = null;
        }
    }

    /** @param emptyList What an empty list says instead of No matches; null when its data is loaded. */
    private static void DrawField(StudioWorkspace workspace, CreationField field, Action<string, Action<string>> pickFile,
        string? emptyList)
    {
        ImGui.PushID(field.Label);
        CreationWidgets.Label(field);
        float scale = CreationWidgets.Scale;
        if (field.Part is { } part)
        {
            DrawPartToggle(field, part);
            if (field.MakeNew)
            {
                ImGui.Indent();
                foreach (var input in part.NewFields.Where(input => input.Visible)) DrawField(workspace, input, pickFile, emptyList);
                ImGui.Unindent();
                ImGui.PopID();
                return;
            }
        }
        int revision = HashCode.Combine(workspace.AssetRevision, workspace.ProjectRevision, workspace.InvestmentRevision,
            workspace.NameRevision);
        switch (field.Kind)
        {
            case CreationFieldKind.Tag when field.Rows is { } rows:
                uint? row = field.Tag; string rowLabel = field.TagLabel;
                if (CreationWidgets.RowPicker(field.Label, ref row, ref rowLabel, rows(), field.RowFilter, revision,
                    workspace.IsProjectTag, empty: emptyList))
                { field.Tag = row; field.TagLabel = rowLabel; }
                break;
            case CreationFieldKind.Tag when field.Search is { } search:
                uint? tag = field.Tag; string label = field.TagLabel;
                if (CreationWidgets.Picker(field.Label, ref tag, ref label, search, revision, empty: emptyList))
                { field.Tag = tag; field.TagLabel = label; }
                break;
            case CreationFieldKind.File:
                string path = field.Text;
                ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
                if (UiAuto.InputText("##path", ref path, 4096)) field.Text = path;
                ImGui.SameLine();
                if (StudioUi.IconButton("browse", StudioIcon.Open, "Browse")) pickFile(field.FileFilter, chosen => field.Text = chosen);
                break;
            case CreationFieldKind.Scalar when field.Choices?.Invoke() is { Count: > 0 } choices:
                uint value = field.TryParse(out ulong parsed) ? (uint)parsed : uint.MaxValue;
                if (CreationWidgets.Choice("##choice", ref value, choices)) field.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case CreationFieldKind.Scalar:
                string number = field.Text;
                ImGui.SetNextItemWidth(160 * scale);
                if (UiAuto.InputText("##scalar", ref number, 32, field.Hexadecimal ? ImGuiInputTextFlags.CharsHexadecimal : ImGuiInputTextFlags.None))
                    field.Text = number;
                break;
            case CreationFieldKind.Text:
                string text = field.Text;
                bool edited = field.Lines > 1
                    ? UiAuto.InputTextMultiline("##text", ref text, 4096, new Vector2(-1, field.Lines * ImGui.GetTextLineHeight() + 8 * scale))
                    : UiAuto.InputText("##text", ref text, 512);
                if (edited) field.Text = text;
                break;
            case CreationFieldKind.Rows:
                field.DrawRows?.Invoke();
                break;
        }
        ImGui.Spacing();
        ImGui.PopID();
    }

    /** New | Pick existing on one line; a New with a blocker stays disabled and shows the blocker as its tooltip. */
    private static void DrawPartToggle(CreationField field, CreationPart part)
    {
        float width = 110 * CreationWidgets.Scale;
        ImGui.BeginDisabled(part.NewBlocker.Length != 0);
        if (UiAuto.Selectable(part.NewLabel, field.MakeNew, ImGuiSelectableFlags.None, new(width, 0))) field.MakeNew = true;
        StudioUi.Tip(part.NewBlocker);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (UiAuto.Selectable(part.PickLabel, !field.MakeNew, ImGuiSelectableFlags.None, new(width, 0))) field.MakeNew = false;
    }
}
