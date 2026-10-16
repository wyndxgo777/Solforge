using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Packages;

/** Checks that assets added by a project show up in the browser as changed rows without displacing installed rows. */
internal static class BrowserAddedChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    /** Native additions share browser routing while source-only files remain in Changes. */
    internal static void Run(string root)
    {
        const uint first = 0x80A00000, addedTag = 0x80A02000;
        string install = Path.Combine(root, "browser-added-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_browser_added_0100_0.pkg"), 0x100,
            [new(0x80800001, 8 << 9, [1, 2, 3, 4], "Installed fixture"),
             new(0x80809C36, 8 << 9, Component(ComponentRoles.LightComponent), "Light config")]);
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "browser-added-settings.json")));
        var workspace = form.Workspace; workspace.CacheOptions = new() { Enabled = false };
        workspace.OpenInstallation(install); Wait(workspace);
        var originals = workspace.Assets.ToArray();
        workspace.Select(first); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "browser-added-project"), "Browser additions"); Wait(workspace);
        var added = workspace.ApplyAssetTransaction(
            [new("Aurora lantern", Entity(first + 1), new(0x101, 0x80809C0F, 8 << 9)),
             new("Cobalt payload", [5, 6, 7], new(0x101, 0x80800001, 8 << 9)),
             new("Reference.txt", [65, 66])], _ => new(new Dictionary<uint, byte[]>()));
        Wait(workspace);
        Check(workspace.Assets.Length == 4 && workspace.Catalog!.Entries.Count == 2
            && workspace.Project!.AddedAssets.Count == 3, "Native and source-only browser membership is wrong.");
        Check(originals.Zip(workspace.Assets).All(pair => ReferenceEquals(pair.First, pair.Second)),
            "Adding assets replaced unrelated installed rows.");
        Check(workspace.BrowserKinds.Contains("Entity / Light")
            && workspace.BrowserPackages.Contains("w64_sunrise_0101_0"), "Added menus are missing.");
        Check(workspace.IsModified(addedTag) && workspace.IsModified(addedTag + 1), "Added rows are not marked changed.");
        Check(workspace.CategoryNameOf(addedTag) == "Entity" && workspace.CategoryRowsOf("Entity").Any(row => row.Entry.TagHash == addedTag)
            && workspace.IsProjectTag(addedTag) && !workspace.IsProjectTag(first), "Creation pickers miss an added category row.");
        Check(workspace.ClassRows(PickClass.InteractionConfig).Select(row => row.Entry.TagHash).SequenceEqual(new[] { first + 1 }),
            "Creation class rows differ from the installed configs.");
        Filter(); Expect(first, first + 1, addedTag, addedTag + 1);
        Filter(search: "Aurora"); Expect(addedTag);
        Filter(search: (addedTag + 1).ToString("X8")); Expect(addedTag + 1);
        Filter(category: "Entity", type: "Light"); Expect(addedTag);
        Filter(category: "Entity"); Expect(addedTag);
        // The package filter lives in All other assets, which leaves out rows with a category viewer.
        Filter(package: "w64_sunrise_0101_0", other: true); Expect(addedTag + 1);
        Filter(changed: true); Expect(addedTag, addedTag + 1);
        Check(workspace.CategoryRowsOf("Entity").Count(row => row.Entry.TagHash == addedTag) == 1,
            "Creation picker duplicated the added browser row.");
        workspace.Select(Rows().Single(row => row.Entry.TagHash == addedTag + 1).Entry.TagHash); Wait(workspace);
        Check(workspace.Document?.AddedAssetId == added[1].Id && workspace.Document.Bytes.SequenceEqual(new byte[] { 5, 6, 7 }),
            "Browser selection lost the added asset identity or payload.");
        workspace.Select(first + 1); Wait(workspace);
        workspace.ApplyBytes(Component(ComponentRoles.ModelComponent)); Wait(workspace);
        Check(workspace.Assets.Single(row => row.Entry.TagHash == addedTag).Kind == "Entity / Prop",
            "Added entity category did not follow an edited installed dependency.");
        Filter(changed: true); Expect(first + 1, addedTag, addedTag + 1);
        workspace.Undo(); Wait(workspace);
        Check(workspace.Assets.Single(row => row.Entry.TagHash == addedTag).Kind == "Entity / Light",
            "Undo did not restore the added entity category.");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Assets.Length == 2 && !workspace.BrowserKinds.Contains("Entity / Light")
            && !workspace.BrowserPackages.Contains("w64_sunrise_0101_0"), "Undo retained added rows or menus.");
        workspace.Redo(); Wait(workspace);
        Check(workspace.Assets.Length == 4, "Redo did not restore native additions.");
        workspace.IndexNames(); Wait(workspace);
        Filter(search: "Cobalt"); Expect(addedTag + 1);
        string project = workspace.Project!.ManifestPath;
        workspace.CloseProject(); Wait(workspace); Filter(); Expect(first, first + 1);
        Check(!workspace.BrowserPackages.Contains("w64_sunrise_0101_0"), "Close retained an added package filter.");
        workspace.OpenProject(project); Wait(workspace); Filter(changed: true); Expect(addedTag, addedTag + 1);
        workspace.RemoveAdded(added[0].Id); Wait(workspace); Filter(changed: true); Expect(addedTag + 1);
        Check(!workspace.BrowserKinds.Contains("Entity / Light"), "Removal retained the added-only category.");
        workspace.RemoveAdded(added[1].Id); Wait(workspace); Filter(); Expect(first, first + 1);
        Check(workspace.Project!.AddedAssets.Single().Native is null
            && !workspace.BrowserPackages.Contains("w64_sunrise_0101_0"), "Source-only asset retained native browser metadata.");
        Console.WriteLine("editor_added_browser rows=pass filters=pass selection=pass dependencies=pass history=pass close=pass source_only=changes");

        BrowserAsset[] Rows() => (BrowserAsset[])typeof(StudioForm).GetField("filtered", Private)!.GetValue(form)!;
        void Expect(params uint[] tags) => Check(Rows().Select(row => row.Entry.TagHash).SequenceEqual(tags),
            "Browser membership or order differs from the requested filter.");
        void Filter(string search = "", string category = "All categories", string type = "All types",
            string package = "All packages", bool changed = false, bool other = false)
        {
            foreach (var (name, value) in new (string, object)[]
                { ("search", search), ("category", category), ("assetType", type), ("package", package), ("changesOnly", changed),
                  ("otherAssets", other) })
                typeof(StudioForm).GetField(name, Private)!.SetValue(form, value);
            typeof(StudioForm).GetMethod("RebuildBrowser", Private)!.Invoke(form, null);
            var watch = Stopwatch.StartNew();
            while (typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is not null
                && watch.Elapsed < TimeSpan.FromSeconds(10))
            { typeof(StudioForm).GetMethod("PollAssetFilter", Private)!.Invoke(form, null); Thread.Sleep(1); }
            Check(typeof(StudioForm).GetField("filterWork", Private)!.GetValue(form) is null, "Browser filter timed out.");
        }
    }

    private static byte[] Entity(uint config)
    {
        byte[] bytes = new byte[176];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 128 - 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(124), 0x80809FBD);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(136), 0x80809C04);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(144), config);
        return bytes;
    }

    private static byte[] Component(uint primary)
    {
        byte[] bytes = new byte[96];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 48 - 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), primary);
        return bytes;
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
