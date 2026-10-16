using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Scene;

/** An entity opens on its first authored spawn; the picker reaches the native default. */
internal static class SpawnPreviewChecks
{
    private const uint Centurion = 0x80C19B1F, Model = 0x80FEE1D0;

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string gameRoot)
    {
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(gameRoot); Wait(workspace);
        workspace.Select(Centurion); Wait(workspace);
        var opened = workspace.Document!.Scene!;
        Require(opened.PreviewSpawn == new SpawnInput(0x80B3C24C, 0x8B0) && opened.SpawnChoices.Count == 17
            && opened.SpawnChoices[0].Input == opened.PreviewSpawn && Decal(opened).InitiallyVisible == false,
            "The Centurion did not open on its first authored spawn with the decal hidden.");
        workspace.PreviewSpawn(null); Wait(workspace);
        var native = workspace.Document!.Scene!;
        Require(native.PreviewSpawn is null && native.SpawnChoices.Count == 17 && Decal(native).InitiallyVisible == true,
            "Choosing the native default did not restore the old state.");
        var top = native.SpawnChoices.Single(choice => choice.RowCount == 639);
        workspace.PreviewSpawn(top.Input); Wait(workspace);
        var chosen = workspace.Document!.Scene!;
        Require(chosen.PreviewSpawn == top.Input && top.Variant == 0xBEF7FFEC && Decal(chosen).InitiallyVisible == false,
            "Choosing the top spawn did not reread the entity with its input.");
        Console.WriteLine($"editor_spawn_preview entity={Centurion:X8} inputs={native.SpawnChoices.Count} "
            + "first=80B3C24C+8B0 native=restored top=BEF7FFEC");
    }

    private static MeshInstance Decal(SceneDocument scene) =>
        scene.Instances.Single(instance => instance.Mesh.TagHash == Model && instance.Mesh.PartIndex == 13328);

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(300)) { workspace.Poll(); Thread.Sleep(2); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, "Spawn preview workflow failed: " + workspace.Error);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
