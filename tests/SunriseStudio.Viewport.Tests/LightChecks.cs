using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Local lights: the garden map's stage-4 lights add light near them, on hardware and WARP alike. */
internal static class LightChecks
{
    private const uint MapTag = 0x80EDC19B;
    // A point light by the garden spawn: row 53 of table 80EDC460, radius 6.
    private static readonly Vector3 LampPosition = new(-138.31058f, -1428.6404f, -3.8873444f);
    private const int Width = 400, Height = 250;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var world = new WorldLoader(catalog).LoadMap(MapTag, new() { IncludeLowerLods = false });
        var rows = world.Scene.LightTables.SelectMany(t => t.Table.Rows.Where(r => r.AuthoredStages.Contains(4))).ToArray();
        Check(rows.Length > 100, $"The map has only {rows.Length} stage-4 light rows.");
        var hardware = Scene(catalog, world, false);
        var warp = Scene(catalog, world, true);
        Check(MathF.Abs(hardware - warp) <= MathF.Max(hardware, warp) * 0.05f,
            $"Hardware adds {hardware} and WARP {warp} of light.");
        Console.WriteLine($"light_devices hardware={hardware:F1} warp={warp:F1}");
    }

    /**
     * Renders the lamp's corner with lights on and off. Lights only add, the sky stays unlit, the global pass
     * still matches its CPU program, and off restores the global pass.
     * @return The summed diffuse the lights added over the sampled points.
     */
    private static float Scene(PackageCatalog catalog, WorldDocument world, bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(world.Scene);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world.Scene, textureBudgetBytes: long.MaxValue));
        viewport.Visibility.Filter = item => item is MeshInstance;
        var native = viewport.NativeRenderer ?? throw new InvalidOperationException(viewport.NativeError);
        native.SkyLightingEnabled = true;
        Check(native.LocalLightCount > 100, $"The plan holds only {native.LocalLightCount} lights.");
        viewport.Camera.LookFrom(LampPosition + new Vector3(6, 4, 3), LampPosition + new Vector3(0, 0, -1));
        var points = Enumerable.Range(0, 25).SelectMany(y => Enumerable.Range(0, 40).Select(x => (x * 10 + 5, y * 10 + 5))).ToArray();
        native.LocalLightsEnabled = false;
        viewport.RenderImage(Width, Height, 2);
        Check(native.LightingError is null, native.LightingError ?? "");
        var without = native.SamplePixels(points);
        native.LocalLightsEnabled = true;
        viewport.RenderImage(Width, Height, 2);
        Check(native.LocalLightsActive && native.LocalLightError is null, native.LocalLightError ?? "Local lights did not run.");
        Check(native.LocalLightDrawCount > 0, "No local light drew.");
        int drawn = native.LocalLightDrawCount;
        var skips = native.LocalLightSkips;
        // The global pass is validated against its copy taken before the lights.
        var check = native.ValidateLighting();
        var with = native.SamplePixels(points);
        var depth = renderer.ReadNativeDepthPixels(native.Targets, points);
        float added = 0; int brighter = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var delta = with[i].Diffuse - without[i].Diffuse;
            Check(float.IsFinite(delta.X) && float.IsFinite(delta.Y) && float.IsFinite(delta.Z), $"A light wrote a non-finite value at {points[i]}.");
            // R11G11B10 rounds each channel; a light never takes energy away beyond that.
            Check(delta.X >= -0.01f * MathF.Abs(without[i].Diffuse.X) - 1e-4f && delta.Y >= -0.01f * MathF.Abs(without[i].Diffuse.Y) - 1e-4f,
                $"A light darkened {points[i]}: {without[i].Diffuse} to {with[i].Diffuse}.");
            if (depth[i].X >= 1) Check(delta == Vector4.Zero, $"A light lit the sky at {points[i]}.");
            if (delta.X + delta.Y + delta.Z > 1e-3f) brighter++;
            added += MathF.Max(0, delta.X + delta.Y + delta.Z);
        }
        Check(brighter > points.Length / 20, $"Lights brightened only {brighter} of {points.Length} points.");
        native.LocalLightsEnabled = false;
        viewport.RenderImage(Width, Height, 2);
        var again = native.SamplePixels(points);
        Check(again.Zip(without).All(pair => pair.First.Diffuse == pair.Second.Diffuse), "Lights off did not restore the global pass.");
        string refused = string.Join(", ", native.RefusedLightMaterials.GroupBy(pair => pair.Value)
            .Select(g => $"{g.Count()}x [{g.Key}] e.g. {string.Join(" ", g.Take(2).Select(pair => pair.Key.ToString("X8")))}").Take(4));
        Console.WriteLine($"light_scene device={(warp ? "warp" : "hardware")} lights={native.LocalLightCount} drawn={drawn}"
            + $" culled={skips.Culled} faded={skips.Faded} disabled={skips.Disabled} unevaluated_programs={native.UnevaluatedLightPrograms}"
            + $" brighter={brighter}/{points.Length} added={added:F1} lit={check.LitPixels} refused={native.RefusedLightMaterials.Count} {refused}");
        return added;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
