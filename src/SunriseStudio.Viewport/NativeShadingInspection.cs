using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;

namespace SunriseStudio.Viewport;

/** Minimum, median, 95th percentile and maximum of a sampled value. */
public sealed record NativeShadingRange(float Minimum, float Median, float P95, float Maximum);
/** Shading values read at one pixel. */
public sealed record NativeShadingProbe(int X, int Y, float Depth, Vector4 Final, Vector4 Albedo,
    Vector4 Third, Vector4 Diffuse, Vector4 Scene, string Group, uint? MeshTag, uint? MaterialTag,
    int? Part, int? Stage, string? Name, Vector4? Ibl = null, string? Layer = null);
/** Shading statistics over a captured frame. */
public sealed record NativeShadingReport(int Width, int Height, int Samples, int GeometrySamples,
    int FinalBlack, int FinalClipped, int AlbedoBlack, int DiffuseBlack, int SceneBlack,
    NativeShadingRange FinalRange, NativeShadingRange AlbedoRange, NativeShadingRange DiffuseRange,
    NativeShadingRange SceneRange, float ExposureStops, float BlackFraction, bool Atmosphere,
    bool Deferred, int AtmosphereIndex, string? AtmosphereError, string? LightingError,
    string? DeferredError, NativeSceneFrameStats Frame, IReadOnlyList<NativeShadingProbe> Probes,
    string ReportPath, bool SkyLighting, string? SkyLightingError, NativeShadingRange? IblRange, int WhiteClipped,
    int SkySamples, int SkyBlack, uint AtmosphereBubble, uint BackdropBubble, Vector3 Eye, Vector3 Pivot, string LightingMode,
    int LocalCubemaps, int LocalCubemapDraws, string? LocalCubemapError)
{
    /** Technique 37 ran, its cascades ran, and the share of geometry samples the mask darkened below one half. */
    public bool Sun { get; init; }
    public bool Shadows { get; init; }
    public string? ShadowError { get; init; }
    public int ShadowedSamples { get; init; }
}

