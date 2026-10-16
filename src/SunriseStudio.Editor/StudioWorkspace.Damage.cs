namespace SunriseStudio.Editor;

/** StudioWorkspace part: damage config edits. */
public sealed partial class StudioWorkspace
{
    /** Untested: writes one damage config edit as one undo step. */
    public void ApplyDamageEdit(uint configTag, Func<uint, ReadOnlyMemory<byte>, byte[]> edit, string action)
    {
        RequireEditable();
        byte[] changed = edit(configTag, Catalog!.Read(configTag));
        ApplyAssetEdits(new Dictionary<uint, byte[]> { [configTag] = changed }, action);
    }
}
