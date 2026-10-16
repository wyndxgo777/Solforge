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
    // New graph: start from an activity-type template (true) or a flow for an SDK activity (false).
    private bool newFromTemplate = true;
    private int newTemplate;
    private string newMissionKey = "", newMissionSearch = "", newName = "my_mission";

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
        AdoptNew(workspace, currentCatalog, draft, inProject);
    }

    /**
     * Starts an unsaved script of any activity type from its template (story, strike, patrol, public event, lost
     * sector, raid, encounter, pvp-style mode, quest or blank custom). Nothing is written until Save; a project script
     * then shows in Project changes and the build like any mod script.
     * @param missionKey The missions.lua key whose Slot, Squad and Directive names the script uses.
     * @return The script's path relative to its scripts folder.
     */
    public string CreateTemplate(StudioWorkspace workspace, MissionActivityType type, string missionKey, string name, bool inProject)
    {
        if (workspace.Catalog is null) throw new InvalidOperationException("Open a Sunrise installation first.");
        if (inProject && (!workspace.CanEdit || workspace.Project is null)) throw new InvalidOperationException("Create or open a project first.");
        if (IsDirty) throw new InvalidOperationException("Save or discard the current mission draft first.");
        var currentCatalog = catalog is not null && string.Equals(catalog.GameRoot, workspace.Catalog.GameRoot, StringComparison.OrdinalIgnoreCase)
            ? catalog! : new MissionScriptCatalog(workspace.Catalog.GameRoot);
        var draft = MissionScriptFactory.CreateTemplate(currentCatalog, type, missionKey, name);
        AdoptNew(workspace, currentCatalog, draft, inProject);
        return draft.Entry.RelativePath;
    }

    /** Adopts a new draft as an unsaved script at its target; refuses to shadow an existing file. */
    private void AdoptNew(StudioWorkspace workspace, MissionScriptCatalog currentCatalog, MissionScriptDraft draft, bool inProject)
    {
        string target = inProject ? MissionProject.PathFor(workspace.Project!, draft.Entry.RelativePath)
            : currentCatalog.InstalledPath(draft.Entry.RelativePath);
        if (File.Exists(target))
            throw new InvalidOperationException((inProject ? "The project" : "The installation") + " already has a script named " + draft.Entry.Name + ". Open it instead.");
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
        if (UiAuto.RadioButton("Activity type template", newFromTemplate)) newFromTemplate = true;
        Tip("A starter script for any activity: story, strike, patrol, public event, lost sector, raid, encounter, PvP-style mode, quest or blank.");
        ImGui.SameLine();
        if (UiAuto.RadioButton("Flow for an SDK activity", !newFromTemplate)) newFromTemplate = false;
        if (newFromTemplate) { DrawNewTemplate(workspace, installed); ImGui.EndPopup(); return; }
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

    /** The template half of New mission script: activity type, the mission whose names it uses, name and where to save. */
    private void DrawNewTemplate(StudioWorkspace workspace, MissionScriptCatalog installed)
    {
        var types = MissionTemplates.Types;
        newTemplate = Math.Clamp(newTemplate, 0, types.Count - 1);
        ImGui.SetNextItemWidth(360);
        if (UiAuto.BeginCombo("Activity type", types[newTemplate].Label))
        {
            for (int i = 0; i < types.Count; i++)
                if (UiAuto.Selectable(types[i].Label + "##" + types[i].Id, i == newTemplate)) newTemplate = i;
            UiAuto.EndCombo();
        }
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360);
        ImGui.TextDisabled(types[newTemplate].Description);
        ImGui.PopTextWrapPos();
        ImGui.SetNextItemWidth(360);
        if (UiAuto.BeginCombo("Mission (map)", newMissionKey.Length == 0 ? "Choose mission" : Friendly(newMissionKey.ToLowerInvariant())))
        {
            ImGui.SetNextItemWidth(-1); UiAuto.InputTextWithHint("##find-mission", "Find mission", ref newMissionSearch, 128);
            foreach (var row in installed.Missions.Where(value => newMissionSearch.Length == 0
                || value.Key.Contains(newMissionSearch, StringComparison.OrdinalIgnoreCase)).Take(200))
                if (UiAuto.Selectable(Friendly(row.Key.ToLowerInvariant()) + "##" + row.Key, row.Key == newMissionKey))
                {
                    newMissionKey = row.Key;
                    if (newName is "my_mission" or "") newName = row.Key.ToLowerInvariant() + "_" + types[newTemplate].Id;
                }
            UiAuto.EndCombo();
        }
        Tip("The script uses this mission's Slot, Squad and Directive names; Checks flags any the map does not have.");
        ImGui.SetNextItemWidth(360); UiAuto.InputText("Script name", ref newName, 120);
        ImGui.TextDisabled("Save to");
        if (UiAuto.RadioButton("Installed scripts##template", !newInProject)) newInProject = false;
        ImGui.SameLine();
        ImGui.BeginDisabled(!workspace.CanEdit);
        if (UiAuto.RadioButton("This project (mod)##template", newInProject)) newInProject = true;
        ImGui.EndDisabled();
        if (status.Length > 0)
        {
            if (statusFailed) Problem(status);
            else ImGui.TextWrapped(status);
        }
        ImGui.BeginDisabled(newMissionKey.Length == 0 || string.IsNullOrWhiteSpace(newName));
        if (UiAuto.Button("Create script##template")
            && Try(() => CreateTemplate(workspace, types[newTemplate].Type, newMissionKey, newName, newInProject)))
            ImGui.CloseCurrentPopup();
        ImGui.EndDisabled(); ImGui.SameLine();
        if (UiAuto.Button("Cancel##template")) ImGui.CloseCurrentPopup();
    }
}
