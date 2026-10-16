using System.Buffers.Binary;
using System.Diagnostics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Scene;

/** Checks the object edit workflow: edits reach the overlay, redo restores saved and compiled bytes, and dependent edits are broken by upstream changes. */
internal static class ObjectEditWorkflowChecks
{
    /** Object edit redo, the raw-edit guard and delta conversion, on the world fixture's instance table. */
    internal static void Run(string root)
    {
        uint table = WorldFixture.Base + 20, bounds = WorldFixture.Base + 24;
        string install = Path.Combine(root, "object-edit-install");
        WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.Select(table); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "object-edit-project"), "Object edit workflow"); Wait(workspace);
        var catalog = workspace.Catalog!;
        var project = workspace.Project!;
        byte[] tableOriginal = catalog.ReadOriginal(table), boundsOriginal = catalog.ReadOriginal(bounds);
        int row = checked(0x48 + (int)BinaryPrimitives.ReadInt64LittleEndian(tableOriginal.AsSpan(0x48)) + 16);
        MeshInstance Instance() => new()
        {
            Mesh = new MeshData(), InstanceSourceTag = table, InstanceSourceIndex = 0,
            NativeInstanceData = catalog.Read(table).AsMemory(row, 48)
        };

        // Redo puts back the same object edit and its derived bytes.
        var edit = SceneEdits.CreateTransformEdit(catalog, Instance(), new(9, 8, 7), null, 4, name: "Tree")!;
        workspace.ApplyObjectEdit(edit); Wait(workspace);
        byte[] editedTable = catalog.Read(table), editedBounds = catalog.Read(bounds);
        Require(project.ObjectEdits.Count == 1 && project.Changes.Count == 0 && !editedTable.SequenceEqual(tableOriginal)
            && !editedBounds.SequenceEqual(boundsOriginal), "The object edit did not reach the overlay.");
        workspace.Undo(); Wait(workspace);
        Require(project.ObjectEdits.Count == 0 && catalog.Read(table).SequenceEqual(tableOriginal)
            && catalog.Read(bounds).SequenceEqual(boundsOriginal) && workspace.CanRedo, "Object edit undo failed.");
        workspace.Redo(); Wait(workspace);
        Require(project.ObjectEdits.SingleOrDefault() is { Scale: 4, Name: "Tree" } redone && redone.Key == edit.Key
            && redone.Position!.SequenceEqual(new float[] { 9, 8, 7 }) && project.Changes.Count == 0,
            "Object edit redo did not restore the saved edit.");
        Require(catalog.Read(table).SequenceEqual(editedTable) && catalog.Read(bounds).SequenceEqual(editedBounds)
            && workspace.CanUndo && !workspace.CanRedo, "Object edit redo did not restore the compiled bytes.");

        // A raw edit that breaks the object edit is refused at save; one that keeps the row saves.
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        var refused = Throws<InvalidDataException>(() =>
            workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = editedTable[..0x50] }));
        Require(refused.Message.Contains("breaks the object edit of Tree", StringComparison.Ordinal)
            && File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest) && project.Changes.Count == 0,
            "A raw edit that breaks an object edit was saved.");
        byte[] header = editedTable.ToArray(); header[4] ^= 1;
        workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = header }); Wait(workspace);
        Require(project.Changes.Count == 1 && catalog.Read(table).SequenceEqual(header), "A raw edit beside an object edit was refused.");
        workspace.Undo(); Wait(workspace);
        workspace.RevertObjectEdit(edit.Key); Wait(workspace);
        Require(project.ObjectEdits.Count == 0 && project.Changes.Count == 0, "The fixture project did not return to empty.");

        // Raw deltas written from a compile convert to one object edit with the same bytes.
        var compiled = ObjectEditCompiler.Default.Compile([edit with { Name = null }], catalog.ReadOriginal, catalog).Assets;
        workspace.ApplyAssetEdits(compiled); Wait(workspace);
        Require(project.Changes.Count == 2 && project.ObjectEdits.Count == 0, "The stale raw deltas were not saved.");
        var plan = workspace.ConvertPlacementDeltas(); Wait(workspace);
        Require(plan.Edits.Count == 1 && plan.Converted.Order().SequenceEqual(new[] { table, bounds }) && plan.Kept.Count == 0
            && project.Changes.Count == 0 && project.ObjectEdits.Single() is { Scale: 4 } converted
            && converted.Position!.SequenceEqual(new float[] { 9, 8, 7 }) && converted.Rotation is null,
            "Placement deltas did not convert to one object edit: " + plan.Summary());
        Require(catalog.Read(table).SequenceEqual(compiled[table]) && catalog.Read(bounds).SequenceEqual(compiled[bounds]),
            "Converted object edit compiles to different bytes.");
        workspace.Undo(); Wait(workspace);
        Require(project.Changes.Count == 2 && project.ObjectEdits.Count == 0 && catalog.Read(table).SequenceEqual(compiled[table])
            && catalog.Read(bounds).SequenceEqual(compiled[bounds]), "Conversion undo did not restore the raw deltas.");
        workspace.Redo(); Wait(workspace);
        Require(project.Changes.Count == 0 && project.ObjectEdits.Count == 1 && catalog.Read(bounds).SequenceEqual(compiled[bounds]),
            "Conversion redo did not restore the object edit.");
        workspace.RevertObjectEdit(project.ObjectEdits.Single().Key); Wait(workspace);

        // A byte outside the QTS fields keeps its delta and says why.
        byte[] outside = tableOriginal.ToArray(); outside[row + 40] ^= 1;
        workspace.ApplyAssetEdits(new Dictionary<uint, byte[]> { [table] = outside }); Wait(workspace);
        var kept = workspace.ConvertPlacementDeltas(); Wait(workspace);
        Require(kept.Edits.Count == 0 && kept.Kept.Single().Tag == table
            && kept.Kept[0].Reason.Contains("not a placement field", StringComparison.Ordinal) && project.Changes.Count == 1,
            "A delta outside the placement fields converted: " + kept.Summary());
        Console.WriteLine("editor_object_edits redo=pass breaking-raw=refused beside-raw=saved convert=byte-identical "
            + "convert-undo=pass convert-redo=pass outside-field=kept");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(30))
        { workspace.Poll(); Thread.Sleep(1); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
