using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Published editor snapshots must already contain their complete preparation outcome. */
internal static class ReadinessChecks
{
    /** Requires that the workspace has published its catalog and dependent state. */
    internal static void AssertPublished(StudioWorkspace workspace)
    {
        if (workspace.Catalog is null)
        {
            Require(workspace.Names is null && workspace.WorldCatalog is null && workspace.Assets.Length == 0,
                "Catalog-dependent state was published before its installation.");
            return;
        }
        Require(workspace.Names is { Coverage.Complete: true } && workspace.WorldCatalog is not null,
            "Installation was published before its names and world catalog completed.");
        Require(workspace.Assets.All(row => ReferenceEquals(row.Names, workspace.Names)),
            "Browser rows and name index came from different preparation snapshots.");
        if (workspace.World is { } world && NeedsNative(world.Scene))
            Require(workspace.NativePlan is not null && ReferenceEquals(workspace.NativePlanScene, world.Scene)
                || !string.IsNullOrWhiteSpace(workspace.NativeError),
                "World was published before native preparation finished.");
        if (workspace.Document is { Scene: { } scene } document && NeedsNative(scene))
            Require(document.NativePlan is not null || !string.IsNullOrWhiteSpace(document.NativeError),
                "Asset scene was published before native preparation finished.");
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "ready-install");
        WorldFixture.Create(install);
        const uint textureTag = 0x80A02000;
        byte[] header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 0xCAFE);
        foreach (int field in new[] { 0x0E, 0x10, 0x12, 0x14 })
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(field), 1);
        header[0x17] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), uint.MaxValue);
        PackageWriter.WriteNew(Path.Combine(install, "packages", "w64_ready_0101_0.pkg"), 0x101,
            [new(textureTag + 1, (32 << 9) | (1 << 6) | 10, header, "Ready texture"),
             new(uint.MaxValue, (40 << 9) | (1 << 6) | 10, [30, 40, 50, 255])]);
        using (var workspace = new StudioWorkspace())
        {
            workspace.OpenInstallation(install);
            AssertPublished(workspace);
            Wait(workspace);
            var originalCatalog = workspace.Catalog;
            var originalNames = workspace.Names;
            var originalWorldCatalog = workspace.WorldCatalog;
            workspace.IndexNames();
            Require(ReferenceEquals(workspace.Names, originalNames)
                && ReferenceEquals(workspace.WorldCatalog, originalWorldCatalog),
                "Reindex replaced completed names before its worker finished.");
            Wait(workspace);
            Require(ReferenceEquals(workspace.Catalog, originalCatalog) && !ReferenceEquals(workspace.Names, originalNames),
                "Completed name refresh was not adopted as a new snapshot.");
            workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4);
            Require(workspace.World is null, "Mission was published before the opening operation completed.");
            Wait(workspace);
            Require(workspace.World is { AllStates.Count: 3 }, "Published mission omitted authored state definitions.");
            var scene = workspace.World!.Scene;
            workspace.Select(textureTag);
            Wait(workspace);
            Require(ReferenceEquals(workspace.World.Scene, scene) && workspace.Document?.Texture is { Width: 1, Height: 1 },
                "Selecting a prepared texture replaced the world or omitted its preview.");
            workspace.IndexNames();
            Wait(workspace);
            Require(ReferenceEquals(workspace.World.Scene, scene), "Name preparation replaced the loaded world geometry.");
            string invalid = Path.Combine(root, "invalid-ready-install");
            Directory.CreateDirectory(Path.Combine(invalid, "packages"));
            File.WriteAllBytes(Path.Combine(invalid, "packages", "invalid.pkg"), [1, 2, 3]);
            var priorNames = workspace.Names;
            workspace.OpenInstallation(invalid);
            Wait(workspace, expectFailure: true);
            Require(ReferenceEquals(workspace.Catalog, originalCatalog) && ReferenceEquals(workspace.Names, priorNames)
                && ReferenceEquals(workspace.World!.Scene, scene), "Failed preparation replaced the last completed installation.");
        }
        Console.WriteLine("editor_ready catalog=atomic names=complete reindex=atomic world=retained failure=retained");
        RunGui(install, root, textureTag);
    }

    /** The first revealed asset frame already owns its GPU data and selection index. */
    private static void RunGui(string install, string root, uint textureTag)
    {
        var project = StudioProject.Create(Path.Combine(root, "ready-project"), "Readiness fixture");
        var added = project.AddAsset("ready.obj", Encoding.UTF8.GetBytes(
            "o Ready\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n"));
        int phase = 0, readyFrames = 0, loadingFrames = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new StudioForm(new(Game: install, Project: project.ManifestPath,
                    Tag: textureTag, Warp: true, Smoke: true, Frames: 24,
                    Capture: Path.Combine(root, "readiness-ui.png"), SettingsPath: Path.Combine(root, "readiness-settings.json")));
                var page = new ReadyPageProbe(() => form.EditorReady);
                form.Pages.Register(page, first: true);
                SceneDocument? retainedWorld = null;
                MeshInstance? selectedAssetPart = null;
                AssetViewport? secondaryPreview = null;
                int expectedPageDraw = 0;
                form.FrameRendered += () =>
                {
                    AssertPublished(form.Workspace);
                    if (!form.EditorReady) { loadingFrames++; return; }
                    readyFrames++;
                    var workspace = form.Workspace;
                    if (workspace.IsBusy) return;
                    Require(workspace.Names?.Coverage.Complete == true && workspace.WorldCatalog is not null,
                        "Editor menus were revealed before all names and catalogs were ready.");
                    if (phase == 7)
                    {
                        Require(workspace.World is null && workspace.Document is null, "Closing the world retained its document.");
                        Require(secondaryPreview is { HasContent: false, MeshCount: 0, NativeSceneReady: false,
                                SelectedObject: null, Animation: null }
                            && secondaryPreview.Selection.Objects.Count == 0,
                            "Closing the world retained secondary preview resources or selection.");
                        Require(form.SceneViewport!.Visibility.HiddenLayers.Count == 0,
                            "World-only visibility filters survived closing the world.");
                        Require(form.SceneViewport.Visibility.HiddenGeometryLayers.Count == 0
                            && form.SceneViewport.Visibility.HiddenOverlayLayers.Count == 0,
                            "Split geometry or overlay filters survived closing the world.");
                        workspace.SelectAdded(added.Id);
                        phase++;
                        return;
                    }
                    if (phase == 8)
                    {
                        var standalone = workspace.Document?.Scene ?? throw new InvalidOperationException("Standalone asset preview is missing.");
                        Require(workspace.World is null && ReferenceEquals(form.AssetPreviewViewport, form.SceneViewport)
                            && ReferenceEquals(form.SceneViewport!.Scene, standalone) && form.SceneViewport.MeshCount == 1,
                            "Standalone asset did not adopt the primary viewport after closing the world.");
                        Require(standalone.Instances.All(x => form.SceneViewport!.IsVisible(x)),
                            "World filters hid the newly opened standalone asset.");
                        phase++;
                        return;
                    }
                    if (phase >= 9) return;
                    if (phase == 0)
                    {
                        Require(workspace.Document?.Entry.TagHash == textureTag && form.AssetPreviewViewport?.Texture is { Width: 1 },
                            "Startup editor appeared before the requested texture and its GPU preview were ready.");
                        Require(page.DrawCount > 0, "The ready asset page was not drawn.");
                        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4);
                        phase++;
                        return;
                    }
                    var world = workspace.World ?? throw new InvalidOperationException("Mission editor was revealed before its world was ready.");
                    Require(ReferenceEquals(form.SceneViewport!.Scene, world.Scene), "Editor revealed a world before viewport adoption.");
                    Require(world.Scene.Instances.All(x => form.SceneViewport.Selection.Objects.Contains(x))
                        && world.Scene.Markers.All(x => form.SceneViewport.Selection.Objects.Contains(x)),
                        "Editor revealed a world before its selection index was ready.");
                    if (NeedsNative(world.Scene) && workspace.NativePlan is not null)
                        Require(form.SceneViewport.NativeSceneReady || !string.IsNullOrWhiteSpace(form.SceneViewport.NativeError),
                            "Editor revealed the world before the native renderer had an outcome.");
                    if (phase == 1)
                    {
                        retainedWorld = world.Scene;
                        var marker = world.Scene.Markers.First(x => x.Layer == SceneLayer.Spawns);
                        form.SelectSceneObject(marker, true);
                        Require(form.SelectedObjectDetails is not null && ReferenceEquals(form.SceneViewport.SelectedObject, marker),
                            "Selection was unavailable on the first ready frame.");
                        workspace.Select(textureTag);
                        phase++;
                    }
                    else if (phase == 2)
                    {
                        Require(workspace.Document?.Entry.TagHash == textureTag && form.AssetPreviewViewport?.Texture is { Width: 1 },
                            "Texture asset was revealed before its GPU preview was prepared.");
                        Require(ReferenceEquals(world.Scene, retainedWorld), "Asset readiness replaced the retained world.");
                        workspace.SelectAdded(added.Id);
                        phase++;
                    }
                    else if (phase == 3)
                    {
                        var scene = workspace.Document?.Scene ?? throw new InvalidOperationException("Owned mesh preview is missing.");
                        var preview = form.AssetPreviewViewport!;
                        Require(ReferenceEquals(preview.Scene, scene) && preview.MeshCount == 1
                            && scene.Instances.All(x => preview.Selection.Objects.Contains(x)),
                            "Mesh asset was revealed before its geometry or selection index was prepared.");
                        preview.SetSelection(scene.Instances[0]);
                        selectedAssetPart = scene.Instances[0];
                        Require(preview.Selection.GetBounds(scene.Instances[0]).IsFinite, "Ready asset bounds were unavailable.");
                        expectedPageDraw = page.DrawCount;
                        form.ShowAssetPage(page.Id);
                        phase++;
                    }
                    else if (phase == 4)
                    {
                        if (page.DrawCount <= expectedPageDraw) return;
                        Require(ReferenceEquals(page.SelectedMesh, selectedAssetPart),
                            "Asset part selection did not reach the Parts/export page context.");
                        var marker = world.Scene.Markers.First(x => x.Layer == SceneLayer.Spawns
                            && !ReferenceEquals(x, form.SceneViewport.SelectedObject));
                        form.SelectSceneObject(marker);
                        Require(ReferenceEquals(form.SelectedObjectDetails?.Object.Source, marker), "World marker selection was not adopted.");
                        expectedPageDraw = page.DrawCount;
                        form.ShowAssetPage(page.Id);
                        phase++;
                    }
                    else if (phase == 5)
                    {
                        if (page.DrawCount <= expectedPageDraw) return;
                        Require(ReferenceEquals(page.SelectedMesh, selectedAssetPart)
                            && ReferenceEquals(form.AssetPreviewViewport?.SelectedObject, selectedAssetPart)
                            && workspace.Document?.AddedAssetId == added.Id,
                            "Selecting a world marker replaced the asset part or Parts/export context.");
                        workspace.IndexNames();
                        phase++;
                    }
                    else if (phase == 6)
                    {
                        Require(ReferenceEquals(world.Scene, retainedWorld) && workspace.Document?.AddedAssetId == added.Id,
                            "Name readiness discarded the selected asset or world.");
                        Require(form.AssetPreviewViewport?.SelectedObject is MeshInstance,
                            "Name-only preparation discarded asset selection.");
                        secondaryPreview = form.AssetPreviewViewport;
                        Require(!ReferenceEquals(secondaryPreview, form.SceneViewport), "World asset preview was not separate.");
                        Require(form.SceneViewport.OccludeOverlays
                            && form.SceneViewport.Visibility.HiddenOverlayLayers.Contains(SceneLayer.Lights),
                            "World fixture did not retain occluded overlays and its light filter.");
                        form.SceneViewport.Visibility.HiddenGeometryLayers.Add(SceneLayer.Geometry);
                        workspace.CloseWorld();
                        phase++;
                    }
                };
                Application.Run(form);
                if (form.Failure is { } error) throw new InvalidOperationException(error);
                Require(phase == 9 && readyFrames > 0 && loadingFrames > 0, "Readiness UI phases did not complete.");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Readiness UI test timed out.");
        if (failure is not null) throw failure;
        Console.WriteLine("editor_ready loading-only=pass first-frame-selection=pass texture-gpu=pass mesh-gpu=pass retained-world=pass");
        Console.WriteLine("editor_context asset-part=retained world-close=released standalone-filters=reset");
    }

    private static bool NeedsNative(SceneDocument scene) => scene.Instances.Any(x => x.Mesh.Native is not null);

    private static void Wait(StudioWorkspace workspace, bool expectFailure = false)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            AssertPublished(workspace);
            workspace.Poll();
            AssertPublished(workspace);
            if (!workspace.IsBusy) break;
            Thread.Sleep(2);
        } while (watch.Elapsed < TimeSpan.FromSeconds(30));
        Require(!workspace.IsBusy, "Editor preparation timed out.");
        Require(expectFailure ? workspace.Error.Length > 0 : workspace.Error.Length == 0,
            expectFailure ? "Invalid installation unexpectedly succeeded." : workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private sealed class ReadyPageProbe(Func<bool> ready) : IStudioPage
    {
        public string Id => "tests.readiness";
        public string Title => "Readiness";
        public int DrawCount { get; private set; }
        public MeshInstance? SelectedMesh { get; private set; }
        /** Offers the page for any document that has a texture or a scene. */
        public bool CanOpen(StudioDocument document) => document.Texture is not null || document.Scene is not null;
        /** Counts draws and records the selected mesh for the readiness checks. */
        public void Draw(StudioPageContext context)
        {
            Require(ready(), "An asset page drew while the editor was still preparing.");
            DrawCount++;
            SelectedMesh = context.SelectedMesh;
            context.Viewport.DrawImGui(ImGuiNET.ImGui.GetContentRegionAvail());
        }
    }
}
