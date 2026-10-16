using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using Vortice.Direct3D;

namespace SunriseStudio.Interchange;

/** Placed lights are measured by running their own light shader on the CPU, then fitted to Blender lights. */
public static partial class WorldSceneExport
{
    public const string LightsName = "world.lights.json", LightsFormat = "sunrise-world-lights";
    public const int LightsVersion = 1;
    // Sample grid: a coarse sphere finds the brightest direction, rings around it give the cone profile.
    private const int SphereDirections = 512, ProfileRings = 61, RingDirections = 8, RaySteps = 24;
    private const float RingDegrees = 3;
    // A light sample below this fraction of the light's brightest sample counts as unlit.
    private const float LitFraction = 1e-4f;
    // A fit part below this share of the light is noise; it would light the whole sphere, or nothing.
    private const float MinorShare = 0.05f;
    // Blender radius and cut-off candidates, as fractions of the lit range.
    private static readonly float[] RadiusFractions = [0, 0.01f, 0.02f, 0.04f, 0.06f, 0.08f, 0.1f, 0.12f, 0.15f, 0.18f,
        0.21f, 0.25f, 0.29f, 0.33f, 0.37f, 0.42f, 0.48f, 0.55f, 0.65f, 0.8f];
    private const float CutFirst = 0.5f, CutLast = 1.5f, CutStep = 0.025f;
    // A falloff sample's relative error counts at least down to this fraction of the brightest sample.
    private const float ProfileFloor = 0.01f;
    // A line becomes Blender points at most this far apart, as a fraction of its lit range.
    private const float LineSpacing = 0.5f;
    private const int MaxLinePoints = 16, LinePhases = 8;
    // A negative back spot is kept only when it cuts the cone fit's error below this share.
    private const float BackGain = 0.8f;
    // The normal buffer stores smoothness in the normal length; this length decodes to smoothness 0.5.
    private const float NormalLength = 0.875f;
    // Seconds at which an animated light is sampled; its intensity is their mean.
    private static readonly double[] AnimationSeconds = [0, 0.5, 1.25, 2.75, 4.5, 7, 9.5, 13];
    private static readonly Vector3 Luminance = new(0.2126f, 0.7152f, 0.0722f);

    /** Writes the lights file and adds its notices; the manifest does not list it. */
    private static void WriteLights(PackageCatalog catalog, SceneDocument scene, string root, List<BundleNotice> notices,
        Dictionary<string, double> phases, CancellationToken token, Action<string>? progress)
    {
        var clock = Stopwatch.StartNew();
        var set = MeasureLights(catalog, scene, token, progress, root);
        notices.AddRange(set.Notices);
        WriteJson(Path.Combine(root, LightsName), set);
        phases["lights"] = clock.Elapsed.TotalSeconds;
    }

    /**
     * Fits every stage-4 light row, the time-of-day sun and the sky of the scene.
     * @param root Folder that receives the sky image; null describes the sky without writing it.
     * @return Lights that cannot be measured are left out and counted in a notice.
     */
    public static WorldLightSet MeasureLights(PackageCatalog catalog, SceneDocument scene, CancellationToken token = default,
        Action<string>? progress = null, string? root = null)
    {
        var context = new LightContext(catalog);
        var notices = new List<BundleNotice>();
        // Identical rows at one place cannot be told apart; each place exports once and counts its copies.
        var groups = scene.LightTables.SelectMany(table => table.Table.Rows.Where(row => row.AuthoredStages.Contains(4))
            .Select(row => (Table: table, Row: row))).GroupBy(item => RowKey(item.Table, item.Row), StringComparer.Ordinal).ToArray();
        var rows = groups.Select(group => group.First()).ToArray();
        var copies = groups.Select(group => group.Count()).ToArray();
        int volumetric = scene.LightTables.Sum(table => table.Table.Rows.Count(row => !row.AuthoredStages.Contains(4)));
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        var prepared = new LightMaterial?[rows.Length];
        for (int index = 0; index < rows.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            try { prepared[index] = context.Prepare(rows[index].Row.MaterialTag); }
            catch (Exception error) when (Recoverable(error)) { Fail(error.Message); }
        }
        var lights = new WorldLight?[rows.Length];
        var reasons = new string?[rows.Length];
        int done = 0;
        Parallel.For(0, rows.Length, new ParallelOptions { CancellationToken = token }, index =>
        {
            if (prepared[index] is not { } material) return;
            try
            {
                // The game draws every copy; LightTable_DrawRows (0x7FF742E757A0) gates rows only by visibility.
                var light = Measure(context, material, rows[index].Table, rows[index].Row);
                lights[index] = light with
                {
                    Intensity = light.Intensity * copies[index], Omni = light.Omni * copies[index], Back = light.Back * copies[index],
                    Copies = copies[index]
                };
            }
            catch (Exception error) when (Recoverable(error) || error is InvalidOperationException) { reasons[index] = error.Message; }
            int count = Interlocked.Increment(ref done);
            if (count % 16 == 0) progress?.Invoke($"Measuring lights {count}/{rows.Length}");
        });
        foreach (string? reason in reasons) if (reason is not null) Fail(reason);
        var result = lights.OfType<WorldLight>().ToList();
        int dark = result.RemoveAll(light => light.Intensity <= 0);
        result = result.Select((light, id) => light with { Id = id }).ToList();
        foreach (var (reason, count) in failures)
            notices.Add(new("Lights", "Not exported", $"{count} lights: {reason}"));
        if (dark != 0) notices.Add(new("Lights", "Not exported", $"{dark} lights add no light at the sampled points."));
        if (volumetric != 0) notices.Add(new("Lights", "Not exported", $"{volumetric} volumetric fog and probe light rows."));
        if (copies.Sum() - copies.Length is > 0 and var merged)
            notices.Add(new("Lights", "Merged", $"{merged} light rows repeat another row at the same place; the game draws"
                + " every copy, so each place exports once with the summed intensity."));
        notices.Add(new("Lights", "Approximate", "Point, spot and line fits of the game's light shaders; light textures"
            + " and clip shapes are averaged into the cone, and light that ignores the surface normal is folded into"
            + " the direct light. The game's local lights cast no shadows."));
        WorldSun? sun = null;
        WorldSky? sky = null;
        var source = NativeScenePlan.SkySource(scene);
        (Vector3 Sky, Vector3 Light)? directions = null;
        try
        {
            if (source.Time is { } time)
            { directions = Directions(catalog, time, source.StartTime); sun = Sun(context, time, directions.Value, source.StartTime); }
        }
        catch (Exception error) when (Recoverable(error)) { notices.Add(new("Lights", "Sun not exported", error.Message)); }
        try
        {
            if (source.Atmosphere is { } atmosphere)
            {
                progress?.Invoke("Measuring the sky");
                // Without time-of-day curves the game's light direction stays +Z.
                sky = WriteSky(catalog, context, atmosphere, directions?.Light ?? Vector3.UnitZ, source.StartTime, root);
            }
            if (sky is null) notices.Add(new("Lights", "Sky not exported", "The map places no atmosphere with a sky lookup for the time of day."));
        }
        catch (Exception error) when (Recoverable(error)) { notices.Add(new("Lights", "Sky not exported", error.Message)); }
        if (sky is not null)
            notices.Add(new("Lights", "Approximate", $"The sky is the atmosphere's at the map's start time, {sky.TimeOfDay:0.###}."
                + " Blender shades occlusion itself instead of the game's baked occlusion; the local cubemaps' colours,"
                + " the atmosphere fog and the sky light in reflections are not exported."));
        var volumes = scene.Cubemaps.DistinctBy(item => (item.PlacementSourceTag, item.AuxiliaryOffset))
            .Select(item => (Item: item, Box: item.Cubemap.SourceMatrixF0, item.Cubemap.Extents))
            .Where(item => item.Extents is { X: > 0, Y: > 0, Z: > 0 } && float.IsFinite(item.Extents.Length()) && Matrix4x4.Invert(item.Box, out _))
            .Select(item => new WorldLightVolume(BundleSceneWriter.MatrixValues(item.Box), [item.Extents.X, item.Extents.Y, item.Extents.Z],
                item.Item.BubbleHash, item.Item.PlacementSourceTag)).ToArray();
        return new(LightsFormat, LightsVersion, scene.Name, "Game lighting units before exposure; a point or spot lights"
            + " E = I / (d^2 + Radius^2) * (1 - (d / Range)^4)^2, as Blender's soft falloff and custom distance do", result, sun, notices)
        { Sky = sky, Volumes = volumes };

        void Fail(string reason) => failures[reason] = failures.GetValueOrDefault(reason) + 1;
    }

