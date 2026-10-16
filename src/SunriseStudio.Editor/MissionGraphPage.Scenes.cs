using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/** MissionGraphPage part: scene start and scene output editors for campaign steps. */
public sealed partial class MissionGraphPage
{
    /** A campaign step's scene starts with the step; its inputs are sent right after. */
    private bool DrawStepScene(MissionGraphNode node)
    {
        ImGui.SeparatorText("Scene");
        if (ReferenceField("Start scene", node.Key, "scene", MissionReferenceKind.AuthoredScene,
            (authoring, chosen) => authoring.SetStepScene(node.Key, chosen))) return true;
        var scene = Current(node.Key, "scene");
        var inputs = visual!.StepInputs(node.Key);
        if (!inputs.Editable) { ImGui.TextDisabled("Inputs: Custom Lua"); return FieldLua(node.Key, "inputs"); }
        if (scene is null) return false;
        var own = inputs.Inputs.Where(value => value.Scene == MissionVisualAuthoring.SceneKey(scene)).ToArray();
        if (own.Length != inputs.Inputs.Count)
        {
            ImGui.TextWrapped("Some inputs belong to another scene.");
            if (UiAuto.SmallButton("Remove them")) { Author(authoring => authoring.SetStepInputs(node.Key, own)); return true; }
            return false;
        }
        if (!InputList(scene, inputs.Inputs, out var next)) return false;
        Author(authoring => authoring.SetStepInputs(node.Key, next)); return true;
    }

    /** Choosing another scene clears the output until one of that scene's outputs is chosen. */
    private bool DrawSceneOutput(MissionGraphNode node)
    {
        var field = graph!.Field(node.Key, "ends.output");
        var (slot, output) = visual!.StepOutput(node.Key);
        if (field is not null && (slot is null || output is null))
        { ImGui.TextDisabled("Scene output: Custom Lua"); return FieldLua(node.Key, "ends.output"); }
        var shown = slot ?? outputScene;
        if (Pick("Output scene", shown, visual.ScenesWithOutputs(), out var scene, true) && scene != shown)
        {
            outputScene = scene;
            if (field is not null) { Author(authoring => authoring.SetSceneOutput(node.Key, null)); return true; }
        }
        if (!Pick("Scene output", output, visual.SceneOutputs(shown), out var chosen, true)) return false;
        Author(authoring => authoring.SetSceneOutput(node.Key, chosen)); return true;
    }

    /** One checkbox per input of the scene; the list keeps the order they were ticked in. */
    private bool InputList(MissionSdkReference? scene, IReadOnlyList<MissionSdkReference> current, out IReadOnlyList<MissionSdkReference> chosen)
    {
        chosen = current;
        ImGui.TextDisabled("Inputs");
        if (scene is null) { ImGui.TextDisabled("Choose a scene first"); return false; }
        var inputs = visual!.SceneInputs(scene);
        if (inputs.Count == 0) { ImGui.TextDisabled("This scene has no inputs"); return false; }
        foreach (var input in inputs)
        {
            bool ticked = current.Contains(input);
            bool changed = UiAuto.Checkbox(input.Name + "##input-" + input.Expression, ref ticked);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(input.Expression + "\nKey " + input.Identity);
            if (!changed) continue;
            chosen = ticked ? [.. current, input] : current.Where(value => value != input).ToArray();
            return true;
        }
        return false;
    }
}
