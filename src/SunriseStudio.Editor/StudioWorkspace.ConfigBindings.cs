using SunriseStudio.Formats;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: looks up config bindings (property to mod setting) of the project. */
public sealed partial class StudioWorkspace
{
    /** The binding at a config site, or null. */
    public ProjectConfigBinding? ConfigBindingAt(uint tag, string profile, int row, int? owner = null, int? component = null) =>
        Project?.ConfigBindings.FirstOrDefault(binding => binding.TagHash == tag
            && binding.Site.Profile == profile && binding.Site.RowOffset == row
            && (owner is null || binding.Site.OwnerIndex == owner) && (component is null || binding.Site.ComponentIndex == component));

    /** The binding behind a CUI property, or null when it is not bound. */
    public ProjectConfigBinding? ConfigBindingForProperty(AssetProperty property) => property.CuiOwner is { } owner && Document is { } document
        ? ConfigBindingAt(document.Entry.TagHash, ConfigBindingLimits.PropertyProfile, owner.PropertyOffset,
            owner.ObjectIndex, owner.ComponentIndex) : null;

    /** Inspector scalar edits retain their logical CUI owner even when several values share storage. */
    public void ApplyInspectorProperty(AssetProperty property, string value)
    {
        var current = Document ?? throw new InvalidOperationException("Choose an asset.");
        if (property.CuiOwner is not { } owner) { ApplyBytes(AssetEdit.Apply(current.Bytes, property, value)); return; }
        if (ConfigBindingForProperty(property) is not null)
            throw new InvalidDataException("Unbind this config setting before editing its literal.");
        var document = CuiDocument.Read(current.Entry.TagHash, current.Entry.Reference, current.Bytes);
        if ((uint)owner.ObjectIndex >= document.Objects.Count
            || (uint)owner.ComponentIndex >= document.Objects[owner.ObjectIndex].Components.Count)
            throw new InvalidDataException("The CUI property owner changed.");
        var selected = document.Objects[owner.ObjectIndex].Components[owner.ComponentIndex].Properties
            .SingleOrDefault(candidate => candidate.RecordOffset == owner.PropertyOffset)
            ?? throw new InvalidDataException("The CUI property row changed.");
        if (!selected.Fields.Any(field => field.Offset == property.Offset && field.Encoding == property.Encoding && field.Editable))
            throw new InvalidDataException("The field is not an editable scalar of this property.");
        var pool = document.Pools.Single(pool => pool.DescriptorOffset == selected.PoolDescriptor);
        byte[] encoded = AssetEdit.Apply(current.Bytes, property, value);
        ApplyBytes(CuiPropertyEdits.ApplyToOwner(current.Bytes, document, owner,
            encoded.AsMemory(selected.ValueOffset, pool.Stride)), "Saved CUI property");
    }

    /** The mod config entry the binding refers to, or null. */
    public ModConfigEntry? ConfigSetting(ProjectConfigBinding binding) =>
        Project?.Mod?.Config?.FirstOrDefault(setting => setting.Key == binding.Key && setting.Type == binding.Type);

    /** Only sites prepared from the active raw document can become bindings. */
    public ProjectConfigBinding CreateConfigBinding(ConfigBindingCandidate site, string key)
    {
        RequireEditable();
        var current = Document ?? throw new InvalidOperationException("Choose a CUI resource.");
        if (!current.ConfigSites.Contains(site)) throw new InvalidDataException("Reload the config target before binding it.");
        var setting = Project!.Mod?.Config?.SingleOrDefault(setting => setting.Key == key);
        if (setting is null || setting.Type != site.Type)
            throw new InvalidDataException("Choose a declared setting with the same type.");
        string? hash = null; int? size = null;
        if (current.AddedAssetId is null)
        {
            byte[] original = Catalog!.ReadOriginal(current.Entry.TagHash);
            hash = BinaryDelta.Hash(original); size = original.Length;
        }
        return new(key, site.Type, current.Entry.TagHash, current.AddedAssetId, current.Entry.Reference,
            hash, size, site.Site);
    }

    /** Binding changes are one metadata-only undo step; raw payloads remain unchanged. */
    public void SetConfigBindings(IReadOnlyList<ProjectConfigBinding> bindings)
    {
        RequireEditable();
        var current = Document ?? throw new InvalidOperationException("Choose a CUI resource.");
        var before = Project!.ConfigBindings.ToArray();
        if (before.SequenceEqual(bindings)) return;
        MutateProject(() => Project.SetConfigBindings(bindings, Catalog!), []);
        ProjectRevision++;
        PushUndo(new(current.Entry.TagHash, new Dictionary<uint, byte[]>(), current.AddedAssetId,
            current.OpenContext, ConfigStates: before));
        if (current.AddedAssetId is { } id) SelectAdded(id, current.OpenContext);
        else Select(current.Entry.TagHash, true, current.OpenContext);
        Status = "Config bindings saved"; Changed?.Invoke();
    }
}