    private static string RowKey(PlacedLightTable table, AuthoredLightRow row)
    {
        // q and -q are one rotation; adding zero turns a negative zero lane into zero.
        var q = row.Rotation.W < 0 ? -row.Rotation : row.Rotation;
        return $"{table.BubbleHash:X8}|{Convert.ToHexString(row.SourceData.Span)}|{row.Translation}|{q.X + 0f}|{q.Y + 0f}|{q.Z + 0f}|{q.W + 0f}|{row.Scale}";
    }

    /** One light's fit; a dark light has zero intensity. */
    private static WorldLight Measure(LightContext context, LightMaterial material, PlacedLightTable table, AuthoredLightRow row)
    {
        var origin = row.Translation;
        var probe = new LightProbe(context, material, row, 0);
        if (Line(probe, row) is { } line) return MeasureLine(context, material, table, row, probe, line.Axis, line.Half);
        // The game draws a light only inside its volume cube; scout passes shrink the cube's reach to the lit distance.
        float reach = 0;
        foreach (var corner in CubeCorners) reach = MathF.Max(reach, Vector3.Distance(Projected(corner, row.VolumeWorldTransform), origin));
        if (!(reach > 0) || !float.IsFinite(reach)) throw new InvalidDataException("The light volume is empty.");
        var sphere = Fibonacci(SphereDirections);
        for (int pass = 0; pass < 3; pass++)
        {
            float lit = Reach(probe, sphere, reach);
            if (lit <= 0) return Dark(material, table, row);
            bool shrunk = lit < reach / 2;
            reach = lit;
            if (!shrunk) break;
        }
        float step = reach / RaySteps;
        float brightest = 0, range = 0;
        var flux = new Vector3[sphere.Length];
        var samples = new Vector3[sphere.Length, RaySteps];
        for (int j = 0; j < sphere.Length; j++)
            for (int k = 0; k < RaySteps; k++)
            {
                var value = probe.Irradiance(sphere[j] * ((k + 0.5f) * step));
                samples[j, k] = value;
                brightest = MathF.Max(brightest, Vector3.Dot(value, Luminance));
            }
        if (brightest <= 0) return Dark(material, table, row);
        for (int j = 0; j < sphere.Length; j++)
            for (int k = 0; k < RaySteps; k++)
            {
                float d = (k + 0.5f) * step;
                flux[j] += samples[j, k] * d * d * step;
                if (Vector3.Dot(samples[j, k], Luminance) > brightest * LitFraction) range = MathF.Max(range, d + step / 2);
            }
        var axis = Vector3.Zero;
        for (int j = 0; j < sphere.Length; j++) axis += sphere[j] * Vector3.Dot(flux[j], Luminance);
        axis = axis.Length() > 1e-12f ? Vector3.Normalize(axis)
            : sphere[Enumerable.Range(0, sphere.Length).MaxBy(j => Vector3.Dot(flux[j], Luminance))];
        // Rings of directions around the axis give the cone profile at three-degree steps.
        var (u, v) = Basis(axis);
        var rings = new Vector3[ProfileRings, RaySteps];
        for (int i = 0; i < ProfileRings; i++)
        {
            float theta = i * RingDegrees * MathF.PI / 180;
            int around = i == 0 || i == ProfileRings - 1 ? 1 : RingDirections;
            for (int a = 0; a < around; a++)
            {
                float phi = a * 2 * MathF.PI / around;
                var direction = Vector3.Normalize(axis * MathF.Cos(theta) + (u * MathF.Cos(phi) + v * MathF.Sin(phi)) * MathF.Sin(theta));
                for (int k = 0; k < RaySteps; k++)
                {
                    var at = direction * ((k + 0.5f) * step);
                    rings[i, k] += Effective(probe.Irradiance(at), probe.Irradiance(at, side: true)) / around;
                }
            }
        }
        var solid = Enumerable.Range(0, ProfileRings).Select(RingSolidAngle).ToArray();
        // All rings share one Blender radius and cut-off, so the shape is fitted to the flux-weighted sum of the rings.
        var shape = new float[RaySteps];
        for (int i = 0; i < ProfileRings; i++)
            for (int k = 0; k < RaySteps; k++) shape[k] += solid[i] * Vector3.Dot(rings[i, k], Luminance);
        var falloff = FitFalloff(shape, step, range, (d, radius, cut) => Falloff(d, radius, cut));
        var profile = new Vector3[ProfileRings];
        for (int i = 0; i < ProfileRings; i++)
            for (int k = 0; k < RaySteps; k++) profile[i] += rings[i, k] * falloff.Weights[k];
        var luminance = profile.Select(value => Vector3.Dot(value, Luminance)).ToArray();
        var total = Vector3.Zero;
        for (int i = 0; i < ProfileRings; i++) total += profile[i] * solid[i];
        var fit = FitCone(luminance, solid, allowBack: true);
        float animation = Animation(context, material, row, axis * (range / 3));
        float maximum = MathF.Max(total.X, MathF.Max(total.Y, total.Z));
        var color = total / maximum;
        float intensity = fit.Scale * animation / Vector3.Dot(color, Luminance), omniPart = fit.Omni * animation / Vector3.Dot(color, Luminance);
        var direction3 = fit.Spot ? axis : Vector3.Transform(-Vector3.UnitZ, row.Rotation);
        return new(0, fit.Spot ? "Spot" : "Point", table.Table.TagHash, row.Index, row.MaterialTag, material.PixelShader,
            table.BubbleHash, SceneLayer.Lights.ToString(), [origin.X, origin.Y, origin.Z], [direction3.X, direction3.Y, direction3.Z],
            [color.X, color.Y, color.Z], intensity, falloff.Cut, falloff.Radius, fit.Spot ? fit.Angle * 2 : 0, fit.Spot ? fit.Blend : 0,
            0, 1, false, Enabled(context, row.SkipParameters), animation != 1, fit.Description + Grey(probe))
        {
            Omni = omniPart, Back = fit.Back * animation / Vector3.Dot(color, Luminance), BackAngle = fit.Back > 0 ? fit.BackAngle * 2 : 0,
            BackBlend = fit.Back > 0 ? fit.BackBlend : 0
        };
    }

