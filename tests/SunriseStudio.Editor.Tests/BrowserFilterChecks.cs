using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;

/** Checks the browser filters, especially the changed-only filter, which must not stay hidden when browsing view-only or after reopening a project. */
internal static class BrowserFilterChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Closing a project must leave its changed-only filter inactive in view-only browsing. */
    internal static void Run(string root)
    {
        const uint first = 0x80A00000;
        string install = Path.Combine(root, "browser-filter-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_browser_filter_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, [1, 2, 3, 4], "Changed fixture"),
             new(0x80800002, 8 << 9, [5, 6, 7, 8], "Unchanged fixture")]);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "browser-filter-settings.json")));
        var workspace = form.Workspace; workspace.CacheOptions = new() { Enabled = false };
        workspace.OpenInstallation(install); Wait(workspace);
        Call("RebuildBrowser");
        Check(Rows().Length == 2, "Initial browser list is incomplete.");
        workspace.Select(first); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "browser-filter-project"), "Browser filter"); Wait(workspace);
        workspace.ApplyBytes([1, 9, 3, 4]); Wait(workspace);
        string project = workspace.Project!.ManifestPath;
        typeof(StudioForm).GetField("changesOnly", Private)!.SetValue(form, true);
        Filter();
        Check(Rows() is [{ Entry.TagHash: first }], "Changed-only filter did not select the edited asset.");
        workspace.CloseProject(); Wait(workspace); Filter();
        Check(workspace.Project is null && Rows().Length == 2
            && !(bool)typeof(StudioForm).GetField("changesOnly", Private)!.GetValue(form)!,
            "View-only browsing retained an invisible changed-only filter.");
        workspace.OpenProject(project); Wait(workspace); Filter();
        Check(Rows().Length == 2, "Reopening a project silently reenabled changed-only filtering.");
        typeof(StudioForm).GetField("changesOnly", Private)!.SetValue(form, true); Filter();
        Check(Rows() is [{ Entry.TagHash: first }], "Changed-only filtering could not be enabled again.");
        Console.WriteLine("editor_browser changed_only=pass close=all_assets reopen=all_assets reenable=pass");

        void Call(string name) => typeof(StudioForm).GetMethod(name, Private)!.Invoke(form, null);
        BrowserAsset[] Rows() => (BrowserAsset[])typeof(StudioForm).GetField("filtered", Private)!.GetValue(form)!;
        void Filter()
        {
            Call("FilterBrowser");
            var elapsed = Stopwatch.StartNew();
            while (typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is not null && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            { Call("PollAssetFilter"); Thread.Sleep(1); }
            Check(typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is null && workspace.Error.Length == 0,
                workspace.Error.Length == 0 ? "Browser filter timed out." : workspace.Error);
        }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var elapsed = Stopwatch.StartNew();
        while (workspace.IsBusy && elapsed.Elapsed < TimeSpan.FromSeconds(20)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
