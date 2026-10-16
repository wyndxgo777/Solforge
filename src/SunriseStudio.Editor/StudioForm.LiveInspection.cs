using System.Numerics;
using ImGuiNET;
using SunriseStudio.Live;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private readonly LiveInspectionIndex liveInspection = new();
    public LivePlacement? SelectedObservedPlacement => selectedObject is null ? null
        : liveInspection.Find(selectedObject)?.Observation ?? livePage.Observed(selectedObject);
    public string? SelectedLiveEntityKey => selectedObject is null ? null : liveInspection.Find(selectedObject)?.Key;

    /**
     * Observation revisions update metadata once; render filters use the cached source lookup. A new
     * selection asks the game once for that object's full record.
     */
    private void UpdateLiveInspection()
    {
        UpdateLiveCui();
        if (viewport is null) return;
        livePage.Inspect(SelectedObservedPlacement);
        string? oldKey = selectedObject is null ? null : liveInspection.Find(selectedObject)?.Key;
        bool changed = liveInspection.Update(viewport, livePage.MatchedPlacements, livePage.RuntimePlacements,
            livePage.RuntimeRevision, workspace.NameRevision, workspace.DisplayName);
        if (!changed) return;
        expandedTreeNodes.Add(LiveInspectionIndex.RootId);
        expandedTreeNodes.RemoveWhere(id => id.StartsWith(LiveInspectionIndex.RootId + "/", StringComparison.Ordinal)
            && liveInspection.Find(id) is null);
        treeRowsDirty = true;
        if (oldKey is not null && (selectedObject is null || liveInspection.Find(selectedObject)?.Key != oldKey))
        { viewport.SetSelection(null); OnViewportSelection(null); }
        else if (selectedObject is not null && liveInspection.Find(selectedObject) is not null)
            TrackTreeSelection(selectedObject, false);
        if (selectedTreeId?.StartsWith(LiveInspectionIndex.RootId + "/", StringComparison.Ordinal) == true
            && liveInspection.Find(selectedTreeId) is null) selectedTreeId = scrollTreeId = null;
    }

    /** Hands the live link to the CUI page and opens the screen Live Sync chose. */
    private void UpdateLiveCui()
    {
        if (pages.Pages.OfType<CuiPage>().FirstOrDefault() is { } cuiPage)
        {
            cuiPage.Live = livePage.Connection;
            cuiPage.LiveFollowing = livePage.FollowingCui;
            cuiPage.UpdateLive();
        }
        if (livePage.TakeCuiOpen() is not { } tag) return;
        workspace.Open(tag);
        ShowAssetPage("sunrise.cui");
    }

    private bool DrawRuntimeObjectInspector()
    {
        if (selectedObject is null || liveInspection.Find(selectedObject) is not { } item) return false;
        var row = item.Observation;
        bool standIn = row.Generation == uint.MaxValue, hasClass = row.ClassTag is not (0 or uint.MaxValue);
        ImGui.TextWrapped(item.Name);
        ImGui.TextDisabled(standIn ? "Drawn by the game only"
            : !row.ShowsModel ? (row.Dynamic ? "Spawned entity (marker)" : "Placed object (marker)")
            : "Spawned entity");
        StudioUi.Tip(standIn ? "Not in the game's object registry" : !row.ShowsModel ? "Not placed or hidden" : "");
        if (ImGuiIcons.Button("live-frame", StudioIcon.Focus, FrameTooltip))
        { viewport!.FocusSelection(); focusWorldScene = true; }
        ImGui.SameLine();
        bool hidden = liveInspection.IsHidden(item.Node.Id);
        if (ImGuiIcons.Button("live-hide", hidden ? StudioIcon.EyeOff : StudioIcon.Eye,
            hidden ? "Show entity and children" : "Hide entity and children")) SetSceneNodeHidden(item.Node.Id, !hidden);
        if (hasClass)
        {
            ImGui.SameLine();
            if (UiAuto.Button("Open class")) OpenObjectAsset(row.ClassTag);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{workspace.DisplayName(row.ClassTag)} ({row.ClassTag:X8})");
        }
        DrawObservedPlacement(row);
        ImGui.SeparatorText("Models");
        foreach (uint model in item.Meshes.Select(mesh => mesh.Mesh.TagHash)
            .Where(tag => tag is not (0 or uint.MaxValue or LiveDynamicBinding.MarkerTag)).Distinct())
        {
            int parts = item.Meshes.Count(mesh => mesh.Mesh.TagHash == model);
            if (UiAuto.Selectable($"{workspace.DisplayName(model)} ({parts} parts)##model-{model}")) OpenObjectAsset(model);
        }
        ImGui.TextDisabled(!livePage.AnimationEnabled ? "Rest pose"
            : livePage.SelectedPoseStatus.Length > 0 ? livePage.SelectedPoseStatus : "Waiting for the game's pose");
        if (!livePage.AnimationEnabled) StudioUi.Tip("Turn on Sync > Animation.");
        if (viewport!.SelectionMode == SceneSelectionMode.MeshPart && selectedObject is MeshInstance mesh)
        {
            ImGui.SeparatorText("Selected part");
            ImGui.TextUnformatted($"Part {mesh.Mesh.PartIndex} / LOD {mesh.Mesh.LodLevel}");
            if (UiAuto.SmallButton($"Mesh {mesh.Mesh.TagHash:X8}")) OpenObjectAsset(mesh.Mesh.TagHash);
            if (mesh.Mesh.MaterialTag is not (0 or uint.MaxValue)
                && UiAuto.SmallButton($"Material {mesh.Mesh.MaterialTag:X8}")) OpenObjectAsset(mesh.Mesh.MaterialTag);
        }
        return true;
    }

    /**
     * What the game reports for one object now. The pushed row gives the pose; the game's one answer
     * to the selection request gives parent, children, drawn models and the raw datum.
     */
    private void DrawObservedPlacement(LivePlacement row, bool holdingLastPose = false)
    {
        ImGui.SeparatorText("In game now (read only)");
        if (holdingLastPose) ImGui.TextDisabled("Last pose; latest read failed");
        if (row.AtOrigin) ImGui.TextColored(new Vector4(1, .75f, .35f, 1), "At the world origin");
        if (!ImGui.BeginTable("observed-placement", 2, ImGuiTableFlags.SizingStretchProp)) return;
        void Field(string label, string value)
        { ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextDisabled(label); ImGui.TableNextColumn(); ImGui.TextWrapped(value); }
        ulong now = (ulong)Environment.TickCount64;
        bool current = LiveWorldProtocol.Fresh(row.Time, now);
        Field("Sample", !row.TransformAvailable ? "Unavailable" : current ? $"{now - row.Time} ms old" : "Stale");
        Field("Position", row.TransformAvailable ? Vector(row.Position) : "Unavailable");
        if (row.TransformAvailable && livePage.Player is { } player) Field("From player", $"{Vector3.Distance(player, row.Position):0.#} m");
        Field("Rotation", row.TransformAvailable ? Vector(QuaternionToDegrees(row.Orientation)) + " deg" : "Unavailable");
        Field("World scale", row.TransformAvailable && row.Scale is { } scale ? scale.ToString("0.#####") : "Unavailable");
        Field("Kind", row.Dynamic ? "Spawned at run time" : $"Placed: list {row.ObjectListTag:X8}, entry {row.EntryIndex}");
        if (!row.Dynamic && row.Identity is not (0 or ulong.MaxValue)) Field("Placement id", $"{row.Identity:X16}");
        Field("Handle", $"{row.Handle:X8}, generation {row.Generation}");
        Field("Placed, not hidden", row.PlacedNotHidden is { } placed ? placed ? "Yes" : "No, drawn as a marker" : "Unknown");
        StudioUi.Tip("Built, linked into the world, not hidden.");
        Field("Native flags", row.TransformAvailable || row.PlacedNotHidden is not null ? $"0x{row.DatumFlags:X8}" : "Unavailable");
        StudioUi.Tip("Raw flags");
        ImGui.EndTable();
        DrawObjectAnswer(row);
        static string Vector(Vector3 value) => $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}";
    }

    /** The game's one answer for the selected object; Refresh asks again. */
    private void DrawObjectAnswer(LivePlacement row)
    {
        var answer = livePage.InspectedObject;
        bool matches = answer is not null && answer.Handle == row.Handle && answer.Generation == row.Generation;
        StudioUi.Heading("Game record", matches && answer!.Status == LiveObjectStatus.Complete && answer.PlacedNotHidden is not null
            ? $"Root {answer.RootHandle:X8}\nRoot flags 0x{answer.RootFlags:X8}\nChain flags 0x{answer.ChainFlags:X8}" : "");
        if (livePage.ObjectBridgeDiagnostic is { Length: > 0 } diagnostic)
        { ImGui.TextColored(new Vector4(1, .6f, .35f, 1), diagnostic); return; }
        if (UiAuto.SmallButton("Refresh##live-object")) livePage.RefreshInspected();
        StudioUi.Tip("Ask the game again");
        ImGui.SameLine();
        if (!matches) { ImGui.TextDisabled("Not requested"); return; }
        ulong now = (ulong)Environment.TickCount64;
        ImGui.TextDisabled(answer!.Status switch
        {
            LiveObjectStatus.Pending => "Waiting for the game",
            LiveObjectStatus.Absent => "Object is gone",
            LiveObjectStatus.Unavailable => "No placement scan in the game",
            _ => answer.Time <= now ? $"Read {now - answer.Time} ms ago" : "Read just now"
        });
        if (answer.Status != LiveObjectStatus.Complete) return;
        if (answer.ParentHandle != uint.MaxValue)
        {
            var parent = livePage.RowByHandle(answer.ParentHandle);
            string name = parent is null ? $"{answer.ParentHandle:X8} (not in the registry)" : livePage.DisplayName(workspace, parent);
            ImGui.TextDisabled("Attached to"); ImGui.SameLine();
            ImGui.BeginDisabled(parent is null);
            if (UiAuto.SmallButton(name + "##parent") && parent is not null) livePage.SelectRow(parent);
            ImGui.EndDisabled();
        }
        if (answer.Children.Count > 0 && ImGui.TreeNode(
            $"Attached objects ({answer.Children.Count}{(answer.ChildrenTruncated ? "+" : "")})###live-children"))
        {
            foreach (var child in answer.Children)
            {
                var childRow = ObservedChild(child);
                string label = childRow is null ? $"{child.Handle:X8}" : livePage.DisplayName(workspace, childRow);
                ImGui.BeginDisabled(childRow is null);
                if (UiAuto.Selectable(label + "##child-" + child.Handle) && childRow is not null) livePage.SelectRow(childRow);
                ImGui.EndDisabled();
            }
            ImGui.TreePop();
        }
        if (answer.Models.Count > 0 && ImGui.TreeNode($"Drawn models ({answer.Models.Count})###live-drawn"))
        {
            foreach (uint model in answer.Models)
                if (UiAuto.Selectable($"{workspace.DisplayName(model)}##drawn-{model}")) OpenObjectAsset(model);
            ImGui.TreePop();
        }
        else if (answer.Models.Count == 0)
        {
            ImGui.TextDisabled("No drawn models");
            StudioUi.Tip(answer.PoseTime == 0 ? "No animation capture. Turn on Sync > Animation." : "None in the last animation capture.");
        }
        if (answer.Datum.Length > 0 && ImGui.TreeNode($"Raw datum ({answer.Datum.Length} bytes)###live-datum"))
        {
            for (int at = 0; at < answer.Datum.Length; at += 16)
                ImGui.TextUnformatted($"+{at:X3}  {Convert.ToHexString(answer.Datum, at, Math.Min(16, answer.Datum.Length - at))}");
            ImGui.TreePop();
        }
    }

    /** An attachment answer never names a later occupant of the captured child's handle. */
    private LivePlacement? ObservedChild(LiveObjectChild child) =>
        livePage.RowByHandle(child.Handle) is { } row && row.Generation == child.Generation ? row : null;

    private void SelectLiveTreeNode(LiveTreeNode node)
    {
        if (node.Source is not MeshInstance source) return;
        liveInspection.Reveal(node);
        viewport!.Visibility.HiddenLayers.Remove(SceneLayer.Objects);
        viewport.Visibility.HiddenGeometryLayers.Remove(SceneLayer.Objects);
        viewport.SelectionMode = SceneSelectionMode.Object;
        viewport.SetSelection(source); OnViewportSelection(viewport.SelectedObject);
        focusWorldScene = true;
    }

    private bool TrySelectLiveSceneObject(object source, bool focus)
    {
        UpdateLiveInspection();
        if (liveInspection.FindNode(source, viewport!.SelectionMode) is not { } node) return false;
        SelectLiveTreeNode(node);
        if (focus) viewport.FocusSelection();
        return true;
    }
}
