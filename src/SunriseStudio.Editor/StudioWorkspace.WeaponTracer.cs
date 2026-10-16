using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Refusal is null only when the weapon's base projectile is Tommy's layout; Overlay is the revision it was read at. */
public sealed record WeaponTracerState(uint Weapon, long Overlay, string? Refusal, Vector4 Start, Vector4 End, bool Custom);

/** StudioWorkspace part: reads and edits a weapon's tracer colour. */
public sealed partial class StudioWorkspace
{
    /** The WIP tracer colour last read; reload when its weapon or overlay revision is stale. */
    public WeaponTracerState? LoadedTracer { get; private set; }

    /** Reads the weapon's WIP tracer colour on a worker; a result for an older overlay is dropped. */
    public void LoadTracer(uint weaponTag)
    {
        RequireIdle();
        if (Project is not { } project || Catalog is not { } catalog)
        {
            LoadedTracer = new(weaponTag, Catalog?.OverlayRevision ?? 0, "Open a project first.",
                WeaponTracerAuthoring.StockStart, WeaponTracerAuthoring.StockEnd, false);
            return;
        }
        long overlay = catalog.OverlayRevision;
        string status = Status;
        BeginWork("Reading tracer", () =>
        {
            WeaponTracerState state;
            // A failed read still publishes, so the page does not retry every frame.
            try
            {
                var row = WeaponFiringRow.Find(project, catalog, catalog.Read, weaponTag);
                var (start, end, custom) = WeaponTracerAuthoring.Read(project, catalog, row);
                state = new(weaponTag, overlay, WeaponTracerAuthoring.Refusal(catalog, row), start, end, custom);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                state = new(weaponTag, overlay, error.Message, WeaponTracerAuthoring.StockStart, WeaponTracerAuthoring.StockEnd, false);
            }
            return () =>
            {
                Status = status;
                if (ReferenceEquals(Catalog, catalog) && catalog.OverlayRevision == overlay) LoadedTracer = state;
            };
        }, blocking: false, keepNotice: true);
    }

    /** WIP: gives the weapon a recoloured tracer trail. One undo step. */
    public void ApplyTracerColour(uint weaponTag, Vector4 start, Vector4 end) =>
        RunTracerStep(weaponTag, "Recolouring tracer (WIP)", "Tracer colour applied (WIP)",
            (project, catalog, package, next) => WeaponTracerAuthoring.Prepare(project, catalog, weaponTag, start, end, package, next));

    /** WIP: puts the base weapon's tracer back. One undo step. */
    public void ResetTracerColour(uint weaponTag) =>
        RunTracerStep(weaponTag, "Resetting tracer (WIP)", "Tracer colour reset (WIP)",
            (project, catalog, _, _) => WeaponTracerAuthoring.PrepareReset(project, catalog, weaponTag));

    /** Prepares off the UI thread, then applies only if the project did not change meanwhile. */
    private void RunTracerStep(uint weaponTag, string status, string done,
        Func<StudioProject, PackageCatalog, ushort, int, PreparedWeaponTracer> prepare)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var (package, next) = WeaponEntryCursor(project, weaponTag);
        int revision = ProjectRevision; long overlay = catalog.OverlayRevision;
        BeginWork(status, () =>
        {
            var plan = prepare(project, catalog, package, next);
            OperationToken.ThrowIfCancellationRequested();
            return () =>
            {
                RequireSameProjectState(project, catalog, revision, overlay);
                if (plan.Assets.Count == 0 && plan.Changes.Count == 0 && plan.Removals.Count == 0) { Status = "No changes"; return; }
                ApplyAssetReplacement(plan.Assets, new ProjectAssetEdits(plan.Changes, null), plan.Removals, done);
            };
        }, blocking: false);
    }
}
