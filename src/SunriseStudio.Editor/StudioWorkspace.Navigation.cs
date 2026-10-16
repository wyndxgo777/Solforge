using SunriseStudio.Formats;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** What the editor showed of a visited place: its page tab and its selected object, both editor ids. */
public sealed record StudioView(string? Page, string? Selection);

/** StudioWorkspace part: bounded back/forward history of opened places. */
public sealed partial class StudioWorkspace
{
    /** One place in the history: the open world, the open asset and the editor's view of it. */
    private sealed record Visit(uint? Map, uint? Mission, uint Tag, string? AddedId, AssetOpenContext? Context, StudioView? View = null);
    // Older places are dropped once the history holds this many.
    private const int MaximumVisits = 200;
    private readonly List<Visit> visits = [];
    private int visitIndex = -1;
    private int? navigationTarget;
    private StudioView? pendingView;

    public bool CanNavigateBack => !IsBusy && navigationTarget is null && FindVisit(-1) >= 0;
    public bool CanNavigateForward => !IsBusy && navigationTarget is null && FindVisit(1) >= 0;

    /** Goes back one visited place. */
    public void NavigateBack() => Navigate(-1);
    /** Goes forward one visited place. */
    public void NavigateForward() => Navigate(1);
    /** Opens a related asset, keeping its open context. */
    public void SelectRelated(RelatedAsset asset) => Select(asset.TagHash, context: asset.Context);

    /** The view of the place navigation arrived at; the editor applies it once, then records views again. */
    public StudioView? TakePendingView()
    {
        var view = pendingView;
        pendingView = null;
        return view;
    }

    /**
     * Records what the editor shows of the current place. The first view fills the place in; a later
     * change is a new place, so Back returns to the previous page or selection.
     */
    public void NoteView(StudioView view)
    {
        if (IsBusy || navigationTarget is not null || pendingView is not null || visitIndex < 0) return;
        // A frame that drew no page is not a view; a new document draws none on its first frame.
        if (view.Page is null) return;
        var current = visits[visitIndex];
        if (current.View == view) return;
        if (current.View is null) { visits[visitIndex] = current with { View = view }; return; }
        Push(current with { View = view });
    }

    private int FindVisit(int direction)
    {
        for (int index = visitIndex + direction; index >= 0 && index < visits.Count; index += direction)
            if (Exists(visits[index])) return index;
        return -1;
    }

    private bool Exists(Visit visit) => (visit.Map is not { } map || map == World?.Map.Tag || Catalog?.ContainsTag(map) == true)
        && (visit.AddedId is { } added ? Project?.AddedAssets.Any(asset => asset.Id == added) == true
            : Catalog?.ContainsTag(visit.Tag) == true);

    private void Navigate(int direction)
    {
        if (IsBusy || navigationTarget is not null) return;
        int index = FindVisit(direction);
        if (index < 0) return;
        navigationTarget = index;
        ContinueNavigation();
    }

    /** Opens the target's world, then its asset, then hands its view to the editor. Each load ends in Poll. */
    private void ContinueNavigation()
    {
        if (navigationTarget is not { } index || IsBusy) return;
        var visit = visits[index];
        if (!Exists(visit)) { navigationTarget = null; return; }
        if (visit.Map != World?.Map.Tag || visit.Mission != World?.MissionTag)
        {
            if (visit.Map is { } map)
            {
                LoadWorld(map, visit.Mission, new(), false);
                if (!IsBusy) navigationTarget = null;
                return;
            }
            CloseWorld();
        }
        if (Document is not { } document || document.Entry.TagHash != visit.Tag || document.AddedAssetId != visit.AddedId
            || document.OpenContext != visit.Context)
        {
            if (visit.AddedId is { } added) SelectAddedAsset(added, context: visit.Context);
            else SelectAsset(visit.Tag, visit.Context);
            // A refused open leaves nothing running; the history then stays where it was.
            if (!IsBusy) navigationTarget = null;
            return;
        }
        visitIndex = index;
        navigationTarget = null;
        pendingView = visit.View;
    }

    /** A new place is recorded after its document opens. Navigation records nothing; it moves the index. */
    private void RememberAsset(StudioDocument document)
    {
        if (navigationTarget is not null) return;
        var visit = new Visit(World?.Map.Tag, World?.MissionTag, document.Entry.TagHash, document.AddedAssetId, document.OpenContext);
        if (visitIndex >= 0 && visits[visitIndex] with { View = null } == visit) return;
        Push(visit);
    }

    private void Push(Visit visit)
    {
        if (visitIndex + 1 < visits.Count) visits.RemoveRange(visitIndex + 1, visits.Count - visitIndex - 1);
        visits.Add(visit);
        if (visits.Count > MaximumVisits) visits.RemoveAt(0);
        visitIndex = visits.Count - 1;
    }

    /** A new installation starts an empty history; the document, when given, becomes its first place. */
    private void ResetAssetVisits(StudioDocument? document = null)
    {
        visits.Clear(); visitIndex = -1; navigationTarget = null; pendingView = null;
        if (document is not null) RememberAsset(document);
    }
}
