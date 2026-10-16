using System.Reflection;
using System.Text.Json;
using SunriseStudio.Editor;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

/** Checks the weapon page UI: the created weapon reads, its model sits beside the tabs, and the scene starts in bind pose with native clips. */
internal static class WeaponSceneUiChecks
{
    /** The model pane must keep the item and visible geometry beside the Weapon tab during playback. */
    public static void Run(string install, string keys, string manifest, string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Frames(install, keys, manifest, output); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    /** The normal form adopts the workspace action and the scene picker's clip selection. */
    private static void Frames(string install, string keys, string manifest, string output)
    {
        Directory.CreateDirectory(output);
        byte[] originalManifest = File.ReadAllBytes(manifest);
        string settings = Path.Combine(output, "settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: manifest, KeySource: keys,
            Warp: true, Smoke: true, Frames: 20000, SettingsPath: settings));
        const BindingFlags access = BindingFlags.Instance | BindingFlags.NonPublic;
        int phase = 0, frames = 0, clipIndex = 0;
        uint weapon = 0;
        List<(string Name, uint Tag)> clips = [];
        StudioDocument? weaponDocument = null;
        SceneDocument? weaponScene = null;
        form.FrameRendered += () =>
        {
            var workspace = form.Workspace;
            if (!form.EditorReady || workspace.IsBusy || workspace.Project is null) return;
            Check(workspace.Error.Length == 0, workspace.Error);
            if (phase == 0)
            {
                var receipt = workspace.Project.AddedAssets.Single(asset => asset.Name.EndsWith(".creation.json"));
                using var json = JsonDocument.Parse(workspace.Project.ReadAddedAsset(receipt.Id, workspace.Catalog!));
                weapon = json.RootElement.GetProperty("weaponTag").GetUInt32();
                clips.AddRange(json.RootElement.GetProperty("animationClips").EnumerateObject()
                    .Select(row => (row.Name, row.Value.GetUInt32())));
                workspace.Select(weapon); phase++; return;
            }
            if (phase == 1)
            {
                form.ShowAssetPage("sunrise.weapon");
                // The page reads its records on a worker; wait until they land.
                if (++frames < 5 || workspace.LoadedWeapon is not { } loaded || loaded.Weapon != weapon) return;
                Check(loaded.Receipt && loaded.Owned && loaded.Art is not null && loaded.Errors.Count == 0,
                    "The Weapon page did not read the created weapon: " + string.Join("; ", loaded.Errors));
                // A plain open is the weapon view: the model loads with the item.
                Check(workspace.Document is { Scene: not null } opened
                    && ReferenceEquals(form.AssetPreviewViewport!.Scene, opened.Scene)
                    && StudioCategories.ForKind(opened.Kind)?.ModelPane == true,
                    "Opening the weapon did not show its model beside the tabs.");
                Capture("weapon-page");
                workspace.OpenWeaponModel(weapon); phase++; frames = 0; return;
            }
            if (phase == 2)
            {
                if (++frames < 5) return;
                var viewport = form.AssetPreviewViewport!;
                weaponDocument = workspace.Document;
                weaponScene = viewport.Scene;
                Check(weaponDocument?.Entry.TagHash == weapon && weaponDocument.Scene is not null
                    && ReferenceEquals(weaponScene, weaponDocument.Scene) && weaponDocument.Animation is not null
                    && viewport.Animation?.Clip is null, "The weapon scene did not start in bind pose.");
                var extra = viewport.Animation!.Source.Clips.First(row =>
                    !clips.Any(clip => clip.Tag == row.ClipTag)
                    && viewport.Animation.Source.BaseClipTags.Contains(row.ClipTag));
                clips.Add(("linked-existing-clip", extra.ClipTag));
                Check(viewport.Animation.Source.Clips.Count > clips.Count,
                    "The scene contains only the imported demo clips.");
                AssertScene(); Capture("scene-bind-pose");
                viewport.Animation.SelectClip(clips[0].Tag); phase++; frames = 0; return;
            }
            if (phase == 3)
            {
                var animation = form.AssetPreviewViewport!.Animation!;
                if (animation.IsLoading || ++frames < 8) return;
                Check(animation.Clip?.TagHash == clips[clipIndex].Tag && animation.Error is null,
                    "A linked clip failed to play in the weapon scene: " + animation.Error);
                AssertScene(); Capture(clips[clipIndex].Name);
                Console.WriteLine($"weapon_scene_ui clip={clips[clipIndex].Name} visible={form.AssetPreviewViewport.VisibleObjectCount} native={form.AssetPreviewViewport.NativePartCount} document={weapon:X8}");
                if (++clipIndex < clips.Count)
                {
                    animation.SelectClip(clips[clipIndex].Tag); frames = 0; return;
                }
                Check(File.ReadAllBytes(manifest).SequenceEqual(originalManifest),
                    "Animation playback changed the project manifest.");
                phase++; form.Close();
            }
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(phase == 4, "The weapon scene check did not finish.");

        void AssertScene()
        {
            var viewport = form.AssetPreviewViewport!;
            string? page = (string?)typeof(StudioForm).GetField("shownPage", access)!.GetValue(form);
            Check(ReferenceEquals(form.Workspace.Document, weaponDocument)
                && ReferenceEquals(viewport.Scene, weaponScene)
                && page == "asset:sunrise.weapon" && viewport.Scene?.Instances.Count > 0
                && viewport.VisibleObjectCount > 0 && viewport.NativePartCount > 0
                && viewport.UseGameShaders && viewport.NativeError is null,
                "Scene playback lost the weapon, changed its document or left the camera empty.");
        }

        void Capture(string name)
        {
            var renderer = (Dx11Renderer)typeof(StudioForm).GetField("renderer", access)!.GetValue(form)!;
            string path = Path.Combine(output, name + ".png");
            renderer.CapturePng(path);
            Console.WriteLine("weapon_scene_capture path=" + path);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
