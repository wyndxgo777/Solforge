namespace SunriseStudio.Editor;

/** MissionGraphPage part: entry point for the creation dialog. */
public sealed partial class MissionGraphPage
{
    /** Opens the existing New mission script dialog the next time this page draws. */
    public void RequestNewScript() => RequestSwitch(() => showNewScript = true);
}
