using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

/** Checks the browser config-binding behavior: binding-only changes mark rows changed, and unbind/undo/redo/reopen leave raw bytes and membership intact. */
internal static class BrowserConfigChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** A binding-only replacement is changed even though its editable bytes stay original. */
    internal static void Run(string root)
    {
        const uint tag = 0x80A00000;
        string install = Path.Combine(root, "browser-config-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        byte[] original = ConfigBindingFixture.Create();
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_browser_config_0100_0.pkg"), 0x100,
            [new(0x80804825, 8 << 9, original, "Bound-only widget"),
             new(0x80800001, 8 << 9, [1, 2], "Unchanged fixture")]);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "browser-config-settings.json")));
        var workspace = form.Workspace; workspace.CacheOptions = new() { Enabled = false };
        workspace.OpenInstallation(install); Wait(); workspace.Select(tag); Wait();
        workspace.CreateProject(Path.Combine(root, "browser-config-project"), "Bound-only browser"); Wait();
        workspace.SetModMetadata(new("test.browser", "1.0.0", "Browser", Config:
            [new("position", ModConfigType.Float, "123.5")]));
        var site = workspace.Document!.ConfigSites.First(site => site.Type == ModConfigType.Float && site.Site.OwnerIndex == 0);
        CheckState(false);
        workspace.SetConfigBindings([workspace.CreateConfigBinding(site, "position")]); Wait();
        CheckState(true);
        workspace.SetConfigBindings([]); Wait(); CheckState(false);
        workspace.Undo(); Wait(); CheckState(true);
        workspace.Redo(); Wait(); CheckState(false);
        workspace.Undo(); Wait(); CheckState(true);
        string manifest = workspace.Project!.ManifestPath;
        workspace.CloseProject(); Wait();
        Check(!workspace.IsModified(tag), "Closing retained the binding-only changed marker.");
        workspace.OpenProject(manifest); Wait(); CheckState(true);
        Console.WriteLine("editor_config_browser binding_only=pass unbind=pass undo=pass redo=pass reopen=pass raw=unchanged");

        void CheckState(bool changed)
        {
            Check(workspace.Project!.Changes.Count == 0 && workspace.Project.AddedAssets.Count == 0
                && workspace.Document!.Bytes.SequenceEqual(original), "Browser fixture changed raw bytes or membership.");
            Check(workspace.IsModified(tag) == changed, "Binding-only changed marker is wrong.");
            typeof(StudioForm).GetField("changesOnly", Private)!.SetValue(form, true);
            typeof(StudioForm).GetMethod("RebuildBrowser", Private)!.Invoke(form, null);
            var watch = Stopwatch.StartNew();
            while (typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is not null
                && watch.Elapsed < TimeSpan.FromSeconds(10))
            { typeof(StudioForm).GetMethod("PollAssetFilter", Private)!.Invoke(form, null); Thread.Sleep(1); }
            Check(typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is null, "Browser filter timed out.");
            var rows = (BrowserAsset[])typeof(StudioForm).GetField("filtered", Private)!.GetValue(form)!;
            Check(rows.Select(row => row.Entry.TagHash).SequenceEqual(changed ? new[] { tag } : []),
                "Changed-only filter missed or retained a binding-only replacement.");
        }

        void Wait()
        {
            var watch = Stopwatch.StartNew();
            while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { workspace.Poll(); Thread.Sleep(1); }
            workspace.Poll();
            Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
