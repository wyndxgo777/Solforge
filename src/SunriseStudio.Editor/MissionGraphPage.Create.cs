using ImGuiNET;
using SunriseStudio.Missions;
using static SunriseStudio.Editor.StudioUi;

namespace SunriseStudio.Editor;

public sealed partial class MissionGraphPage
{
    private bool showNewScript;
    private MissionSdkActivity? newActivity;
    // The picked activity's mission module.
    private string? newModule;
    // Why a flow script cannot run here; read when the popup opens.
    private string newFlowProblem = "";
    private MissionSdkReference? newInitialState;
    // The picked activity's states; read once when it is picked.
    private IReadOnlyList<MissionSdkReference> newStates = [];
    private string newGraphKey = "mission", newActivitySearch = "";
    private bool newInProject;

    /**
     * Starts an unsaved script. Nothing is written until Save.
     * @param inProject True for a mod script in the open project; false for an installed script.
     */
    public void CreateFlow(StudioWorkspace workspace, MissionSdkActivity activity, MissionSdkReference initialState, string key,
        bool inProject)
    {
        if (workspace.Catalog is null) throw new InvalidOperationException("Open a Sunrise installation first.");
        if (inProject && (!workspace.CanEdit || workspace.Project is null)) throw new InvalidOperationException("Create or open a project first.");
        if (IsDirty) throw new InvalidOperationException("Save or discard the current mission draft first.");
        var currentCatalog = catalog is not null && string.Equals(catalog.GameRoot, workspace.Catalog.GameRoot, StringComparison.OrdinalIgnoreCase)
            ? catalog! : new MissionScriptCatalog(workspace.Catalog.GameRoot);
        var draft = MissionScriptFactory.CreateFlow(currentCatalog, activity, initialState, key);
        string target = inProject ? MissionProject.PathFor(workspace.Project!, draft.Entry.RelativePath)
            : currentCatalog.InstalledPath(draft.Entry.RelativePath);
        if (File.Exists(target))
            throw new InvalidOperationException((inProject ? "The project" : "The installation") + " already has a script for this activity. Open it instead.");
        var priorCatalog = catalog; var priorProject = project; var priorWorkspace = workspaceOwner;
        try
        {
            catalog = currentCatalog; project = workspace.Project; workspaceOwner = workspace;
            Adopt(draft.Entry with { FullPath = target }, inProject ? ScriptHome.Project : ScriptHome.Installed, false, draft.Graph);
        }
        catch { catalog = priorCatalog; project = priorProject; workspaceOwner = priorWorkspace; throw; }
        IsDirty = true;
    }

    /** The New mission script popup: activity, arrival region, graph key and where to save. */
    private void DrawNewScript(StudioWorkspace workspace)
    {
        if (showNewScript)
        {
            showNewScript = false; newActivity = null; newModule = null; newInitialState = null; newStates = [];
            newActivitySearch = ""; newGraphKey = "mission"; Note(""); newInProject = workspace.CanEdit;
            newFlowProblem = catalog is null ? "" : MissionScriptFactory.FlowRuntimeProblem(catalog);
            ImGui.OpenPopup("New mission script");
        }
        if (!ImGui.BeginPopupModal("New mission script", ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (catalog is not { } installed) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
        if (newFlowProblem.Length > 0) Problem(newFlowProblem);
        ImGui.SetNextItemWidth(360);
        if (UiAuto.BeginCombo("Activity", newActivity is null ? "Choose activity" : Friendly(newActivity.Key.ToLowerInvariant())))
        {
            ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##find-activity", "Find activity", ref newActivitySearch, 128);
            foreach (var activity in installed.Activities.Where(value => newActivitySearch.Length == 0
                || value.Key.Contains(newActivitySearch, StringComparison.OrdinalIgnoreCase)).Take(150))
                if (UiAuto.Selectable(Friendly(activity.Key.ToLowerInvariant()) + "##" + activity.Key, activity == newActivity))
                {
                    Try(() =>
                    {
                        var entry = installed.EntryForActivity(activity);
                        // The same tables as Adopt, so creating the script reuses this read.
                        newStates = installed.ReadReferences(entry, installed.Api?.MissionTables)
                            .Where(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.State)).ToArray();
                        newActivity = activity; newModule = entry.SdkModule; newInitialState = null; newGraphKey = entry.Name;
                    });
                }
            UiAuto.EndCombo();
        }
        if (installed.SdkProblem(newModule) is { Length: > 0 } sdk) Problem(sdk);
        if (Pick("Arrival region", newInitialState, newStates, out var state)) newInitialState = state;
        Tip("Where the player starts. Change it later with a Select state action.");
        ImGui.SetNextItemWidth(360); UiAuto.InputText("Graph key", ref newGraphKey, 128);
        ImGui.TextDisabled("Save to");
        if (UiAuto.RadioButton("Installed scripts", !newInProject)) newInProject = false;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A normal mission's script. It belongs to no project.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (UiAuto.RadioButton("This project (mod)", newInProject)) newInProject = true;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(workspace.CanEdit ? "A script the project's mod installs." : "Open a project to add a mod script.");
        if (status.Length > 0)
        {
            if (statusFailed) Problem(status);
            else ImGui.TextWrapped(status);
        }
        ImGui.BeginDisabled(newActivity is null || newInitialState is null || string.IsNullOrWhiteSpace(newGraphKey)
            || newFlowProblem.Length > 0);
        if (UiAuto.Button("Create script") && Try(() => CreateFlow(workspace, newActivity!, newInitialState!, newGraphKey, newInProject)))
            ImGui.CloseCurrentPopup();
        Tip(newFlowProblem);
        ImGui.EndDisabled(); ImGui.SameLine();
        if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
