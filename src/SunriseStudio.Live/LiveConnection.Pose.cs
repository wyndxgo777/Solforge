namespace SunriseStudio.Live;

/** Partial: exposes the skinned-pose channel (draw palettes and its diagnostic text). */
public sealed partial class LiveConnection
{
    private LivePoseConnection? pose;
    public LiveDraws? Draws => pose?.Draws;
    public string PoseDiagnostic => pose?.Diagnostic ?? "";

    /**
     * Asks the game to copy the completed skinned draws of these owners; it never evaluates or
     * changes game animation.
     */
    public void RequestDraws(bool enabled, IEnumerable<uint> owners, uint? player = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (enabled) pose ??= new(process.Id);
        if (enabled) pose!.SetOwners(owners);
        pose?.SetPlayer(enabled ? player : null);
        pose?.Enable(enabled);
    }
}