    /** Blender's soft falloff for a facing surface: 1 / (d^2 + r^2), times the custom distance window. */
    private static float Falloff(float d, float radius, float cut)
    {
        if (d >= cut) return 0;
        float x = d / cut, window = 1 - x * x * x * x;
        return window * window / (d * d + radius * radius);
    }

    /**
     * The Blender radius and cut-off whose falloff best matches a sampled game profile. Each sample weighs the area of
     * its shell over its own value, so the dim edge of the light counts by its relative error.
     * @param profile Game irradiance at the centre of each ray step.
     * @param model Blender's irradiance for unit intensity at a distance, for a radius and cut-off.
     * @return Weights[k] times a profile gives the fitted intensity of that profile.
     */
    private static FalloffFit FitFalloff(float[] profile, float step, float range, Func<float, float, float, float> model)
    {
        var best = new FalloffFit(0, range, new float[profile.Length]);
        float floor = profile.Max() * ProfileFloor;
        var area = new double[profile.Length];
        for (int k = 0; k < profile.Length; k++) { float d = (k + 0.5f) * step; area[k] = d * d / Math.Max(profile[k], floor); }
        double bestError = double.MaxValue;
        var values = new float[profile.Length];
        foreach (float radiusFraction in RadiusFractions)
            for (float cutFraction = CutFirst; cutFraction <= CutLast + 1e-4f; cutFraction += CutStep)
            {
                float radius = radiusFraction * range, cut = cutFraction * range;
                double tf = 0, ff = 0, tt = 0;
                for (int k = 0; k < profile.Length; k++)
                {
                    values[k] = model((k + 0.5f) * step, radius, cut);
                    tf += area[k] * profile[k] * values[k]; ff += area[k] * values[k] * values[k]; tt += area[k] * profile[k] * profile[k];
                }
                if (!(ff > 0)) continue;
                double error = tt - tf * tf / ff;
                if (error >= bestError) continue;
                bestError = error;
                var weights = new float[profile.Length];
                for (int k = 0; k < profile.Length; k++) weights[k] = (float)(area[k] * values[k] / ff);
                best = new(radius, cut, weights);
            }
        return best;
    }

    private sealed record FalloffFit(float Radius, float Cut, float[] Weights);

    /**
     * A box volume longer along one axis than across holds a line light; the excess is its half length.
     * The shader must agree: halfway along the line, a nearby surface is lit about as much as at the middle.
     */
    private static (Vector3 Axis, float Half)? Line(LightProbe probe, AuthoredLightRow row)
    {
        var m = row.VolumeTransform;
        if (m.M14 != 0 || m.M24 != 0 || m.M34 != 0 || m.M44 != 1) return null;
        var x = new Vector3(m.M11, m.M12, m.M13);
        float across = new Vector3(m.M21, m.M22, m.M23).Length(), along = x.Length();
        if (MathF.Abs(across - new Vector3(m.M31, m.M32, m.M33).Length()) > 1e-3f * MathF.Max(across, 1) || along - across < 0.01f) return null;
        var world = Vector3.TransformNormal(x / along, row.Transform);
        float scale = world.Length();
        var axis = world / scale;
        float half = (along - across) * scale, near = across * scale / 4;
        var (u, _) = Basis(axis);
        float middle = Vector3.Dot(probe.Irradiance(u * near), Luminance);
        float off = Vector3.Dot(probe.Irradiance(axis * (half / 2) + u * near, axis * (half / 2)), Luminance);
        return middle > 0 && off >= 0.7f * middle ? (axis, half) : null;
    }

