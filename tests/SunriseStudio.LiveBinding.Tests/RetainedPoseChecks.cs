using System.Numerics;
using SunriseStudio.Editor;
using SunriseStudio.Live;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks retained poses: a transient read failure keeps the last fresh pose for drawing and picking instead of snapping to authored placement. */
internal static partial class Program
{
    /** Transient reads retain one lifetime's last sample, with no prediction or extension of its TTL. */
    private static void RetainedPoses(AssetViewport viewport, Dx11Renderer renderer)
    {
        var (world, mesh) = Fixture();
        var marker = new SceneMarker { ObjectPlacement = mesh.ObjectPlacement, Position = new(101, 0, 0),
            Extents = new(.1f), Layer = SceneLayer.Spawns };
        world.Scene.Markers.Add(marker); world = WorldDocument.FromScene(world.Scene);
        viewport.SetScene(world.Scene); viewport.Camera.LookFrom(new(0, -4, 0), Vector3.Zero);
        var binding = new LiveSceneBinding(world);
        var original = Row(0);
        var good = Snapshot([original]);
        binding.Apply(good, viewport, Now);
        var matrix = mesh.RenderTransform; var point = marker.RenderPosition;
        var failed = Snapshot([original with { TransformAvailable = false, Time = 0 }]) with { Time = Now + 50 };
        binding.Apply(failed, viewport, Now + 50);
        Check(Near(mesh.RenderTransform, matrix) && marker.RenderPosition == point && binding.Matched == 1,
            "A temporary read failure snapped a fresh pose back to authored placement.");
        var image = viewport.RenderImage(320, 240);
        Check(ReferenceEquals(viewport.Pick(new(160, 120), new(320, 240)), mesh)
            && renderer.ReadTexturePixel(image, 160, 120).X > .2f,
            "Retained placement was missing from GPU drawing or picking.");
        var recovery = original with { Position = new(.5f, 0, 0), Time = Now + 70 };
        binding.Apply(Snapshot([recovery]) with { Time = recovery.Time }, viewport, recovery.Time);
        Check(mesh.RenderTransform.Translation.X == .5f && marker.RenderPosition.X == 1.5f,
            "A recovered read did not replace the retained pose.");
        var partial = Snapshot([]) with { Time = Now + 100, Complete = false };
        binding.Apply(partial, viewport, Now + 100);
        Check(mesh.RenderTransform.Translation.X == .5f && marker.RenderPosition.X == 1.5f,
            "An omitted partial row cleared a fresh pose.");
        binding.Apply(partial, viewport, recovery.Time + LiveWorldProtocol.StaleAfterMilliseconds);
        Check(mesh.PreviewTransform is not null, "Retained pose expired before its inclusive TTL.");
        binding.Apply(partial, viewport, recovery.Time + LiveWorldProtocol.StaleAfterMilliseconds + 1);
        Check(mesh.PreviewTransform is null && marker.PreviewPose is null,
            "A fresh section extended an older row beyond its TTL.");

        var revocations = new[]
        {
            Snapshot([original with { Generation = original.Generation + 1, TransformAvailable = false, Time = 0 }]),
            Snapshot([original with { Handle = original.Handle + 1, TransformAvailable = false, Time = 0 }]),
            Snapshot([original with { EntryIndex = 9, Generation = original.Generation + 1, TransformAvailable = false, Time = 0 }]) with { Complete = false },
            Snapshot([original with { Handle = original.Handle ^ 0x2000, EntryIndex = 9,
                Generation = original.Generation + 1, Dynamic = true, TransformAvailable = false, Time = 0 }]) with { Complete = false },
            Snapshot([original, original with { Handle = original.Handle ^ 0x2000, EntryIndex = 9,
                Generation = original.Generation + 1, Dynamic = true }]) with { Complete = false },
            Snapshot([original, original with { Handle = 3 }]),
            Snapshot([original, original with { EntryIndex = 9, Generation = 9 }]) with { Complete = false },
            Snapshot([]), Snapshot([]) with { Available = false }, Snapshot([]) with { Time = 1 }
        };
        foreach (var revoked in revocations)
        {
            binding.InvalidatePreview(); binding.Apply(good, viewport, Now);
            binding.Apply(revoked, viewport, Now);
            Check(mesh.PreviewTransform is null && marker.PreviewPose is null,
                "A lifetime change, ambiguity, disappearance or invalid section retained an old pose.");
            binding.Apply(Snapshot([]) with { Complete = false }, viewport, Now);
            Check(mesh.PreviewTransform is null, "A partial scan resurrected a revoked pose.");
        }
        binding.Apply(good, viewport, Now);
        binding.InvalidatePreview(); viewport.ClearLiveTransforms();
        binding.Apply(Snapshot([]) with { Complete = false }, viewport, Now);
        Check(mesh.PreviewTransform is null, "Explicit preview invalidation retained a cached pose.");
        Console.WriteLine("live_pose_cache failed_read=held partial=held recovery=updated ttl=bounded lifetime=revalidated gpu=checked");
        cases++;
    }
}
