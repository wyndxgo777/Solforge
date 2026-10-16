using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Timing and image comparison of placement edits, reporting phase times and renderer noise. */
internal static partial class Program
{
    /**
     * Moves one drawn static instance in place, then loads the same bytes again, and times both.
     * Both renderers must upload the same CB11 vectors; images are saved and compared for the log only.
     */
    private static void PlacementTiming(PackageCatalog catalog, WorldLoader loader, WorldDocument world, uint map, uint mission,
        Dx11Renderer renderer, AssetViewport viewport, NativeScenePlan plan, string stem)
    {
        var instance = plan.Groups.Where(group => group.Kind == NativeMeshKind.Static).SelectMany(group => group.Parts)
            .Select(part => part.Instance).First(item => item.InstanceSourceTag != 0 && item.NativeInstanceData.Length == 48);
        uint table = instance.InstanceSourceTag;
        if (!MemoryMarshal.TryGetArray(instance.NativeInstanceData, out var row)) throw new InvalidOperationException("Instance row has no array.");
        byte[] edited = catalog.Read(table);
        // Translation X of the row's QTS.
        int x = row.Offset + 16;
        BitConverter.GetBytes(BitConverter.ToSingle(edited, x) + 2).CopyTo(edited, x);
        catalog.ReadOverride = tag => tag == table ? edited.ToArray() : null;
        try
        {
            var bounds = viewport.Selection.GetBounds(instance);
            viewport.FrameArea(bounds.Center + new Vector3(2, 0, 0), MathF.Max(4, bounds.Radius * 2));
            var timings = new List<(string Phase, double Milliseconds)>();
            T Measure<T>(string phase, Func<T> operation)
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = operation();
                timings.Add((phase, System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds));
                return result;
            }
            var update = Measure("update_scene", () => WorldPlacementUpdate.Prepare(catalog, world, [table], out string reason)
                ?? throw new InvalidOperationException(reason));
            Measure("apply_scene", () => { update.Apply(); return true; });
            Measure("update_plan", () => plan.UpdatePlacements(update.Instances, out string reason) ? true : throw new InvalidOperationException(reason));
            Measure("update_viewport", () => { viewport.UpdatePlacements(update.Instances, update.Markers); return true; });
            var moved = Measure("update_first_frame", () => viewport.RenderImage(800, 500, 2));
            var samples = Enumerable.Range(0, 50).SelectMany(sy => Enumerable.Range(0, 80).Select(sx => (sx * 10, sy * 10))).ToArray();
            // Adaptive exposure differs between a long-lived renderer and a new one, so both measure at once.
            Vector4[] Steady()
            {
                viewport.NativeRenderer!.AutoExposure = false;
                viewport.NativeRenderer.RequestExposureWarmup();
                return renderer.ReadTexturePixels(viewport.RenderImage(800, 500, 2), samples);
            }
            var inPlace = Steady();
            var again = Steady();
            int noise = inPlace.Where((pixel, i) => Vector4.Distance(pixel, again[i]) > 1e-4f).Count();
            SaveImage(renderer, moved, stem + "-moved-in-place.png");
            var inPlaceConstants = HandleConstants(viewport.NativeRenderer!, world.Scene);
            double incremental = timings.Sum(item => item.Milliseconds);

            var reloaded = Measure("reload_world_decode", () => loader.LoadMission(map, mission, new() { IncludeLowerLods = false }));
            var reloadedPlan = Measure("reload_native_plan", () => AssetViewport.PrepareNativeScene(catalog, reloaded.Scene, textureBudgetBytes: long.MaxValue));
            var camera = (viewport.Camera.Position, viewport.Camera.Pivot);
            Measure("reload_geometry_adoption", () => { viewport.SetScene(reloaded.Scene); return true; });
            Measure("reload_native_adoption", () => { viewport.SetNativeScene(reloadedPlan); return true; });
            viewport.Camera.LookFrom(camera.Position, camera.Pivot);
            var full = Measure("reload_first_frame", () => viewport.RenderImage(800, 500, 2));
            SaveImage(renderer, full, stem + "-moved-reloaded.png");
            var fromReload = Steady();
            double reload = timings.Where(item => item.Phase.StartsWith("reload_", StringComparison.Ordinal)).Sum(item => item.Milliseconds);
            var differences = inPlace.Select((pixel, i) => Vector4.Distance(pixel, fromReload[i])).ToArray();
            int different = differences.Count(value => value > 1e-4f);
            var reloadedConstants = HandleConstants(viewport.NativeRenderer!, reloaded.Scene);
            int handlesDifferent = inPlaceConstants.Count == reloadedConstants.Count
                ? inPlaceConstants.Where((line, i) => line != reloadedConstants[i]).Count() : -1;
            foreach (var (phase, milliseconds) in timings)
                Console.WriteLine($"placement_timing phase={phase} ms={milliseconds:F1}");
            Console.WriteLine($"placement_timing instance_table={table:X8} moved_instances={update.Instances.Count} in_place_ms={incremental:F1} "
                + $"reload_ms={reload:F0} handles={reloadedConstants.Count} different_handles={handlesDifferent} image_samples={samples.Length} "
                + $"image_different={different} image_largest={differences.Max():G3} image_same_renderer_noise={noise}");
            Require(handlesDifferent == 0, "The in-place renderer uploads different CB11 vectors than a reloaded one.");
        }
        finally { catalog.ReadOverride = null; }
    }

    /** Each draw handle's instance index and CB11 vector bits, in handle order. */
    private static List<string> HandleConstants(NativeSceneRenderer native, SceneDocument scene)
    {
        var index = new Dictionary<MeshInstance, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < scene.Instances.Count; i++) index[scene.Instances[i]] = i;
        return native.PartHandles().Select(part => $"{index.GetValueOrDefault(part.Instance, -1)}:" + string.Join(",",
            native.HandleConstants(part.Handle).ToArray().Select(v =>
                $"{BitConverter.SingleToUInt32Bits(v.X):X8}{BitConverter.SingleToUInt32Bits(v.Y):X8}"
                + $"{BitConverter.SingleToUInt32Bits(v.Z):X8}{BitConverter.SingleToUInt32Bits(v.W):X8}"))).ToList();
    }
}