    /** A line light's intensity per metre, from the ring of directions across its middle. */
    private static WorldLight MeasureLine(LightContext context, LightMaterial material, PlacedLightTable table, AuthoredLightRow row,
        LightProbe probe, Vector3 axis, float half)
    {
        var (u, v) = Basis(axis);
        var ring = Enumerable.Range(0, SphereDirections / 4).Select(a =>
        {
            float phi = a * 2 * MathF.PI / (SphereDirections / 4);
            return u * MathF.Cos(phi) + v * MathF.Sin(phi);
        }).ToArray();
        float reach = 0;
        foreach (var corner in CubeCorners) reach = MathF.Max(reach, Vector3.Distance(Projected(corner, row.VolumeWorldTransform), row.Translation));
        for (int pass = 0; pass < 3; pass++)
        {
            float lit = Reach(probe, ring, reach);
            if (lit <= 0) return Dark(material, table, row);
            bool shrunk = lit < reach / 2;
            reach = lit;
            if (!shrunk) break;
        }
        float step = reach / RaySteps, range = 0, brightest = 0;
        var samples = new Vector3[ring.Length, RaySteps];
        for (int j = 0; j < ring.Length; j++)
            for (int k = 0; k < RaySteps; k++)
                brightest = MathF.Max(brightest, Vector3.Dot(samples[j, k] = probe.Irradiance(ring[j] * ((k + 0.5f) * step)), Luminance));
        if (brightest <= 0) return Dark(material, table, row);
        var effective = new Vector3[ring.Length, RaySteps];
        var middle = new Vector3[RaySteps];
        for (int j = 0; j < ring.Length; j++)
            for (int k = 0; k < RaySteps; k++)
            {
                float d = (k + 0.5f) * step;
                effective[j, k] = Effective(samples[j, k], probe.Irradiance(ring[j] * d, side: true));
                middle[k] += effective[j, k] / ring.Length;
                if (Vector3.Dot(samples[j, k], Luminance) > brightest * LitFraction) range = MathF.Max(range, d + step / 2);
            }
        float length = 2 * half;
        int points = Math.Clamp((int)MathF.Ceiling(length / (LineSpacing * range)), 2, MaxLinePoints);
        float spacing = length / points;
        // Blender points along the line, averaged over one spacing so sparse points fit their mean light.
        float Row(float d, float radius, float cut)
        {
            float sum = 0;
            for (int phase = 0; phase < LinePhases; phase++)
            {
                float shift = ((phase + 0.5f) / LinePhases - 0.5f) * spacing;
                for (int n = 0; n < points; n++)
                {
                    float z = ((n + 0.5f) / points - 0.5f) * length + shift, distance = MathF.Sqrt(d * d + z * z);
                    sum += d / distance * Falloff(distance, radius, cut);
                }
            }
            return sum / LinePhases;
        }
        var falloff = FitFalloff(middle.Select(value => Vector3.Dot(value, Luminance)).ToArray(), step, range, Row);
        var around = new Vector3[ring.Length];
        var linear = Vector3.Zero;
        var aim = Vector3.Zero;
        for (int j = 0; j < ring.Length; j++)
        {
            for (int k = 0; k < RaySteps; k++) around[j] += effective[j, k] * falloff.Weights[k];
            linear += around[j] / ring.Length;
            aim += ring[j] * Vector3.Dot(around[j], Luminance);
        }
        float maximum = MathF.Max(linear.X, MathF.Max(linear.Y, linear.Z));
        if (!(maximum > 0)) return Dark(material, table, row);
        var color = linear / maximum;
        // The light around the line as a cone profile about its brightest side; the ring is planar, so each angle weighs alike.
        var fit = new ConeFit(false, 0, 0, Vector3.Dot(linear, Luminance), 0, "");
        if (aim.Length() > 1e-6f * Vector3.Dot(linear, Luminance) * ring.Length)
        {
            aim = Vector3.Normalize(aim);
            var profile = new float[ProfileRings];
            for (int i = 0; i < ProfileRings; i++)
                foreach (float sign in new[] { -1f, 1f })
                    profile[i] += Vector3.Dot(AroundAt(around, ring, aim, Vector3.Cross(axis, aim), sign * i * RingDegrees), Luminance) / 2;
            fit = FitCone(profile, Enumerable.Repeat(1f, ProfileRings).ToArray());
        }
        float animation = Animation(context, material, row, u * (range / 3));
        // Intensity is per metre: each point carries length / points of it.
        float perMetre = animation * points / length / Vector3.Dot(color, Luminance);
        return new(0, "Line", table.Table.TagHash, row.Index, row.MaterialTag, material.PixelShader, table.BubbleHash,
            SceneLayer.Lights.ToString(), [row.Translation.X, row.Translation.Y, row.Translation.Z], [axis.X, axis.Y, axis.Z],
            [color.X, color.Y, color.Z], fit.Scale * perMetre, falloff.Cut, falloff.Radius, fit.Spot ? fit.Angle * 2 : 0,
            fit.Spot ? fit.Blend : 0, length, 1, false, Enabled(context, row.SkipParameters), animation != 1,
            $"line {length:0.##} m" + (fit.Spot ? ", " + fit.Description : "") + Grey(probe))
        { Points = points, Omni = fit.Omni * perMetre, Aim = fit.Spot ? [aim.X, aim.Y, aim.Z] : null };
    }

    /** The ring value at an angle from the aim, interpolated between the two nearest ring directions. */
    private static Vector3 AroundAt(Vector3[] around, Vector3[] ring, Vector3 aim, Vector3 side, float degrees)
    {
        float radians = degrees * MathF.PI / 180;
        var direction = aim * MathF.Cos(radians) + side * MathF.Sin(radians);
        // Ring direction j sits at angle 2 pi j / count from ring[0]; ring[count / 4] is a quarter turn on.
        float phi = MathF.Atan2(Vector3.Dot(direction, ring[ring.Length / 4]), Vector3.Dot(direction, ring[0]));
        float at = (phi < 0 ? phi + 2 * MathF.PI : phi) / (2 * MathF.PI) * ring.Length;
        int lower = (int)MathF.Floor(at) % ring.Length, upper = (lower + 1) % ring.Length;
        return Vector3.Lerp(around[lower], around[upper], at - MathF.Floor(at));
    }

