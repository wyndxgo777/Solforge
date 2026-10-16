using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    // Page ids: "live", "asset:<page>", "world:scene", "world:mission", "world:report", "world:asset:<page>".
    private string? shownPage;
    private StudioView? restoringView;
    private int restoringFrames;
    // A restored tab can take a few frames to open; after this many the view is recorded as it is.
    private const int RestoreFrameLimit = 30;

    private void HandleNavigationShortcuts()
    {
        if (ShortcutInput.Pressed(ShortcutActions.NavigateBack)) NavigateBack();
        if (ShortcutInput.Pressed(ShortcutActions.NavigateForward)) NavigateForward();
    }

    private void NavigateBack() { if (workspace.CanNavigateBack) workspace.Run(workspace.NavigateBack); }
    private void NavigateForward() { if (workspace.CanNavigateForward) workspace.Run(workspace.NavigateForward); }

    /** @param top The toolbar's control top; SameLine alone would put Forward on the logo's top. */
    private void DrawNavigationButtons(float top)
    {
        ImGui.BeginDisabled(!workspace.CanNavigateBack);
        string back = ShortcutInput.Text(ShortcutActions.NavigateBack);
        if (ImGuiIcons.Button("navigate-back", StudioIcon.Back, back.Length == 0 ? "Back" : $"Back ({back})")) NavigateBack();
        ImGui.EndDisabled(); ImGui.SameLine(); ImGui.SetCursorPosY(top);
        ImGui.BeginDisabled(!workspace.CanNavigateForward);
        string forward = ShortcutInput.Text(ShortcutActions.NavigateForward);
        if (ImGuiIcons.Button("navigate-forward", StudioIcon.Forward, forward.Length == 0 ? "Forward" : $"Forward ({forward})"))
            NavigateForward();
        ImGui.EndDisabled();
    }

    /** Runs after the frame is drawn: applies a view that navigation arrived at, else records the shown one. */
    private void TrackNavigation()
    {
        if (workspace.IsBusy) return;
        if (workspace.TakePendingView() is { } arrived)
        {
            restoringView = arrived; restoringFrames = 0;
            ApplyView(arrived);
            return;
        }
        var view = new StudioView(shownPage, selectedWorldId);
        if (restoringView is { } target)
        {
            bool done = (target.Page is null || target.Page == view.Page) && target.Selection == view.Selection;
            if (!done && ++restoringFrames < RestoreFrameLimit) return;
            restoringView = null;
        }
        workspace.NoteView(view);
    }

    /** Opens the recorded tab and selects the recorded object; a missing object clears the selection. */
    private void ApplyView(StudioView view)
    {
        switch (view.Page)
        {
            case "live": focusLive = true; break;
            case "local-server": focusLocalServer = true; break;
            case "world:scene": focusWorldScene = true; break;
            case "world:mission": focusMissionPage = true; break;
            case "world:report": focusWorldReport = true; break;
            case { } page when page.StartsWith("world:asset", StringComparison.Ordinal):
                focusAssetPage = true;
                if (page.Length > "world:asset:".Length) focusDocumentPage = page["world:asset:".Length..];
                break;
            case { } page when page.StartsWith("asset:", StringComparison.Ordinal):
                focusAssets = true; focusDocumentPage = page["asset:".Length..];
                break;
        }
        if (view.Selection is { } id && inspectionWorld?.Find(id) is { } row) SelectWorldObject(row);
        else if (selectedWorldId is not null)
        {
            viewport?.SetSelection(null);
            OnViewportSelection(null);
        }
    }
}
