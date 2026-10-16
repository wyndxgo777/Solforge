using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Scene;

/** Checks undo/redo around imports (texture creation adopting its coupled group, selection, allocated identities). */
internal static class ImportHistoryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Imports publish only at adoption, with one reversible group and its selected identity. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "import-history-install"), packages = Path.Combine(install, "packages");
        Directory.CreateDirectory(packages);
        byte[] header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 0xCAFE);
        foreach (int offset in new[] { 14, 16, 18, 20 }) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(offset), 1);
        header[23] = 1; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), uint.MaxValue);
        PackageWriter.WriteNew(Path.Combine(packages, "w64_import_0100_0.pkg"), 0x100,
            [new(0x80A00001, (32 << 9) | (1 << 6), header), new(uint.MaxValue, (40 << 9) | (1 << 6), [1, 2, 3, 255]),
             new(0x80800001, 8 << 9, [10, 20])]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "import-history-project"), "Import history");
        string png = Path.Combine(root, "import-pixels.png");
        File.WriteAllBytes(png, PngInterchange.Write(new RgbaImage(1, 1, [64, 128, 192, 255])));
        Texture(workspace, png); Wait(workspace);
        Check(workspace.Project!.AddedAssets.Count == 2 && workspace.Document?.AddedAssetId is not null,
            "Texture creation did not adopt its coupled group.");
        string selected = workspace.Document!.AddedAssetId!;
        var assets = workspace.Project.AddedAssets.ToArray();
        workspace.Undo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.Count == 0 && workspace.Document is null && !workspace.CanUndo && workspace.CanRedo,
            "Undo did not restore the empty project and selection.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Project.AddedAssets.SequenceEqual(assets) && workspace.Document?.AddedAssetId == selected,
            "Redo changed the allocated texture identities or selected asset.");
        workspace.Undo(); Wait(workspace);
        byte[] baseline = File.ReadAllBytes(workspace.Project.ManifestPath);
        int revision = workspace.ProjectRevision;
        void Unchanged()
        {
            Check(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(baseline)
                && workspace.Project.AddedAssets.Count == 0 && workspace.Document is null
                && workspace.CanRedo && !workspace.CanUndo && workspace.ProjectRevision == revision,
                "Failed or canceled preparation changed project, selection or history.");
        }
        using (var began = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            workspace.ImportAsset((_, _) => { began.Set(); release.Wait(); return new([new("cancelled.txt", [1])]); });
            Check(began.Wait(TimeSpan.FromSeconds(5)), "Preparation did not start.");
            Cancel(workspace); release.Set(); Wait(workspace); Unchanged();
        }
        workspace.ImportAsset((_, _) => new([new("not-published.txt", [2])]));
        Ready(workspace); Cancel(workspace); Wait(workspace); Unchanged();
        workspace.ImportAsset((_, _) => new([new("stale.txt", [3])]));
        Ready(workspace); workspace.Catalog!.InvalidateReadCache(); Wait(workspace, failed: true); Unchanged();
        string invalid = Path.Combine(root, "invalid-import.png"); File.WriteAllText(invalid, "not a PNG");
        Texture(workspace, invalid); Wait(workspace, failed: true); Unchanged();
        using (var locked = new FileStream(workspace.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Texture(workspace, png); Wait(workspace, failed: true); Unchanged(); }
        Check(!Directory.EnumerateFiles(Path.Combine(workspace.Project.DirectoryPath, "assets")).Any()
            && !Directory.EnumerateFiles(Path.Combine(workspace.Project.DirectoryPath, "deltas")).Any(),
            "Failed save left prepared payload files in the project.");
        Texture(workspace, png); Wait(workspace);
        Check(!workspace.CanRedo && workspace.CanUndo, "Successful import did not replace the redo branch.");
        string imported = workspace.Document!.AddedAssetId!;
        var context = new AssetOpenContext(PlacementTag: 0x80A00002, PlacementOffset: 8);
        workspace.Select(0x80A00002, context: context); Wait(workspace);
        var prior = workspace.Document;
        workspace.ImportAsset((_, _) => new([new("source.txt", [4, 5])])); Wait(workspace);
        string source = workspace.Document!.AddedAssetId!;
        workspace.Undo(); Wait(workspace);
        Check(workspace.Document?.Entry.TagHash == prior!.Entry.TagHash && workspace.Document.AddedAssetId is null
            && workspace.Document.OpenContext == context,
            "Undo did not restore the prior installed selection.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Document?.AddedAssetId == source && workspace.Project.AddedAssets.Any(asset => asset.Id == imported),
            "Redo lost source identity or an earlier import.");
        workspace.Undo(); Wait(workspace);
        workspace.ImportAsset((_, _) => new([new("project-change.txt", [6])])); Ready(workspace);
        workspace.Project.SetMod(new("test.changed", "1.0.0", "Changed during preparation"));
        byte[] external = File.ReadAllBytes(workspace.Project.ManifestPath);
        Wait(workspace, failed: true);
        Check(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(external) && workspace.CanRedo
            && workspace.Project.AddedAssets.Count == 2, "Stale adoption overwrote a changed project.");
        World(root);
        Console.WriteLine("import_history texture=pass empty_selection=pass identity=pass cancellation=pass stale=pass save_failure=pass redo=pass");
    }

    private static void World(string root)
    {
        string install = Path.Combine(root, "import-history-world"); WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "import-history-world-project"), "World import"); Wait(workspace);
        var world = workspace.World!; var document = workspace.Document!;
        workspace.ImportAsset((_, _) => new([new("world-source.txt", [7, 8])])); Wait(workspace);
        string id = workspace.Document!.AddedAssetId!;
        workspace.Undo(); Wait(workspace);
        Check(ReferenceEquals(workspace.World, world) && workspace.Document?.Entry.TagHash == document.Entry.TagHash
            && workspace.Document.AddedAssetId == document.AddedAssetId && workspace.Document.OpenContext == document.OpenContext,
            "Import undo replaced the world or lost its prior inspector context.");
        workspace.Redo(); Wait(workspace);
        Check(ReferenceEquals(workspace.World, world) && workspace.Document?.AddedAssetId == id,
            "Import redo replaced the world or lost the source identity.");
    }

    /** Both static creation routes exercise their real preparation and group publication. */
    internal static void Owned(string root, string game)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
        typeof(StudioWorkspace).GetProperty("Catalog")!.SetValue(workspace, catalog);
        typeof(StudioWorkspace).GetProperty("Names")!.SetValue(workspace, StudioNameIndex.Create(catalog));
        workspace.CreateProject(Path.Combine(root, "static-history-project"), "Static history");
        ushort package = Enumerable.Range(0x100, 0xC00).Select(value => (ushort)value)
            .First(value => !catalog.ContainsInstalledPackage(value));
        string gltf = Path.Combine(root, "import-triangle.gltf");
        GltfInterchange.Write(gltf, [new InterchangeMesh("Triangle", [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2])]);
        foreach (bool original in new[] { false, true })
        {
            var workflow = new StaticPropWorkflow(workspace);
            Field(workflow, "Source").Text = original ? "1" : "0";
            Field(workflow, "Mesh file").Text = gltf; Field(workflow, "Name").Text = "History triangle";
            Field(workflow, "Package ID").Text = package.ToString("X4");
            // An original asset is read on the worker.
            workflow.Update(workspace); Wait(workspace);
            Check(workflow.Problems(workspace).Count == 0, "Static workflow failed preparation checks.");
            workflow.Create(workspace); Wait(workspace);
            var assets = workspace.Project!.AddedAssets.ToArray();
            Check(assets.Length >= 8 && workspace.Document?.Entry.Reference == 0x808071A7,
                "Static creation selected a dependency instead of its model.");
            string id = workspace.Document!.AddedAssetId!;
            var bytes = assets.ToDictionary(asset => asset.Id, asset => workspace.Project.ReadAddedAsset(asset.Id, catalog));
            workspace.Undo(); Wait(workspace);
            Check(workspace.Project.AddedAssets.Count == 0 && workspace.Document is null, "Static undo left a partial group.");
            workspace.Redo(); Wait(workspace);
            Check(workspace.Project.AddedAssets.SequenceEqual(assets) && workspace.Document?.AddedAssetId == id
                && assets.All(asset => workspace.Project.ReadAddedAsset(asset.Id, catalog).SequenceEqual(bytes[asset.Id])),
                "Static redo changed group identities or prepared bytes.");
            workspace.Undo(); Wait(workspace);
        }
        Console.WriteLine("import_history static_mesh=pass original_materials=pass undo=one_group redo=exact selection=model");
    }

    private static CreationField Field(CreationWorkflow workflow, string name) => workflow.Fields.Single(field => field.Label == name);
    private static void Texture(StudioWorkspace workspace, string path)
    {
        var workflow = new TextureWorkflow(workspace);
        Field(workflow, "Image").Text = path; Field(workflow, "Name").Text = "History texture";
        Field(workflow, "Package ID").Text = "0101";
        workflow.Create(workspace);
    }

    private static void Cancel(StudioWorkspace workspace) =>
        ((CancellationTokenSource)typeof(StudioWorkspace).GetField("operationCancellation", Private)!.GetValue(workspace)!).Cancel();

    private static void Ready(StudioWorkspace workspace)
    {
        var work = (Task)typeof(StudioWorkspace).GetField("work", Private)!.GetValue(workspace)!;
        var watch = Stopwatch.StartNew();
        while (!work.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(1);
        Check(work.IsCompleted, "Preparation timed out before adoption.");
    }

    private static void Wait(StudioWorkspace workspace, bool failed = false)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(90)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && (failed ? workspace.Error.Length != 0 : workspace.Error.Length == 0),
            workspace.Error.Length == 0 ? "Unexpected import completion state." : workspace.Error);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
