using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: wide hash aliases of added assets. */
public sealed partial class StudioWorkspace
{
    /** Alias edits share ordinary added-asset undo without replacing unchanged payload files. */
    public void SetAddedWideHashAliases(string id, IEnumerable<ulong> aliases)
    {
        RequireEditable();
        var project = Project!;
        var values = new WideHashAliases(aliases);
        var asset = project.AddedAssets.FirstOrDefault(value => value.Id == id)
            ?? throw new KeyNotFoundException("Added asset is not in the project.");
        var native = asset.Native ?? throw new InvalidOperationException("Wide hashes need a native package asset.");
        if ((native.WideHashes ?? WideHashAliases.Empty).Equals(values)) { Status = "No changes"; return; }
        var before = project.CaptureAddedAsset(id, Catalog);
        var document = Document;
        uint tag = native.Tag();
        MutateProject(() => project.SetWideHashAliases(id, values, Catalog!));
        ProjectRevision++;
        PushUndo(new(document?.Entry.TagHash ?? tag, new Dictionary<uint, byte[]>(),
            document?.AddedAssetId ?? (document is null ? id : null), document?.OpenContext,
            new Dictionary<string, AddedAssetSnapshot?> { [id] = before }));
        if (World is not null) RefreshWorld();
        else if (document?.AddedAssetId is { } selected) SelectAdded(selected, document.OpenContext);
        else if (document is not null) Select(document.Entry.TagHash, true, document.OpenContext);
        else SelectAdded(id);
        Status = "Saved wide-hash aliases";
        Changed?.Invoke();
    }
}
