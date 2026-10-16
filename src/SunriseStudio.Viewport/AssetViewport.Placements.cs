using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Placement half of AssetViewport: follows objects an edit moved in place. */
public sealed partial class AssetViewport
{
    /**
     * Follows objects a placement edit moved in place. Uploads, selection and camera stay.
     * Call after NativeScenePlan.UpdatePlacements so the renderer copies the new constants.
     */
    public void UpdatePlacements(IReadOnlyCollection<MeshInstance> instances, IReadOnlyCollection<SceneMarker> markers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Scene is null || instances.Count + markers.Count == 0) return;
        foreach (var instance in instances)
            if (selection.IndexOf(instance) >= 0) selection.RefreshBounds(instance);
        foreach (var marker in markers)
            if (selection.IndexOf(marker) >= 0) selection.RefreshBounds(marker);
        // Backdrop layers and anchors depend on part bounds.
        Backdrop.SetScene(Scene, selection);
        gridDirty = true;
        renderIndexDirty = true;
        effectScene = null;
        HoveredObject = null;
        nativeScene?.RefreshPlacements(instances);
    }

    /** A moved light or squad point replaces its proxy; a selected proxy stays selected. */
    public void ReplacePointProxy(ScenePointProxy old, ScenePointProxy replacement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!selection.ReplaceProxy(old, replacement)) return;
        gridDirty = true;
        renderIndexDirty = true;
        int index = selectionSources.FindIndex(item => ReferenceEquals(item, old));
        if (index < 0) return;
        var items = selectionSources.ToArray();
        items[index] = replacement;
        ApplySelection(items, true);
    }
}
