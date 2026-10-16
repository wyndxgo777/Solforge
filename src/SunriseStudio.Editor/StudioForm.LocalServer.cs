namespace SunriseStudio.Editor;

/** View > Local Server: the local WSL sol-server manager window (LocalServerPage). */
public sealed partial class StudioForm
{
    private readonly LocalServerPage localServerPage = new();
    private bool localServerOpen;

    private void DrawLocalServer() =>
        localServerPage.Draw(ref localServerOpen, scale, workspace.Project?.DirectoryPath, workspace.LastBuild?.OutputDirectory);
}
