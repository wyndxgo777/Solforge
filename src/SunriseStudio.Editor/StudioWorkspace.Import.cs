using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Assets an import prepared, and which of them to open afterwards. */
public sealed record PreparedAssetImport(IReadOnlyList<ProjectAssetAddition> Assets, int SelectedIndex = 0);

/** StudioWorkspace part: adds imported assets to the project on the worker. */
public sealed partial class StudioWorkspace
{
    /** Prepares the import on the worker with the project and catalog, then adds it as one edit. */
    public void ImportAsset(Func<StudioProject, PackageCatalog, PreparedAssetImport> prepare) =>
        ImportAsset((project, catalog, _) => prepare(project, catalog));

    /** Workers prepare bytes only; cancellation and source checks precede one atomic publication. */
    public void ImportAsset(Func<StudioProject, PackageCatalog, CancellationToken, PreparedAssetImport> prepare)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        var selected = Document;
        var manifest = File.ReadAllBytes(project.ManifestPath);
        int revision = ProjectRevision;
        catalog.InvalidateReadCache();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity();
        BeforeAssetMutation?.Invoke();
        BeginWork("Importing asset", () =>
        {
            var token = OperationToken;
            token.ThrowIfCancellationRequested();
            var prepared = prepare(project, catalog, token);
            if ((uint)prepared.SelectedIndex >= prepared.Assets.Count)
                throw new InvalidDataException("The import did not select an asset from its prepared group.");
            var additions = prepared.Assets.Select(asset => asset with { Data = asset.Data.ToArray() }).ToArray();
            token.ThrowIfCancellationRequested();
            return () =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || !File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
                    || catalog.GetPackageFileIdentity() != source)
                    throw new InvalidDataException("The project or import source changed. Import it again.");
                IReadOnlyList<AddedProjectAsset> created = [];
                MutateProject(() =>
                {
                    token.ThrowIfCancellationRequested();
                    created = project.ApplyTransaction(additions, catalog: catalog);
                });
                string id = created[prepared.SelectedIndex].Id;
                PushUndo(new(selected?.Entry.TagHash ?? 0, new Dictionary<uint, byte[]>(), selected?.AddedAssetId,
                    selected?.OpenContext, created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null),
                    ReverseSelection: new(0, id, null)));
                ProjectRevision++;
                SelectAdded(id);
            };
        });
    }

    /** The next free entry index in a package, counting the project's added assets. */
    public int NextImportEntry(ushort packageId) => Project!.AddedAssets
        .Where(asset => asset.Native?.PackageId == packageId)
        .Select(asset => asset.Native!.EntryIndex ?? -1).DefaultIfEmpty(-1).Max() + 1;
}
