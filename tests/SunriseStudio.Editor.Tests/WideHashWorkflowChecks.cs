using System.Diagnostics;
using System.Buffers.Binary;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** Checks the wide-hash workflow: draft snapshots are immutable and undo/redo restore identity, aliases and context without rewriting derived deltas. */
internal static class WideHashWorkflowChecks
{
    private const ulong Key = 0xFEDCBA9876543210, Reserved = 0x1234567890ABCDEF;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string parent)
    {
        string install = Path.Combine(parent, "wide-ui-install");
        Directory.CreateDirectory(Path.Combine(install, "packages"));
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_wide_0100_0.pkg"), 0x100,
            [new(0x80800001, 0x1000, [1, 2, 3], "Wide owner") { WideHashes = new([Reserved]) }]);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.Select(0x80A00000); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "wide-ui-project"), "Wide aliases"); Wait(workspace);
        var asset = workspace.ApplyAssetTransaction([new("derived.bin", [1, 2, 3], new(0xC00, 0x80800001, 0x1000), 0x80A00000)],
            _ => new(new Dictionary<uint, byte[]>() )).Single(); Wait(workspace);
        var context = new AssetOpenContext(PlacementTag: 0x80A00000, PlacementOffset: 12);
        workspace.SelectAdded(asset.Id, context); Wait(workspace);
        string payload = Path.Combine(workspace.Project!.DirectoryPath, asset.Path);
        byte[] bytes = File.ReadAllBytes(payload); DateTime stamp = File.GetLastWriteTimeUtc(payload);
        var draft = new WideHashAliasDraft(workspace.Project, asset.Id);
        draft.Add("0xFEDCBA9876543210"); draft.Add("0000000000000000");
        var snapshot = draft.Values;
        draft.Add("0000000000000001");
        Check(snapshot.Count == 2 && draft.Values.Count == 3, "draft snapshots immutable");
        draft.Remove(1); draft.Save(workspace); Wait(workspace);
        Check(workspace.Catalog!.Resolve64(Key) == 0x82000000 && workspace.Catalog.Resolve64(0) == 0x82000000,
            "saved aliases resolve");
        for (int i = 0; i < 2; i++)
        {
            workspace.Undo(); Wait(workspace);
            Check(workspace.Catalog.Resolve64(Key) is null && workspace.Document?.AddedAssetId == asset.Id
                && workspace.Document.OpenContext == context, "undo preserves selected identity");
            workspace.Redo(); Wait(workspace);
            Check(workspace.Catalog.Resolve64(Key) == 0x82000000 && workspace.Document?.OpenContext == context,
                "redo restores aliases and context");
        }
        Check(File.ReadAllBytes(payload).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(payload) == stamp,
            "metadata undo does not rewrite derived delta");
        var conflict = new WideHashAliasDraft(workspace.Project, asset.Id); conflict.Add(Reserved.ToString("X16"));
        byte[] manifest = File.ReadAllBytes(workspace.Project.ManifestPath);
        long overlay = workspace.Catalog.OverlayRevision; int revision = workspace.ProjectRevision;
        Reject<InvalidDataException>(() => conflict.Save(workspace));
        Check(conflict.Values.Contains(Reserved) && File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest)
            && overlay == workspace.Catalog.OverlayRevision && revision == workspace.ProjectRevision,
            "failed save retains draft manifest overlay and history");
        conflict.Remove(Reserved); conflict.Save(workspace); Wait(workspace);
        var stale = new WideHashAliasDraft(workspace.Project, asset.Id);
        workspace.ApplyBytes([3, 2, 1]); Wait(workspace);
        Reject<InvalidOperationException>(() => stale.Save(workspace));
        workspace.Undo(); Wait(workspace);
        string projectPath = workspace.Project.ManifestPath;
        workspace.CloseProject(); Wait(workspace);
        Check(workspace.Catalog.Resolve64(Key) is null, "close detaches aliases");
        workspace.OpenProject(projectPath); Wait(workspace);
        Check(workspace.Catalog.Resolve64(Key) == 0x82000000, "reopen republishes aliases");
        Reject<InvalidOperationException>(() => stale.Save(workspace));
        workspace.SelectAdded(asset.Id); Wait(workspace);
        var clear = new WideHashAliasDraft(workspace.Project!, asset.Id);
        foreach (ulong key in clear.Values) clear.Remove(key);
        clear.Save(workspace); Wait(workspace);
        Check(workspace.Catalog.Resolve64(Key) is null && !File.ReadAllText(projectPath).Contains("wideHashes"), "clear aliases");
        Input();
        MapBubble(parent);
        Console.WriteLine("editor_wide_hashes undo=pass redo=pass draft=retained reopen=pass input=exact payload=unchanged");
    }

    /** A map's wide reference must follow the same derived bubble before and after package build. */
    private static void MapBubble(string parent)
    {
        string install = Path.Combine(parent, "wide-map-install"); WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        workspace.CreateProject(Path.Combine(parent, "wide-map-project"), "Wide bubble"); Wait(workspace);
        byte[] map = workspace.Catalog!.ReadOriginal(WorldFixture.Base);
        byte[] original = map.ToArray(), bubble = workspace.Catalog.ReadOriginal(WorldFixture.Base + 2);
        int row = checked(0x28 + (int)BinaryPrimitives.ReadInt64LittleEndian(map.AsSpan(0x18)));
        Check(BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan(row - 8)) == 0x80807D53, "map reference row class");
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(row), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(map.AsSpan(row + 8), Key);
        var added = workspace.ApplyAssetTransaction([new("private-bubble", bubble,
            new NativeAssetMetadata(0xC00, 0x80807DAE, 0x100A) { WideHashes = new([Key]) }, WorldFixture.Base + 2)],
            _ => new(new Dictionary<uint, byte[]> { [WorldFixture.Base] = map })).Single(); Wait(workspace);
        Check(workspace.World!.MapScene.ReadTags.Contains(0x82000000)
            && !workspace.World.MapScene.ReadTags.Contains(WorldFixture.Base + 2), "map follows added wide bubble");
        workspace.Undo(); Wait(workspace);
        Check(workspace.Catalog.Resolve64(Key) is null && workspace.World!.MapScene.ReadTags.Contains(WorldFixture.Base + 2),
            "map undo restores direct bubble");
        workspace.Redo(); Wait(workspace);
        Check(workspace.World!.MapScene.ReadTags.Contains(0x82000000), "map redo resolves wide bubble");
        var build = workspace.Project!.Build(workspace.Catalog);
        string verify = Path.Combine(parent, "wide-map-built"), packages = Path.Combine(verify, "packages");
        Directory.CreateDirectory(packages);
        foreach (string source in Directory.GetFiles(Path.Combine(install, "packages")).Concat(build.PackageFiles))
            File.Copy(source, Path.Combine(packages, Path.GetFileName(source)));
        using var catalog = PackageCatalog.Open(verify);
        var world = new WorldLoader(catalog).LoadMission(WorldFixture.Base, WorldFixture.Base + 4);
        Check(world.MapScene.ReadTags.Contains(0x82000000) && catalog.Read(0x82000000).SequenceEqual(bubble)
            && workspace.Project.AddedAssets.Single(value => value.Id == added.Id).BaseTagHash == WorldFixture.Base + 2
            && workspace.Catalog.ReadOriginal(WorldFixture.Base).SequenceEqual(original), "built map alias and delta-only bubble");
    }

    /** The production field must accept a full prefixed key without filtering or truncation. */
    private static unsafe void Input()
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(640, 480); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            string input = "";
            void Frame(bool focus)
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("wide-hash-input");
                if (focus) ImGui.SetKeyboardFocusHere();
                StudioForm.WideHashInput(ref input);
                ImGui.End(); ImGui.Render();
            }
            Frame(true); Frame(false);
            io.AddInputCharactersUTF8("0xFEDCBA9876543210"); Frame(false);
            Check(input == "0xFEDCBA9876543210", "prefixed key UI input");
        }
        finally { ImGui.DestroyContext(context); }
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var elapsed = Stopwatch.StartNew();
        while (workspace.IsBusy && elapsed.Elapsed < TimeSpan.FromSeconds(30)) { workspace.Poll(); Thread.Sleep(1); }
        workspace.Poll(); Check(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException("Alias workflow: " + message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidDataException("Expected " + typeof(T).Name);
    }
}