    /**
     * A Blender light lights a surface by its cosine; many game lights also add a fill that ignores the surface normal.
     * The sum of the facing and the sideways irradiance matches the game at 60 degrees incidence.
     */
    private static Vector3 Effective(Vector3 facing, Vector3 sideways) => facing + sideways;

    private static string Grey(LightProbe probe) => probe.UsesEngineTexture ? "; engine texture read as grey" : "";

    /** The farthest lit distance along any direction, one scout step past it; zero for a dark light. */
    private static float Reach(LightProbe probe, Vector3[] directions, float limit)
    {
        const int Steps = 32;
        float step = limit / Steps, brightest = 0;
        var values = new float[directions.Length, Steps];
        for (int j = 0; j < directions.Length; j++)
            for (int k = 0; k < Steps; k++)
                brightest = MathF.Max(brightest, values[j, k] = Vector3.Dot(probe.Irradiance(directions[j] * ((k + 0.5f) * step)), Luminance));
        if (brightest <= 0) return 0;
        float farthest = 0;
        for (int j = 0; j < directions.Length; j++)
            for (int k = 0; k < Steps; k++)
                if (values[j, k] > brightest * LitFraction) farthest = MathF.Max(farthest, (k + 1.5f) * step);
        return MathF.Min(farthest, limit);
    }

    private static WorldLight Dark(LightMaterial material, PlacedLightTable table, AuthoredLightRow row) =>
        new(0, "Point", table.Table.TagHash, row.Index, row.MaterialTag, material.PixelShader, table.BubbleHash,
            SceneLayer.Lights.ToString(), [row.Translation.X, row.Translation.Y, row.Translation.Z], [0, 0, -1], [1, 1, 1],
            0, 0, 0, 0, 0, 0, 1, false, false, false, "dark");

    /**
     * A cone profile as Blender lights: an omni point, a spot along the axis, and a negative spot facing back.
     * @param Back Intensity the negative spot removes straight behind the light; at most Omni.
     */
    private readonly record struct ConeFit(bool Spot, float Angle, float Blend, float Scale, float Omni, string Description)
    {
        public float Back { get; init; }
        public float BackAngle { get; init; }
        public float BackBlend { get; init; }
    }

