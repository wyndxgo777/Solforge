using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/** MissionGraphPage part: editors for flow action nodes and the Add mission node template popup. */
public sealed partial class MissionGraphPage
{
    private MissionFlowAction? actionDraft;
    private string actionNode = "", actionError = "";
    // The Add mission node popup's draft; its Kind is the chosen template.
    private MissionFlowAction templateDraft = new(MissionNodeTemplate.ActionStep);
    private string templateLeg = "";
    private static readonly MissionNodeTemplate[] FlowActions = [MissionNodeTemplate.ActionStep,
        MissionNodeTemplate.FlowObjective, MissionNodeTemplate.FlowDialogue, MissionNodeTemplate.FlowState,
        MissionNodeTemplate.FlowArmTrigger, MissionNodeTemplate.FlowCompleteMission, MissionNodeTemplate.FlowStartScene,
        MissionNodeTemplate.FlowSceneInput];

    /** The step's action: a typed one with its Edit popup, or a custom one as Lua. */
    private void DrawFlowAction(MissionGraphNode node)
    {
        var action = visual!.ReadFlowAction(node.Key);
        ImGui.SeparatorText("Action");
        ImGui.TextUnformatted(action is null ? "Custom Lua" : TemplateName(action.Kind));
        if (action?.Scene is { } actionScene) ImGui.TextWrapped(actionScene.Name);
        if (action?.Target is { } target) ImGui.TextWrapped(target.Name);
        if (action?.Inputs is { Count: > 0 } sent) ImGui.TextWrapped("Then sends " + string.Join(", ", sent.Select(value => value.Name)));
        if (action is not null && UiAuto.SmallButton("Edit action"))
        { actionDraft = action; actionNode = node.Key; actionError = ""; ImGui.OpenPopup("Edit flow action"); }
        // A custom callback opens as code; a typed one keeps its code one click away.
        if (action is null) FieldLua(node.Key, "run");
        else if (ImGui.TreeNode("Lua##run")) { FieldLua(node.Key, "run"); ImGui.TreePop(); }
        if (!ImGui.BeginPopup("Edit flow action")) return;
        var draft = actionDraft!;
        ImGui.SetNextItemWidth(320);
        if (UiAuto.BeginCombo("Action type", TemplateName(draft.Kind)))
        {
            foreach (var kind in FlowActions)
                if (UiAuto.Selectable(TemplateName(kind), kind == draft.Kind)) draft = new(kind);
            UiAuto.EndCombo();
        }
        DrawActionFields(ref draft);
        actionDraft = draft;
        if (UiAuto.Button("Apply"))
        {
            if (Author(authoring => authoring.SetFlowAction(actionNode, draft))) ImGui.CloseCurrentPopup();
            else actionError = status;
        }
        ImGui.SameLine(); if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        if (actionError.Length > 0) ImGui.TextWrapped(actionError);
        ImGui.EndPopup();
    }

    /** Field pickers shared by the Add node and Edit action popups. */
    private void DrawActionFields(ref MissionFlowAction draft)
    {
        var kind = draft.Kind;
        if (kind is MissionNodeTemplate.FlowObjective or MissionNodeTemplate.FlowDialogue)
        {
            var role = kind == MissionNodeTemplate.FlowObjective ? MissionReferenceKind.DirectiveSensor : MissionReferenceKind.DialogueSensor;
            // A dialogue's cue belongs to its sensor, so a new sensor clears the cue.
            if (Pick("Sensor", draft.Sensor, visual!.Choices(role), out var sensor))
                draft = draft with { Sensor = sensor, Target = kind == MissionNodeTemplate.FlowDialogue ? null : draft.Target };
        }
        MissionReferenceKind? targetKind = kind switch
        {
            MissionNodeTemplate.ReachTrigger or MissionNodeTemplate.FlowArmTrigger or MissionNodeTemplate.TriggerFact => MissionReferenceKind.Trigger,
            MissionNodeTemplate.GhostScan => MissionReferenceKind.GhostLink, MissionNodeTemplate.Interact => MissionReferenceKind.Object,
            MissionNodeTemplate.FlowObjective => MissionReferenceKind.Directive, MissionNodeTemplate.FlowDialogue => MissionReferenceKind.Dialogue,
            MissionNodeTemplate.FlowState => MissionReferenceKind.State, _ => null
        };
        if (targetKind is { } targetType)
        {
            var choices = visual!.Choices(targetType);
            string? sensorExpression = draft.Sensor?.Expression;
            if (kind == MissionNodeTemplate.FlowDialogue) choices = choices.Where(value => value.Sensor == sensorExpression).ToArray();
            if (Pick("Target", draft.Target, choices, out var chosen)) draft = draft with { Target = chosen };
        }
        if (kind == MissionNodeTemplate.FlowObjective && Pick("Navpoint", draft.Navpoint,
            visual!.Choices(MissionReferenceKind.Navpoint), out var navpoint, true)) draft = draft with { Navpoint = navpoint };
        if (kind is not (MissionNodeTemplate.FlowStartScene or MissionNodeTemplate.FlowSceneInput or MissionNodeTemplate.SceneOutputFact))
            return;
        var scenes = kind switch
        {
            MissionNodeTemplate.FlowSceneInput => visual!.ScenesWithInputs(),
            MissionNodeTemplate.SceneOutputFact => visual!.ScenesWithOutputs(),
            _ => visual!.Choices(MissionReferenceKind.AuthoredScene)
        };
        if (Pick("Scene", draft.Scene, scenes, out var scene)) draft = draft with { Scene = scene, Target = null, Inputs = [] };
        if (kind == MissionNodeTemplate.FlowSceneInput)
        {
            if (Pick("Input", draft.Target, visual!.SceneInputs(draft.Scene), out var input)) draft = draft with { Target = input };
        }
        else if (kind == MissionNodeTemplate.SceneOutputFact)
        {
            if (Pick("Output", draft.Target, visual!.SceneOutputs(draft.Scene), out var output)) draft = draft with { Target = output };
        }
        else if (InputList(draft.Scene, draft.Inputs ?? [], out var inputs)) draft = draft with { Inputs = inputs };
    }

