using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;

/** A finished worker is adopted by the frame tick even when the frame itself is skipped (minimised, dialog, no renderer). */
internal static class TickPollChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Opens a fixture install, lets the read finish, then ticks without drawing: the result must be adopted. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "tick-poll-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_tick_poll_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, [1, 2, 3, 4], "Installed fixture")]);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "tick-poll-settings.json")));
        var workspace = form.Workspace; workspace.CacheOptions = new() { Enabled = false };
        workspace.OpenInstallation(install);
        var watch = Stopwatch.StartNew();
        // Nothing polls here, as in a minimised window: the worker finishes but the result is not adopted.
        while (workspace.Catalog is null && watch.Elapsed < TimeSpan.FromSeconds(2)) Thread.Sleep(20);
        Check(workspace.IsBusy && workspace.Catalog is null, "The result was adopted without a poll.");
        var tick = typeof(StudioForm).GetMethod("TickFrame", Private)!;
        watch.Restart();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { tick.Invoke(form, null); Thread.Sleep(5); }
        Check(!workspace.IsBusy && workspace.Catalog is not null && workspace.Error.Length == 0,
            "A skipped frame tick did not adopt the finished catalog read: " + workspace.Error);
        form.PollWhileSkipping("minimised");
        Console.WriteLine("editor tick_poll skipped-frame=adopts-worker minimised-path=pollable");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
