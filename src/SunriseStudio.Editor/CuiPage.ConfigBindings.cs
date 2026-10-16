using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** CuiPage part: finds the project config binding that drives a property and shows it on the field. */
public sealed partial class CuiPage
{
    private ProjectConfigBinding? PropertyBinding(StudioPageContext context, CuiDocument owner,
        CuiComponent component, CuiProperty property)
    {
        for (int i = 0; i < owner.Objects.Count; i++)
            for (int j = 0; j < owner.Objects[i].Components.Count; j++)
                if (ReferenceEquals(owner.Objects[i].Components[j], component))
                    return context.Workspace.ConfigBindingAt(owner.TagHash, ConfigBindingLimits.PropertyProfile, property.RecordOffset, i, j);
        return null;
    }

    private bool ConfiguredAxis(StudioPageContext context, int axis) => document is not null && selected >= 0
        && PropertyBinding(context, document, rectangles[selected].Component, rectangles[selected].Properties[axis]) is not null;

    private Vector4 ConfiguredRectangle(StudioPageContext context)
    {
        var result = edited;
        if (selected >= 0 && preview?.ConfiguredRectangles.TryGetValue(rectangles[selected].Component, out var configured) == true)
            for (int axis = 0; axis < 4; axis++)
                if (ConfiguredAxis(context, axis)) result[axis] = configured[axis];
        return result;
    }

    private static void ConfigLabel(StudioPageContext context, ProjectConfigBinding binding)
    {
        ImGui.TextDisabled($"Config: {binding.Key} = {context.Workspace.ConfigSetting(binding)?.Default}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Declared default preview. Saved game settings apply after restart.");
    }
}
