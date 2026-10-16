using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** The category registry, row order, category filter, default open context and viewer tab order. */
internal static class CategoryShellChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const uint Leftover = 0x80A00000, Member = Leftover + 1;
    private const string Name = "Shell check";

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string root)
    {
        string[] names = [.. StudioCategories.All.Select(category => category.Name)];
        Check(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "Two browser categories share a name.");
        var pageIds = new StudioPageRegistry().Pages.Select(page => page.Id).ToHashSet(StringComparer.Ordinal);
        string[] unknown = [.. StudioCategories.All.SelectMany(category => category.Sections).Where(id => !pageIds.Contains(id))];
        Check(unknown.Length == 0, "Category sections name unregistered pages: " + string.Join(", ", unknown));

        var category = new StudioCategory(Name, Name, StudioCategories.WorldGroup, StudioIcon.Cube, "Test category",
            ["sunrise.properties", "sunrise.bytes"], ModelPane: true,
            OpenContext: tag => new AssetOpenContext(PlacementTag: tag, PlacementOffset: 16));
        using var registration = StudioCategories.AddForTest(category, entry =>
            entry.Reference == 0x80800002 ? new StudioCategoryMatch(Name + " / Part", "shellword", 3) : null);
        string install = Path.Combine(root, "category-shell-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_category_shell_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, [1, 2, 3, 4], "Leftover fixture"),
             new(0x80800002, 8 << 9, [5, 6, 7, 8], "Member fixture")]);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "category-shell-settings.json")));
        var workspace = form.Workspace; workspace.CacheOptions = new() { Enabled = false };
        workspace.OpenInstallation(install); Wait(workspace);

        Check(workspace.Assets.Select(row => row.Entry.TagHash).SequenceEqual(new[] { Member, Leftover }),
            "A category row did not sort before a leftover row.");
        var member = workspace.Assets[0];
        Check(member.InCategory && member.Tier == 3 && member.SearchText.Contains("shellword") && !workspace.Assets[1].InCategory,
            "The category row lost its match, tier or tags.");
        Check(workspace.CategoryRows[Name] is [var only] && ReferenceEquals(only, member) && workspace.CategoryCounts[Name] == 1,
            "Category rows or counts were not built with the browser rows.");

        Filter(Name, false, "");
        Check(Rows() is [{ Entry.TagHash: Member }], "The category filter listed a row outside the category.");
        Filter("All categories", true, "");
        Check(Rows() is [{ Entry.TagHash: Leftover }], "All other assets listed a category row.");
        Filter("All categories", false, "shellword");
        Check(Rows() is [{ Entry.TagHash: Member }], "Search missed the category row tags.");

        var context = category.OpenContext!(Member);
        workspace.Open(Member); Wait(workspace);
        Check(workspace.Document is { Kind: Name + " / Part" } opened && opened.OpenContext == context,
            "A plain open did not use the category context.");
        workspace.Open(Member, StudioWorkspace.PlainOpen); Wait(workspace);
        Check(workspace.Document!.OpenContext == StudioWorkspace.PlainOpen, "Open as asset used the category context.");
        Check(StudioCategories.ForKind(workspace.Document!.Kind) is null, "Open as asset drew the category viewer.");
        workspace.NavigateBack(); Wait(workspace);
        Check(workspace.Document!.OpenContext == context && StudioCategories.ForKind(workspace.Document.Kind) is not null,
            "Back lost the category context.");
        workspace.NavigateForward(); Wait(workspace);
        Check(workspace.Document!.OpenContext == StudioWorkspace.PlainOpen && StudioCategories.ForKind(workspace.Document.Kind) is null,
            "Forward lost the plain open.");

        var pages = new StudioPageRegistry().Pages;
        var document = workspace.Document! with { Scene = new SceneDocument() };
        string[] tabs = [.. StudioCategories.Tabs(category, pages, document, true).Select(page => page.Id)];
        Check(tabs.Take(2).SequenceEqual(new[] { "sunrise.properties", "sunrise.bytes" })
            && !tabs.Contains(StudioCategories.PreviewPageId), "Viewer tabs ignore Sections order or list the preview.");
        Check(StudioCategories.Tabs(category, pages, document, false).Any(page => page.Id == StudioCategories.PreviewPageId),
            "Without the model pane the preview tab is missing.");
        Console.WriteLine("editor_category_shell registry=pass order=pass filter=pass context=pass history=pass tabs=pass");

        BrowserAsset[] Rows() => (BrowserAsset[])typeof(StudioForm).GetField("filtered", Private)!.GetValue(form)!;
        void Filter(string browserCategory, bool other, string search)
        {
            foreach (var (field, value) in new (string, object)[]
                { ("category", browserCategory), ("otherAssets", other), ("search", search), ("assetType", "All types") })
                typeof(StudioForm).GetField(field, Private)!.SetValue(form, value);
            typeof(StudioForm).GetMethod("RebuildBrowser", Private)!.Invoke(form, null);
            var watch = Stopwatch.StartNew();
            while (typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is not null
                && watch.Elapsed < TimeSpan.FromSeconds(10))
            { typeof(StudioForm).GetMethod("PollAssetFilter", Private)!.Invoke(form, null); Thread.Sleep(1); }
            Check(typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is null, "Browser filter timed out.");
        }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(20)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error.Length == 0 ? "Workspace timed out." : workspace.Error);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
