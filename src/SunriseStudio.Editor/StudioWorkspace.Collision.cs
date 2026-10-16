using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Interchange;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: collision export, import and part edits for the open scene. */
public sealed partial class StudioWorkspace
{
    /** Loads the scene's collision from the current project and writes it as separate geometry. */
    public void ExportCollision(string path, SceneDocument scene)
    {
        RequireIdle();
        var catalog = Catalog ?? throw new InvalidOperationException("Open your Sunrise installation first.");
        BeginWork("Exporting collision", () =>
        {
            var collision = new CollisionSceneLoader(catalog).Load(scene, OperationToken);
            CollisionInterchange.Write(path, collision, catalog, scene.TagHash);
            return () =>
            {
                Status = "Collision exported";
                Notice = new("Collision exported", false, Path.GetDirectoryName(path),
                    $"{collision.Placements.Count:N0} shapes, {collision.Pieces.Count:N0} distinct, "
                    + $"{collision.PlacedTriangles:N0} triangles");
            };
        }, blocking: false);
    }

    /** Reads an edited collision export and saves its changes as one undo step. */
    public void ImportCollision(string path, SceneDocument scene)
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        var package = CollisionImport.ShapePackage(project, catalog);
        BeginWork("Importing collision", () =>
        {
            var input = CollisionInterchange.Read(path);
            var baseline = new CollisionSceneLoader(catalog).Load(scene, OperationToken);
            var plan = CollisionImport.Plan(catalog, baseline, input, ReadCollisionRaw, package,
                project.Changes.Select(change => change.TagHash).Concat(nativeAdditions.Keys).ToArray());
            return () => ApplyCollisionPlan(plan);
        });
    }

    /**
     * Moves chosen parts of one or more shapes by a world transform, as one undo step.
     * @param collision The loaded collision the parts were chosen in; it gives the world frames.
     */
    public void EditCollisionParts(CollisionScene collision, IReadOnlyCollection<CollisionPartRef> parts, Matrix4x4 transform)
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        var input = CollisionPartEdit.Input(catalog, collision, parts, transform);
        string label = input.Shapes.Count == 1 ? $"Collision parts {input.Shapes[0].Tag:X8}" : $"Collision parts {input.Shapes.Count} shapes";
        var package = CollisionImport.ShapePackage(project, catalog);
        BeginWork("Editing collision parts", () =>
        {
            var plan = CollisionImport.Plan(catalog, collision, input, ReadCollisionRaw, package,
                project.Changes.Select(change => change.TagHash).Concat(nativeAdditions.Keys).ToArray());
            return () => ApplyCollisionPlan(plan, label);
        });
    }

    /**
     * Applies a group's object edits and moves its collision parts by the same world transform, as one undo step.
     * @param collision The loaded collision the parts resolve in; ignored when parts is empty.
     */
    public void TransformGroup(IReadOnlyList<ObjectEdit> edits, CollisionScene? collision,
        IReadOnlyCollection<CollisionPartRef> parts, Matrix4x4 transform, string label)
    {
        if (parts.Count == 0 || collision is null) { ApplyObjectEdits(edits, label); return; }
        RequireEditable();
        foreach (var edit in edits) edit.Validate();
        var project = Project!;
        var catalog = Catalog!;
        var input = CollisionPartEdit.Input(catalog, collision, parts, transform);
        var package = CollisionImport.ShapePackage(project, catalog);
        BeginWork("Transforming group", () =>
        {
            var plan = CollisionImport.Plan(catalog, collision, input, ReadCollisionRaw, package,
                project.Changes.Select(change => change.TagHash).Concat(nativeAdditions.Keys).ToArray());
            IReadOnlyList<ObjectEdit> all = [.. plan.ObjectEdits, .. edits];
            if (all.Select(edit => edit.Key).Distinct(StringComparer.Ordinal).Count() != all.Count)
                throw new InvalidDataException("One change edits an object twice.");
            return () => ApplyCollisionPlan(plan with { ObjectEdits = all }, label);
        });
    }

    /** Raw project bytes of an installed tag or of a shape an earlier import added. */
    private byte[] ReadCollisionRaw(uint tag) => nativeAdditions.TryGetValue(tag, out var added)
        ? Project!.ReadAddedAsset(added.Id, Catalog) : Project!.ReadAsset(tag, Catalog!);

    /**
     * Instance moves merge into their object edits; shape bytes replace the raw payloads; new shapes
     * become added assets. All go in one manifest write, and one undo step removes the new shapes.
     */
    public void ApplyCollisionPlan(CollisionImportPlan plan, string label = "Imported collision")
    {
        RequireEditable();
        var project = Project!;
        var catalog = Catalog!;
        foreach (var (tag, hash) in plan.SourceFingerprints)
            if (BinaryDelta.Hash(catalog.Read(tag)) != hash)
                throw new InvalidOperationException($"Collision source {tag:X8} changed after import preparation.");
        if (plan.ObjectEdits.Count == 0 && plan.Assets.Count == 0 && plan.AddedShapes.Count == 0)
        {
            Status = "No changes";
            Notice = new("No collision changes", false, null, plan.Notes.Count > 0 ? string.Join("\n", plan.Notes) : null);
            return;
        }
        var compiler = project.ObjectEditCompiler ?? ObjectEditCompiler.Default;
        byte[] ReadStaged(uint tag) => plan.Assets.TryGetValue(tag, out var bytes) ? bytes : ReadCollisionRaw(tag);
        var previousBytes = plan.Assets.Keys.ToDictionary(tag => tag, ReadRawBytes);
        var previousObjects = plan.ObjectEdits.ToDictionary(edit => edit.Key, edit => project.FindObjectEdit(edit.Key), StringComparer.Ordinal);
        var states = new Dictionary<string, ObjectEdit?>(StringComparer.Ordinal);
        foreach (var edit in plan.ObjectEdits)
            states[edit.Key] = compiler.Normalize(ObjectEdit.Merge(previousObjects[edit.Key], edit), ReadStaged, catalog);
        IReadOnlyList<AddedProjectAsset> created = [];
        MutateProject(() => created = project.RestoreTransaction(SplitAssetEdits(plan.Assets), new Dictionary<string, AddedAssetSnapshot?>(),
            catalog, states.Count > 0 ? states : null, plan.Additions), plan.AddedShapes.Count > 0 ? null : plan.Assets.Keys);
        FinishObjectStates(previousBytes, previousObjects, label,
            created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null, StringComparer.Ordinal));
        var overridden = compiledObjects.Edits.Count(item => states.ContainsKey(item.Edit.Key) && item.Tags.Count == 0);
        var notes = plan.Notes.ToList();
        if (overridden > 0) notes.Add($"{overridden} collision instance edits lose to static instance edits of the same object.");
        Notice = notes.Count > 0
            ? new WorkNotice(notes.Count == 1 ? "1 collision note" : $"{notes.Count} collision notes", false, null, string.Join("\n", notes))
            : null;
    }
}