    private void ResetTemplate()
    {
        templateDraft = new(graph!.Group(groupKey).IsFlowFacts ? MissionNodeTemplate.TriggerFact : MissionNodeTemplate.ActionStep);
        templateLeg = "";
    }

    /** The Add mission node popup; the group decides which templates it offers. */
    private void DrawTemplatePopup()
    {
        if (!ImGui.BeginPopup("Add mission node")) return;
        var group = graph!.Group(groupKey);
        var templates = group.IsCampaignSteps
            ? new[] { MissionNodeTemplate.ActionStep, MissionNodeTemplate.ReachTrigger, MissionNodeTemplate.GhostScan, MissionNodeTemplate.Interact }
            : group.IsFlowSteps ? [.. FlowActions]
            : group.IsFlowFacts ? [MissionNodeTemplate.TriggerFact, MissionNodeTemplate.SceneOutputFact] : [];
        ImGui.SetNextItemWidth(320); UiAuto.InputText("Node id", ref newId, 64);
        if (UiAuto.BeginCombo("Template", TemplateName(templateDraft.Kind)))
        {
            foreach (var item in templates)
                if (UiAuto.Selectable(TemplateName(item), item == templateDraft.Kind)) templateDraft = new(item);
            UiAuto.EndCombo();
        }
        if (templateDraft.Kind == MissionNodeTemplate.ReachTrigger) LegPicker(group, ref templateLeg);
        DrawActionFields(ref templateDraft);
        if (UiAuto.Button("Create"))
        {
            var draft = templateDraft;
            if (Change(document => templates.Length == 0 ? document.AddNode(groupKey, newId)
                : new MissionVisualAuthoring(document, references).CreateNode(groupKey, newId, draft.Kind,
                    draft.Target, templateLeg, draft.Sensor, draft.Navpoint, draft.Scene, draft.Inputs ?? [])))
            { selected = groupKey + "/" + newId; ImGui.CloseCurrentPopup(); }
        }
        ImGui.SameLine(); if (UiAuto.Button("Cancel")) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static string TemplateName(MissionNodeTemplate value) => value switch
    {
        MissionNodeTemplate.ActionStep => "Immediate step", MissionNodeTemplate.ReachTrigger => "Reach trigger",
        MissionNodeTemplate.GhostScan => "Complete ghost scan", MissionNodeTemplate.Interact => "Use interactable",
        MissionNodeTemplate.FlowObjective => "Show objective", MissionNodeTemplate.FlowDialogue => "Play dialogue",
        MissionNodeTemplate.FlowState => "Select state", MissionNodeTemplate.FlowArmTrigger => "Arm trigger",
        MissionNodeTemplate.FlowCompleteMission => "Complete mission",
        MissionNodeTemplate.FlowStartScene => "Start scene", MissionNodeTemplate.FlowSceneInput => "Send scene input",
        MissionNodeTemplate.TriggerFact => "Observe trigger", MissionNodeTemplate.SceneOutputFact => "Scene output", _ => value.ToString()
    };
}
