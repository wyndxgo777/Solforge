/**
 * File: StudioWorkspace partial: builds custom perks from weapon graphs into the project (uses
 * Creation/CustomPerkAuthoring.cs and Solforge.Graphs.PerkLowering).
 */
using Solforge.Graphs;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Workspace operations for custom perk authoring (part of StudioWorkspace). */
public sealed partial class StudioWorkspace
{
    /** The last node graph custom perks installed, for the apply_graph reply and get_editor_state. */
    public PreparedCustomPerks? LastCustomPerks { get; private set; }

    /** The project's newest package, else 0AA0, as item creation chooses it; null when that id is installed. */
    public ushort? CustomPerkPackage()
    {
        if (Project is not { } project || Catalog is not { } catalog) return null;
        ushort id = project.AddedAssets.LastOrDefault(asset => asset.Native is not null)?.Native!.PackageId ?? 0x0AA0;
        return PackageWriter.IsNewPackageId(id) && !catalog.ContainsInstalledPackage(id) ? id : null;
    }

    /** Packs node graph Custom Perks into the weapon off-thread, then commits one undoable transaction. */
    public void CreateCustomPerks(uint weaponTag, IReadOnlyList<CustomPerkDefinition> perks, ushort packageId)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; var selected = Document;
        var manifest = File.ReadAllBytes(project.ManifestPath);
        string? root = Path.GetDirectoryName(project.ManifestPath);
        int revision = ProjectRevision;
        catalog.InvalidateReadCache();
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        string source = catalog.GetPackageFileIdentity();
        LastCustomPerks = null;
        BeforeAssetMutation?.Invoke();
        BeginWork("Packing custom perks", () =>
        {
            var token = OperationToken;
            token.ThrowIfCancellationRequested();
            var plan = CustomPerkAuthoring.Prepare(project, catalog, weaponTag, perks, packageId, root);
            token.ThrowIfCancellationRequested();
            return () =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || !File.ReadAllBytes(project.ManifestPath).AsSpan().SequenceEqual(manifest)
                    || catalog.GetPackageFileIdentity() != source)
                    throw new InvalidDataException("The project or installation changed. Apply the graph again.");
                var previous = plan.Changes.Keys.ToDictionary(tag => tag, ReadRawBytes);
                var edits = plan.Changes.ToDictionary(pair => pair.Key, pair => RebaseOnRaw(pair.Key, pair.Value, previous[pair.Key]));
                var split = SplitAssetEdits(edits);
                IReadOnlyList<AddedProjectAsset> created = [];
                MutateProject(() => created = project.ApplyTransaction(plan.Assets, _ => split, catalog));
                PushUndo(new(selected?.Entry.TagHash ?? 0, previous, selected?.AddedAssetId, selected?.OpenContext,
                    created.ToDictionary(asset => asset.Id, _ => (AddedAssetSnapshot?)null)));
                ProjectRevision++;
                LastCustomPerks = plan;
                Status = $"Custom perks installed: {string.Join(", ", plan.Perks.Select(perk => perk.Name))}";
            };
        });
    }
}
