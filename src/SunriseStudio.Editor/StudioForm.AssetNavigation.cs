using ImGuiNET;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    /** Related assets retain their animation owner; Back and Forward live on the toolbar. */
    private void DrawAssetHeader(StudioDocument document)
    {
        ImGui.TextUnformatted(document.Name);
        if (document.Connections is { EntityCandidates.Count: > 0 } connections
            && (connections.EntityCandidates.Count > 1 || connections.EntityCandidates[0] != document.Entry.TagHash))
        {
            uint owner = connections.AnimationEntityTag != 0 ? connections.AnimationEntityTag
                : document.OpenContext?.EntityTag ?? 0;
            string label = owner == 0 ? "Choose entity owner" : workspace.DisplayName(owner);
            ImGui.SetNextItemWidth(-1);
            if (UiAuto.BeginCombo("##animation-owner", label))
            {
                foreach (uint candidate in connections.EntityCandidates)
                    if (UiAuto.Selectable(workspace.DisplayName(candidate) + "##" + candidate, candidate == owner))
                    {
                        var nextContext = document.OpenContext is { WeaponItemTag: > 0 } weapon
                            ? weapon with { EntityTag = candidate } : new AssetOpenContext(EntityTag: candidate);
                        workspace.Select(document.Entry.TagHash, context: nextContext);
                        ShowAssetPage("sunrise.preview");
                    }
                UiAuto.EndCombo();
            }
        }
    }
}
