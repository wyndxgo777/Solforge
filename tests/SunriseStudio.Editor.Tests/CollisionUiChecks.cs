using SunriseStudio.Editor;
using SunriseStudio.Scene.Collision;

/** A collision selection in an opened compound draws its inspector and parts, frames and clears. */
internal static class CollisionUiChecks
{
    // The Leviathan static compound; opening it shows its collision at once.
    private const uint Compound = 0x80EDC2D0;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string gameRoot, string root)
    {
        Exception? failure = null;
        string result = "";
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: gameRoot, Tag: Compound, Warp: true, Smoke: true, Frames: 100000,
                    SettingsPath: Path.Combine(root, "collision-ui-settings.json")));
                int stage = 0, selected = -1, waited = 0, parts = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                form.FrameRendered += () =>
                {
                    if (stage > 3) return;
                    var view = form.SceneViewport;
                    // The viewport draws, and so loads collision, only on a page that shows it.
                    if (waited++ == 0) form.ShowAssetPage("sunrise.preview");
                    if (view?.Collision is not { IsEmpty: false } collision)
                    {
                        if (clock.Elapsed > TimeSpan.FromMinutes(3))
                        {
                            failure = new TimeoutException($"The compound collision did not load in the editor: scene={view?.Scene?.TagHash:X8} "
                                + $"content={view?.Visibility.ShowCollisionContent} busy={form.Workspace.IsBusy} status=\"{view?.CollisionStatus}\" "
                                + $"document={form.Workspace.Document?.Entry.TagHash:X8} frames={waited}");
                            stage = 4;
                            form.BeginInvoke(form.Close);
                        }
                        return;
                    }
                    if (stage == 0)
                    {
                        selected = collision.Placements.FindIndex(p => p.Instance >= 0 && collision.Pieces[p.Piece].Shape.TriangleCount > 0);
                        view.SelectCollision(selected);
                        stage++;
                    }
                    else if (stage == 1)
                    {
                        Require(form.SelectedCollisionDetails is { } details && ReferenceEquals(details.View, view)
                            && details.Placement == collision.Placements[selected], "The collision selection has no inspector.");
                        view.FocusSelection();
                        Require(view.SelectConnectedCollision(new(.02f, CollisionConnectScope.Shape)), "Select connected did not start.");
                        stage++;
                    }
                    else if (stage == 2)
                    {
                        // The inspector draws the chosen parts' summary on the frames after the search ends.
                        if (view.CollisionConnecting || view.CollisionPartSelection.Count == 0) return;
                        parts = view.CollisionPartSelection.Count;
                        stage++;
                    }
                    else
                    {
                        view.SelectCollision(-1);
                        Require(form.SelectedCollisionDetails is null && view.CollisionPartSelection.Count == 0,
                            "Clearing the collision selection kept its inspector or its parts.");
                        result = $"placement={selected} inspector=drawn frame=pass connected-parts={parts} clear=pass";
                        stage++;
                        form.BeginInvoke(form.Close);
                    }
                };
                Application.Run(form);
                if (form.Failure is { } error) throw new Exception(error);
            }
            catch (Exception error) { failure ??= error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(5))) throw new TimeoutException("Collision UI check timed out.");
        if (failure is not null) throw failure;
        Require(result.Length > 0, "The collision UI check did not finish.");
        Console.WriteLine("editor_collision_ui " + result);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