/** Readbacks compare pipeline surfaces without replacing any material or shader. */
public static class NativeShadingInspection
{
    /** CPU ray hits are candidates; alpha-tested surfaces can differ from the visible GPU surface. */
    public static NativeShadingReport Capture(Dx11Renderer renderer, AssetViewport viewport,
        GpuTexture image, string outputPrefix)
    {
        var native = viewport.NativeRenderer ?? throw new InvalidOperationException("Native scene is not loaded.");
        const int columns = 80, rows = 50;
        var points = Enumerable.Range(0, rows).SelectMany(y => Enumerable.Range(0, columns)
            .Select(x => (X: Math.Min(image.Width - 1, (int)((x + .5f) * image.Width / columns)),
                Y: Math.Min(image.Height - 1, (int)((y + .5f) * image.Height / rows))))).ToArray();
        var final = renderer.ReadTexturePixels(image, points);
        var depth = renderer.ReadNativeDepthPixels(native.Targets, points);
        var data = native.SamplePixels(points);
        var ibl = native.IblLighting is { } reflections ? renderer.ReadTexturePixels(reflections, points) : null;
        // The mask is half resolution; each point reads the texel that covers it.
        var mask = native.ShadowMaskTexture is { } shadowMask ? renderer.ReadTexturePixels(shadowMask, points.Select(p =>
            (Math.Min(shadowMask.Width - 1, p.X * shadowMask.Width / image.Width),
                Math.Min(shadowMask.Height - 1, p.Y * shadowMask.Height / image.Height))).ToArray()) : null;
        var geometry = Enumerable.Range(0, points.Length).Where(i => depth[i].X >= 0 && depth[i].X < 1 - 1e-7f).ToArray();
        var black = geometry.Where(i => Maximum(final[i]) <= .02f).ToArray();
        var bright = geometry.Where(i => Maximum(final[i]) >= .98f).ToArray();
        List<NativeShadingProbe> probes = [];
        var visibility = new SceneVisibility { Filter = item => item is MeshInstance mesh && viewport.IsVisible(mesh) };
        foreach (var (name, indices) in new[] { ("Black", black), ("Clipped", bright), ("Midtone", geometry.Except(black).Except(bright).ToArray()) })
        {
            var selected = indices.Length <= 16 ? indices : Enumerable.Range(0, 16).Select(i => indices[i * indices.Length / 16]).ToArray();
            foreach (int index in selected)
            {
                var point = points[index];
                var mesh = viewport.Selection.Pick(viewport.Camera, new(point.X, point.Y), new(image.Width, image.Height), visibility) as MeshInstance;
                probes.Add(new(point.X, point.Y, depth[index].X, final[index], data[index].Albedo,
                    data[index].Third, data[index].Diffuse, data[index].Scene, name,
                    mesh?.Mesh.TagHash, mesh?.Mesh.MaterialTag, mesh?.Mesh.PartIndex,
                    mesh?.Mesh.Native?.RenderStage, mesh?.Name, ibl?[index], mesh?.Layer.ToString()));
            }
        }
        string full = Path.GetFullPath(outputPrefix), directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        string reportPath = Path.Combine(logs, Path.GetFileName(full) + ".json");
        var report = new NativeShadingReport(image.Width, image.Height, points.Length, geometry.Length,
            black.Length, bright.Length, geometry.Count(i => Maximum(data[i].Albedo) <= .01f),
            geometry.Count(i => Maximum(data[i].Diffuse) <= 1e-7f), geometry.Count(i => Maximum(data[i].Scene) <= 1e-7f),
            Range(geometry.Select(i => Luminance(final[i]))), Range(geometry.Select(i => Luminance(data[i].Albedo))),
            Range(geometry.Select(i => Luminance(data[i].Diffuse))), Range(geometry.Select(i => Luminance(data[i].Scene))),
            native.AppliedViewerStops, native.BlackFraction, native.AtmosphereActive, native.DeferredActive,
            native.AtmosphereIndex, native.AtmosphereError, native.LightingError, native.DeferredError,
            native.LastFrame, probes.AsReadOnly(), reportPath, native.SkyLightingActive,
            native.SkyLightingError, ibl is null ? null : Range(geometry.Select(i => Luminance(ibl[i]))),
            geometry.Count(i => Math.Min(final[i].X, Math.Min(final[i].Y, final[i].Z)) >= .98f),
            points.Length - geometry.Length, Enumerable.Range(0, points.Length).Count(i => depth[i].X >= 1 - 1e-7f && Maximum(final[i]) <= .02f),
            (uint)native.AtmosphereIndex < (uint)native.Plan.Atmospheres.Count ? native.Plan.Atmospheres[native.AtmosphereIndex].BubbleHash : 0,
            viewport.Backdrop.ActiveBubbleHash, viewport.Camera.Position, viewport.Camera.Pivot, native.LightingMode.ToString(),
            native.LocalCubemapCount, native.LocalCubemapDrawCount, native.LocalCubemapError)
        {
            Sun = native.SunActive, Shadows = native.ShadowsActive, ShadowError = native.ShadowError,
            ShadowedSamples = mask is null ? 0 : geometry.Count(i => mask[i].X < 0.5f)
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        { WriteIndented = true, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Save(renderer, image, full + "-final.png");
        foreach (var (output, name, srgb, exposure) in new[]
        { (0, "albedo", false, 0f), (1, "normal", false, 0f), (2, "third", false, 0f),
            (3, "diffuse", true, 0f), (6, "scene", true, native.AppliedViewerStops) })
            Save(renderer, native.DisplayTexture(output, srgb: srgb, exposure: exposure), full + "-" + name + ".png");
        if (native.ShadowMaskTexture is { } shadowImage) Save(renderer, shadowImage, full + "-shadowmask.png");
        return report;
    }

    /** Captures store displayed pixels; HDR ranges remain in the numeric report. */
    private static unsafe void Save(Dx11Renderer renderer, GpuTexture texture, string path)
    {
        var points = Enumerable.Range(0, texture.Height).SelectMany(y => Enumerable.Range(0, texture.Width)
            .Select(x => (X: x, Y: y))).ToArray();
        var pixels = renderer.ReadTexturePixels(texture, points);
        using var bitmap = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new(0, 0, texture.Width, texture.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < texture.Height; y++)
                for (int x = 0; x < texture.Width; x++)
                {
                    var pixel = pixels[y * texture.Width + x];
                    byte* target = (byte*)data.Scan0 + y * data.Stride + x * 4;
                    target[0] = Byte(pixel.Z); target[1] = Byte(pixel.Y); target[2] = Byte(pixel.X); target[3] = 255;
                }
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static byte Byte(float value) => float.IsFinite(value) ? (byte)Math.Clamp(MathF.Round(value * 255), 0, 255) : (byte)255;
    private static float Maximum(Vector4 value) => Math.Max(value.X, Math.Max(value.Y, value.Z));
    private static float Luminance(Vector4 value) => value.X * .2126f + value.Y * .7152f + value.Z * .0722f;
    private static NativeShadingRange Range(IEnumerable<float> source)
    {
        float[] values = source.Where(float.IsFinite).Order().ToArray();
        return values.Length == 0 ? new(0, 0, 0, 0)
            : new(values[0], values[values.Length / 2], values[(int)((values.Length - 1) * .95f)], values[^1]);
    }
}