    /**
     * Least-squares fit of the ring profile to an omni part plus a Blender spot cone, both non-negative, and with
     * allowBack a negative spot facing back that darkens what lies behind the light.
     * @param profile Intensity luminance by ring.
     * @param solid Weight of each ring, its solid angle for a sphere of directions.
     */
    private static ConeFit FitCone(float[] profile, float[] solid, bool allowBack = false)
    {
        int count = profile.Length;
        double mean = 0, total = 0;
        for (int i = 0; i < count; i++) { total += solid[i]; mean += solid[i] * profile[i]; }
        mean /= total;
        double Error(Span<double> c, float[][] basis)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                double e = profile[i];
                for (int b = 0; b < basis.Length; b++) e -= c[b] * basis[b][i];
                sum += solid[i] * e * e;
            }
            return sum;
        }
        var ones = Enumerable.Repeat(1f, count).ToArray();
        double best = Error([mean], [ones]);
        var result = new ConeFit(false, 0, 0, (float)mean, 0, "point");
        var masks = new List<(float Half, float Blend, float[] Mask)>();
        for (float half = 2.5f; half <= 90; half += 2.5f)
            for (float blend = 0.05f; blend <= 1.0001f; blend += 0.05f) masks.Add((half, blend, ConeMask(count, half, blend)));
        Span<double> c2 = stackalloc double[2], c3 = stackalloc double[3];
        foreach (var (half, blend, mask) in masks)
        {
            if (!Solve([ones, mask], c2) || c2[0] < 0 || c2[1] <= 0) continue;
            double error = Error(c2, [ones, mask]);
            if (error < best) { best = error; result = new(true, half, blend, (float)c2[1], (float)c2[0], ""); }
        }
        if (allowBack)
        {
            var backs = masks.Where(m => m.Half % 5 == 0 && MathF.Round(m.Blend * 20) % 2 == 0)
                .Select(m => (m.Half, m.Blend, Mask: m.Mask.Reverse().Select(v => -v).ToArray())).ToArray();
            var withBack = result;
            double backBest = best;
            foreach (var (half, blend, mask) in masks)
                foreach (var back in backs)
                {
                    // The negative spot never removes more than the omni gives.
                    if (Solve([ones, mask, back.Mask], c3) && c3[0] >= 0 && c3[1] > 0 && c3[2] >= 0 && c3[2] <= c3[0])
                    {
                        double error = Error(c3, [ones, mask, back.Mask]);
                        if (error < backBest)
                        {
                            backBest = error;
                            withBack = new ConeFit(true, half, blend, (float)c3[1], (float)c3[0], "")
                            { Back = (float)c3[2], BackAngle = back.Half, BackBlend = back.Blend };
                        }
                    }
                }
            // A negative light is one more Blender light; keep it only when it clearly helps.
            if (backBest < BackGain * best && withBack.Back >= MinorShare * (withBack.Scale + withBack.Omni)) { result = withBack; best = backBest; }
        }
        if (result.Spot && result.Scale < MinorShare * (result.Scale + result.Omni)) return new(false, 0, 0, (float)mean, 0, "point");
        if (result.Spot && result.Back == 0 && result.Omni < MinorShare * (result.Scale + result.Omni)) result = result with { Omni = 0 };
        return !result.Spot ? result : result with
        {
            Description = $"spot {result.Angle * 2:0.#} deg, blend {result.Blend:0.##}"
                + (result.Omni > 0 ? $", omni {result.Omni / (result.Omni + result.Scale):0.##}" : "")
                + (result.Back > 0 ? $", back {result.BackAngle * 2:0.#} deg -{result.Back / (result.Omni + result.Scale):0.##}" : "")
        };

        // Weighted least squares through the normal equations; false when they are singular.
        bool Solve(float[][] basis, Span<double> coefficients)
        {
            int n = basis.Length;
            Span<double> a = stackalloc double[n * (n + 1)];
            for (int r = 0; r < n; r++)
            {
                for (int k = 0; k < n; k++)
                {
                    double sum = 0;
                    for (int i = 0; i < count; i++) sum += solid[i] * basis[r][i] * basis[k][i];
                    a[r * (n + 1) + k] = sum;
                }
                double rhs = 0;
                for (int i = 0; i < count; i++) rhs += solid[i] * basis[r][i] * profile[i];
                a[r * (n + 1) + n] = rhs;
            }
            for (int p = 0; p < n; p++)
            {
                int pivot = p;
                for (int r = p + 1; r < n; r++) if (Math.Abs(a[r * (n + 1) + p]) > Math.Abs(a[pivot * (n + 1) + p])) pivot = r;
                if (Math.Abs(a[pivot * (n + 1) + p]) < 1e-12) return false;
                for (int k = 0; k <= n; k++) (a[p * (n + 1) + k], a[pivot * (n + 1) + k]) = (a[pivot * (n + 1) + k], a[p * (n + 1) + k]);
                for (int r = 0; r < n; r++)
                {
                    if (r == p) continue;
                    double f = a[r * (n + 1) + p] / a[p * (n + 1) + p];
                    for (int k = p; k <= n; k++) a[r * (n + 1) + k] -= f * a[p * (n + 1) + k];
                }
            }
            for (int r = 0; r < n; r++) coefficients[r] = a[r * (n + 1) + n] / a[r * (n + 1) + r];
            return true;
        }
    }

    /** Blender's spot mask at each ring: smoothstep((cos theta - cos half) / ((1 - cos half) * blend)). */
    private static float[] ConeMask(int count, float half, float blend)
    {
        float cosine = MathF.Cos(half * MathF.PI / 180), width = (1 - cosine) * blend;
        var mask = new float[count];
        for (int i = 0; i < count; i++)
        {
            float x = Math.Clamp((MathF.Cos(i * RingDegrees * MathF.PI / 180) - cosine) / width, 0, 1);
            mask[i] = x * x * (3 - 2 * x);
        }
        return mask;
    }

    /** Mean over AnimationSeconds of a reference sample, relative to the sample at time zero; 1 when the light holds still. */
    private static float Animation(LightContext context, LightMaterial material, AuthoredLightRow row, Vector3 at)
    {
        var first = new LightProbe(context, material, row, 0);
        float start = Vector3.Dot(first.Irradiance(at), Luminance);
        if (start <= 0) return 1;
        bool moved = false; double sum = 0;
        foreach (double seconds in AnimationSeconds)
        {
            var probe = seconds == 0 ? first : new LightProbe(context, material, row, seconds);
            // Bitwise, so a NaN lane does not count as a change.
            moved |= !MemoryMarshal.AsBytes(probe.Constants.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(first.Constants.AsSpan()));
            sum += Vector3.Dot(probe.Irradiance(at), Luminance);
        }
        return moved ? (float)(sum / AnimationSeconds.Length / start) : 1;
    }

    /** The row's +0x8C program, as the deferred light pass runs it: on when an output lane is above zero. */
    private static bool Enabled(LightContext context, AuthoredLightParameters? source)
    {
        if (source is null) return true;
        try
        {
            var stage = new MaterialStageData(GameShaderStage.Pixel, null, source.Bytecode.ToArray(), source.Constants.ToArray(),
                source.Defaults.ToArray(), 0, []);
            var tfx = NativeFrameInputs.Create(0);
            tfx.Parameters = context.Parameters;
            tfx.Floats[new(4, 66)] = 0;
            tfx.Floats[new(4, 68)] = 1;
            var result = TfxEvaluator.Evaluate(stage, tfx);
            result.RequireComplete();
            return result.Constants.Take(4).Any(row => row.X > 0 || row.Y > 0 || row.Z > 0 || row.W > 0);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or InvalidDataException) { return true; }
    }

    /**
     * The time-of-day sun at the given time, with the colour technique 37 adds to the diffuse lighting.
     * Technique 37 shades with the sky direction, extern 41 vector 3; the shadow cascades use the light direction.
     * @return Null when the sun adds no light.
     */
    private static WorldSun? Sun(LightContext context, PlacedTimeOfDay placed, (Vector3 Sky, Vector3 Light) directions, float time)
    {
        var direction = directions.Sky;
        var execution = context.Reader.ReadExecution(NativeAmbientLighting.SunMaterialTag);
        var tfx = NativeFrameInputs.Create(0, timeOfDay: time);
        tfx.Parameters = context.Parameters;
        Fill(tfx, execution.Pixel.TfxBytecode);
        tfx.Vectors[new(41, 1)] = NativeAmbientEnvironment.ReadSunColor(context.Defaults);
        tfx.Vectors[new(41, 3)] = new(direction, 0);
        tfx.Floats[new(1, 7)] = 1;
        var constants = TfxEvaluator.Evaluate(execution.Pixel, tfx).Constants;
        // Technique 37 adds (fade * cb0[65] + cb0[62]) * NdotL / pi; the fade is one near the camera.
        float fade = Math.Clamp(constants[67].Y, 0, 1);
        var color = new Vector3(constants[62].X, constants[62].Y, constants[62].Z) + fade * new Vector3(constants[65].X, constants[65].Y, constants[65].Z);
        float maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (!(maximum > 0)) return null;
        return new([direction.X, direction.Y, direction.Z], [color.X / maximum, color.Y / maximum, color.Z / maximum], maximum,
            time, placed.CurveSetTag, placed.BubbleHash)
        { ShadowDirection = [directions.Light.X, directions.Light.Y, directions.Light.Z] };
    }

    /** Supplies zero for every extern the program reads that the caller has not set. */
    private static void Fill(TfxInputs tfx, byte[] program)
    {
        foreach (var dependency in TfxProgramInspection.Read(program).Dependencies)
        {
            var key = new TfxExternKey(dependency.Extern, dependency.Element);
            switch (dependency.Kind)
            {
                case "Float": tfx.Floats.TryAdd(key, 0); break;
                case "Vector": tfx.Vectors.TryAdd(key, Vector4.Zero); break;
                case "Matrix": tfx.Matrices.TryAdd(key, Matrix4x4.Identity); break;
                default: tfx.Resources.TryAdd(key, new($"extern {key.Extern}.{key.Element}", ShaderResourceViewDimension.Texture2D)); break;
            }
        }
    }

    private static readonly Vector3[] CubeCorners = [new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1),
        new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1)];

    /** The volume matrix of a spot light is projective; the cube maps to a frustum. */
    private static Vector3 Projected(Vector3 point, Matrix4x4 transform)
    {
        var value = Vector4.Transform(new Vector4(point, 1), transform);
        return value.W != 0 ? new Vector3(value.X, value.Y, value.Z) / value.W : new(float.PositiveInfinity);
    }

    private static Vector3[] Fibonacci(int count)
    {
        var result = new Vector3[count];
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < count; i++)
        {
            float z = 1 - 2 * (i + 0.5f) / count, r = MathF.Sqrt(1 - z * z);
            result[i] = new(r * MathF.Cos(golden * i), r * MathF.Sin(golden * i), z);
        }
        return result;
    }

    private static (Vector3 U, Vector3 V) Basis(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(helper, axis));
        return (u, Vector3.Cross(axis, u));
    }

    private static float RingSolidAngle(int ring)
    {
        float half = RingDegrees / 2 * MathF.PI / 180, theta = ring * RingDegrees * MathF.PI / 180;
        return 2 * MathF.PI * (MathF.Cos(MathF.Max(theta - half, 0)) - MathF.Cos(MathF.Min(theta + half, MathF.PI)));
    }

    /** Shared readers; Prepare runs on one thread, LightMaterial and the parameter table are read-only after. */
    private sealed class LightContext(PackageCatalog catalog)
    {
        public GameMaterialReader Reader { get; } = new(catalog);
        public TfxParameterDefaults Defaults => defaults ??= Reader.ReadTfxParameterDefaults(NativeInspectionMaterialInputs.RenderGlobalsTag);
        public TfxParameterLookup Parameters => parameters ??= catalog.ContainsTag(NativeInspectionMaterialInputs.RenderGlobalsTag)
            ? Defaults.CreateSnapshot() : TfxParameterLookup.NativeNullTable;
        public Vector4[] Frame { get; } = NativeFrameScope.Create(new() { SimulationSeconds = 0, FrameFlag425B8 = false, FrameFlag425B9 = false }).Vectors;
        private TfxParameterDefaults? defaults;
        private TfxParameterLookup? parameters;
        private readonly Dictionary<uint, LightMaterial> materials = [];
        private readonly Dictionary<uint, WorldLightShader> shaders = [];
        private readonly Dictionary<uint, LightTexture> textures = [];

        /** Reads a light material (its pixel shader and inputs) once and caches it by tag. */
        public LightMaterial Prepare(uint tag)
        {
            if (materials.TryGetValue(tag, out var ready)) return ready;
            _ = Parameters;
            var execution = Reader.ReadExecution(tag);
            var shader = execution.Material.PixelShader ?? throw new NotSupportedException("A light material has no pixel shader.");
            if (!shaders.TryGetValue(shader.BytecodeTag, out var program))
                shaders.Add(shader.BytecodeTag, program = WorldLightShader.Parse(shader.Disassembly));
            var bound = new Dictionary<int, LightTexture>();
            foreach (var binding in execution.Material.Textures.Where(t => t.Stage == GameShaderStage.Pixel && t.DeclaredByShader))
            {
                if (!textures.TryGetValue(binding.TextureTag, out var texture))
                    textures.Add(binding.TextureTag, texture = LightTexture.Read(catalog, binding));
                bound[(int)binding.Slot] = texture;
            }
            var result = new LightMaterial(execution, program, bound, shader.BytecodeTag);
            materials.Add(tag, result);
            return result;
        }
    }

    private sealed record LightMaterial(MaterialExecutionData Execution, WorldLightShader Shader,
        IReadOnlyDictionary<int, LightTexture> Textures, uint PixelShader);

    /** A decoded texture sampled bilinearly with clamped coordinates, linear colour. */
    private sealed class LightTexture(int width, int height, int depth, Vector4[] texels)
    {
        /** Reads a texture used by a light material. */
        public static LightTexture Read(PackageCatalog catalog, MaterialTextureBinding binding)
        {
            var data = new TextureDataReader(catalog).Read(binding.TextureTag);
            int depth = Math.Max(1, data.Depth);
            Vector4[]? texels = null; int width = 0, height = 0;
            for (int slice = 0; slice < depth; slice++)
            {
                var image = BundleTextureCodec.Decode(data, 0, 0, slice);
                width = image.Width; height = image.Height;
                texels ??= new Vector4[checked(width * height * depth)];
                for (int p = 0; p < width * height; p++)
                {
                    var pixel = image.Pixels;
                    int at = p * 4;
                    float Channel(int c) => binding.IsSrgb ? SrgbToLinear(pixel[at + c] / 255f) : pixel[at + c] / 255f;
                    texels[slice * width * height + p] = new(Channel(0), Channel(1), Channel(2), pixel[at + 3] / 255f);
                }
            }
            return new(width, height, depth, texels!);
        }

        private static float SrgbToLinear(float value) =>
            value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

        /** Texel at integer coordinates, clamped to the texture. */
        public Vector4 Load(int x, int y) => texels[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];

        /** Samples the texture at normalized coordinates. */
        public Vector4 Sample(float s, float t, float r)
        {
            float z = Math.Clamp(r * depth - 0.5f, 0, depth - 1);
            int z0 = (int)z, z1 = Math.Min(z0 + 1, depth - 1);
            return Vector4.Lerp(Slice(z0, s, t), Slice(z1, s, t), z - z0);
        }

        private Vector4 Slice(int slice, float s, float t)
        {
            float x = Math.Clamp(s, 0, 1) * width - 0.5f, y = Math.Clamp(t, 0, 1) * height - 0.5f;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            Vector4 At(int px, int py) => texels[slice * width * height + Math.Clamp(py, 0, height - 1) * width + Math.Clamp(px, 0, width - 1)];
            return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
        }
    }

    /**
     * One light's evaluated shader, sampled at points with the camera at the light origin and the surface facing the light.
     * Extern 2 vector 38 is the identity, so the shader rebuilds the position as (v0.x, v0.y, depth).
     */
    private sealed class LightProbe
    {
        private readonly LightMaterial material;
        private readonly Dictionary<int, Vector4[]> constants;
        private readonly int normalSlot = -1, depthSlot = -1, thirdSlot = -1;
        private readonly HashSet<int> engineSlots = [];
        private readonly Matrix4x4 volume;
        private readonly Vector3 origin;
        public Vector4[] Constants { get; }

        public LightProbe(LightContext context, LightMaterial material, AuthoredLightRow row, double seconds)
        {
            this.material = material;
            origin = row.Translation;
            if (!Matrix4x4.Invert(row.VolumeWorldTransform, out volume)) throw new InvalidDataException("The light volume is singular.");
            var view = Matrix4x4.CreateTranslation(-origin);
            var tfx = NativeFrameInputs.Create(seconds);
            var camera = NativeViewInputs.CreateCameraInputs(64, 64, view, Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 1000));
            foreach (var item in camera.Matrices) tfx.Matrices[item.Key] = item.Value;
            foreach (var item in camera.Floats) tfx.Floats[item.Key] = item.Value;
            tfx.Matrices[new(2, 38)] = Matrix4x4.Identity;
            tfx.Parameters = context.Parameters;
            tfx.Floats[new(1, 7)] = 1;
            tfx.Resources[new(3, 7)] = Depth;
            tfx.Resources[new(3, 10)] = Normal;
            tfx.Resources[new(3, 11)] = Third;
            // Engine textures such as the frame noise volume are not in the packages; they read as mid grey.
            foreach (var dependency in TfxProgramInspection.Read(material.Execution.Pixel.TfxBytecode).Dependencies)
                if (dependency.Kind == "Resource") tfx.Resources.TryAdd(new(dependency.Extern, dependency.Element), Engine);
            // Extern 4 as the deferred light pass writes it, with the distance fade at one.
            var relative = row.Transform * view;
            Matrix4x4.Invert(relative, out var fromRelative);
            Matrix4x4.Invert(row.VolumeTransform * relative, out var fromVolume);
            tfx.Matrices[new(4, 0)] = relative;
            tfx.Matrices[new(4, 4)] = Matrix4x4.Transpose(fromRelative);
            tfx.Matrices[new(4, 8)] = fromVolume;
            tfx.Matrices[new(4, 12)] = relative;
            tfx.Floats[new(4, 64)] = 1;
            tfx.Floats[new(4, 65)] = 0;
            tfx.Floats[new(4, 66)] = 0;
            tfx.Floats[new(4, 67)] = row.Scale;
            tfx.Floats[new(4, 68)] = 1;
            var pixel = TfxEvaluator.Evaluate(material.Execution.Pixel, tfx);
            pixel.RequireComplete();
            Constants = pixel.Constants;
            foreach (var (key, resource) in pixel.ResourceBindings)
            {
                if (key.Stage != GameShaderStage.Pixel) continue;
                if (ReferenceEquals(resource, Normal)) normalSlot = (int)key.Slot;
                else if (ReferenceEquals(resource, Depth)) depthSlot = (int)key.Slot;
                else if (ReferenceEquals(resource, Third)) thirdSlot = (int)key.Slot;
                else if (ReferenceEquals(resource, Engine)) engineSlots.Add((int)key.Slot);
            }
            if (normalSlot < 0 || depthSlot < 0) throw new NotSupportedException("A light shader does not read the normal and depth buffers.");
            constants = new() { [0] = Constants, [12] = new Vector4[13], [13] = context.Frame };
            constants[12][12] = new(64, 64, 1 / 64f, 1 / 64f);
        }

        private static readonly TfxResource Depth = new("depth_stencil_read", ShaderResourceViewDimension.Texture2D);
        private static readonly TfxResource Normal = new("gbuffer_normal_read", ShaderResourceViewDimension.Texture2D);
        private static readonly TfxResource Third = new("gbuffer_third", ShaderResourceViewDimension.Texture2D);
        private static readonly TfxResource Engine = new("engine texture", ShaderResourceViewDimension.Texture2D);
        private const float EngineGrey = 0.5f;
        /** True when the shader reads an engine texture the export stands in for. */
        public bool UsesEngineTexture => engineSlots.Count != 0;
        // Third buffer: specular 0.5, occlusion 1 after its doubling, no subsurface.
        private static readonly Vector4 ThirdValue = new(0.5f, 0.5f, 0, 1);

        /**
         * Irradiance at a point offset from the light origin, on a white surface facing the origin.
         * @param toward Offset of the point the surface faces instead of the origin.
         * @param side The surface lies along the light direction instead, so only light that ignores the normal remains.
         * @return Pi times the diffuse output: the diffuse output carries the Lambert 1/pi.
         */
        public Vector3 Irradiance(Vector3 offset, Vector3 toward = default, bool side = false)
        {
            var local = Projected(origin + offset, volume);
            if (MathF.Abs(local.X) > 1 || MathF.Abs(local.Y) > 1 || MathF.Abs(local.Z) > 1) return Vector3.Zero;
            float distance = (toward - offset).Length();
            if (distance <= 0) return Vector3.Zero;
            var normal = (toward - offset) / distance;
            if (side) normal = Basis(normal).U;
            var encoded = new Vector4((normal * NormalLength + Vector3.One) * 0.5f, 0);
            var (diffuse, _) = material.Shader.Run(constants, new(offset.X, offset.Y, 0, 1), (slot, load, coordinates, level) =>
            {
                if (slot == depthSlot) return new(offset.Z);
                if (slot == normalSlot) return encoded;
                if (slot == thirdSlot) return ThirdValue;
                if (engineSlots.Contains(slot)) return new(EngineGrey);
                if (!material.Textures.TryGetValue(slot, out var texture)) return Vector4.Zero;
                if (load) return texture.Load((int)coordinates[0], (int)coordinates[1]);
                return texture.Sample(BitConverter.UInt32BitsToSingle(coordinates[0]), BitConverter.UInt32BitsToSingle(coordinates[1]),
                    BitConverter.UInt32BitsToSingle(coordinates[2]));
            });
            var value = new Vector3(diffuse.X, diffuse.Y, diffuse.Z) * MathF.PI;
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) ? Vector3.Max(value, Vector3.Zero) : Vector3.Zero;
        }
    }
}
