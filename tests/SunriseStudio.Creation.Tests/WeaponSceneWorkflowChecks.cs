using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Formats;

namespace SunriseStudio.Creation.Tests;

/** Checks that a newly created weapon opens a scene in bind pose with its full animation catalog and keeps geometry when an animation is selected. */
internal static class WeaponSceneWorkflowChecks
{
    /** Clip navigation keeps the weapon item, complete catalog and geometry together. */
    public static void Run(StudioWorkspace workspace, uint weaponTag, IReadOnlySet<uint> authoredClips)
    {
        workspace.OpenWeaponModel(weaponTag); Wait(workspace);
        var scene = workspace.Document ?? throw new InvalidDataException("The weapon scene did not open.");
        Check(scene.Entry.TagHash == weaponTag && scene.Scene?.Instances.Count > 0
            && scene.Animation?.Clips.Count > authoredClips.Count && scene.Connections?.SelectedClipTag == 0,
            "The weapon scene did not start in bind pose with its complete animation catalog.");
        Check(workspace.TakePendingView()?.Page == "asset:sunrise.preview",
            "The weapon button did not request the scene tab.");
        int parts = scene.Scene!.Instances.Count;
        uint owner = scene.Connections!.AnimationEntityTag;
        uint extra = scene.Animation!.BaseClipTags.First(tag => !authoredClips.Contains(tag));
        foreach (uint clip in authoredClips.Append(extra))
        {
            workspace.PreviewWeaponAnimation(weaponTag, clip, owner); Wait(workspace);
            CheckCurrent(clip);
        }
        var related = workspace.Document!.Connections!.Related.First(link =>
            link.Kind == AssetRelationshipKind.AnimationClip && link.TagHash == extra);
        workspace.SelectRelated(related); Wait(workspace); CheckCurrent(extra);
        Console.WriteLine($"weapon_scene_navigation clips={scene.Animation.Clips.Count} checked={authoredClips.Count + 1} related=pass item=retained");

        void CheckCurrent(uint clip) => Check(workspace.Document is { } document
            && document.Entry.TagHash == weaponTag && document.Scene?.Instances.Count == parts
            && document.Animation?.Clips.Count == scene.Animation.Clips.Count
            && document.Connections?.SelectedClipTag == clip,
            "Selecting an animation replaced the weapon scene or lost its geometry.");
    }

    private static void Wait(StudioWorkspace workspace)
    {
        var timer = Stopwatch.StartNew();
        while (workspace.IsBusy && timer.Elapsed < TimeSpan.FromMinutes(5)) { workspace.Poll(); Thread.Sleep(2); }
        workspace.Poll();
        Check(!workspace.IsBusy && workspace.Error.Length == 0,
            workspace.Error.Length == 0 ? "The weapon scene timed out." : workspace.Error);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
