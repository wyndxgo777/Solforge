using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;

/** Checks the bundle export and import workflow in the editor: results are published, and an unchanged import changes nothing while a delta is one undoable edit. */
internal static class BundleWorkflowChecks
{
    /** Bundle imports preserve undo for originals and publish derived roots through the overlay. */
    internal static void Run(string root)
    {
        const uint tag = 0x80A00000;
        string install = Path.Combine(root, "bundle-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] original = [11, 22, 33, 44, 55, 66, 77, 88];
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_bundle_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, original, "bundle_workflow")]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.Select(tag); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "bundle-project"), "Bundle workflow"); Wait(workspace);
        string exported = Path.Combine(root, "bundle-export");
        workspace.ExportAssetBundle(exported); Wait(workspace);
        var bundle = workspace.LastAssetExport ?? throw new Exception("Bundle export was not published.");
        Require(workspace.TransferRevision == 1 && workspace.LastTransferWasExport, "Export did not publish its result.");
        int projectRevision = workspace.ProjectRevision, mutations = 0;
        workspace.BeforeAssetMutation += () => mutations++;
        workspace.ImportAssetBundle(bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)); Wait(workspace);
        Require(workspace.ProjectRevision == projectRevision && mutations == 0
            && workspace.LastAssetImport?.ChangedAssets == 0, "Unchanged bundle import rebuilt the view or changed the project.");
        var native = bundle.Manifest.NativeAssets.Single(asset => asset.TagHash == tag);
        byte[] changed = (byte[])original.Clone(); changed[3] ^= 7;
        File.WriteAllBytes(Path.Combine(exported, native.Path), changed);
        workspace.ImportAssetBundle(bundle.ManifestPath, new(AssetBundleImportMode.ApplyDeltas)); Wait(workspace);
        Require(workspace.Project!.Changes.Count == 1 && workspace.Document!.Bytes.SequenceEqual(changed)
            && workspace.CanUndo, "Bundle delta import did not publish one undoable edit.");
        workspace.Undo(); Wait(workspace);
        Require(workspace.Document!.Bytes.SequenceEqual(original) && workspace.Project.Changes.Count == 0,
            "Bundle import undo did not restore the original asset.");
        workspace.Redo(); Wait(workspace);
        Require(workspace.Document!.Bytes.SequenceEqual(changed), "Bundle import redo lost its edit.");
        workspace.Undo(); Wait(workspace);
        workspace.ImportAssetBundle(bundle.ManifestPath, new(AssetBundleImportMode.NewAsset, 0x0C00, "Bundle copy")); Wait(workspace);
        Require(workspace.LastAssetImport is { RootAddedAssetId: not null, AddedAssets.Count: > 0 }
            && workspace.Document!.AddedAssetId == workspace.LastAssetImport.RootAddedAssetId,
            "New bundle import did not select its added root.");
        Require(workspace.Document!.Bytes.SequenceEqual(changed)
            && workspace.Catalog!.ReadOriginal(tag).SequenceEqual(original),
            "Bundle import changed installed data or lost the working bytes.");
        Require(workspace.LastAssetImport!.AddedAssets.All(asset => asset.BaseTagHash == tag
            && asset.Path.StartsWith("deltas/", StringComparison.Ordinal)),
            "A derived bundle copied native originals into the project.");
        Console.WriteLine("editor_bundle export=pass delta=pass undo=pass redo=pass new-root=pass originals=unchanged");
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
