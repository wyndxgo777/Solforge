using SunriseStudio.Authoring;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: compatible skin template discovery and import. */
public sealed partial class StudioWorkspace
{
    /** Template discovery reads installed bytes without publishing a project edit. */
    public void ReadCompatibleSkinTemplates(uint entityTag, Action<IReadOnlyList<CompatibleSkinTemplate>> publish)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; int revision = ProjectRevision;
        BeginWork("Reading skin templates", () =>
        {
            var templates = CompatibleSkinnedMesh.Templates(catalog, entityTag, OperationToken);
            if (templates.Count == 0)
                throw new NotSupportedException("This entity has no supported matrix skinning template.");
            return () =>
            {
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog) || ProjectRevision != revision) return;
                publish(templates); Status = "Choose a skin template";
            };
        });
    }

    /** The review holds prepared bytes; Apply rechecks the source snapshot before changing the project. */
    public void PrepareCompatibleSkin(string path, CompatibleSkinReplacementOptions options,
        Action<PreparedCompatibleSkin> publish)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; int revision = ProjectRevision;
        long overlay = catalog.OverlayRevision;
        BeginWork("Checking skinned model", () =>
        {
            var prepared = CompatibleSkinnedMesh.PrepareGltf(catalog, path, options, OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            return () =>
            {
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.OverlayRevision != overlay) return;
                publish(prepared); Status = "Skin replacement ready";
            };
        });
    }

    /** A new entity shares the checked rig while all nine new entries remain original-relative deltas. */
    public void PrepareCompatibleSkinEntity(string path, CompatibleSkinReplacementOptions options,
        Action<PreparedCompatibleEntity> publish)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!; int revision = ProjectRevision;
        long overlay = catalog.OverlayRevision;
        BeginWork("Checking entity asset", () =>
        {
            var prepared = CompatibleSkinnedMesh.PrepareEntityGltf(project, catalog, path, options, OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            return () =>
            {
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.OverlayRevision != overlay) return;
                publish(prepared); Status = "Entity asset ready";
            };
        });
    }
}
