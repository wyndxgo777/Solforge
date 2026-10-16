using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Sun shadows: the cascade math, then map 80EDC19B against a CPU ray, then decorator casters. */
internal static class ShadowChecks
{
    private const uint MapTag = 0x80EDC19B;
    // An open garden spawn: about half its sunlit floor is shadowed by the building beside it.
    private static readonly Vector3 GardenSpawn = new(-136.7777f, -1439.7067f, 6.5f);
    // A decorator tree whose own shadow lands in view when looked at from the sun side.
    private static readonly Vector3 TreeBase = new(-11.413317f, -1222.9293f, -78.321365f);
    private const int Width = 400, Height = 250;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        Math();
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        var world = new WorldLoader(catalog).LoadMap(MapTag, new() { IncludeLowerLods = false });
        var hardware = Map(catalog, world, false);
        var warp = Map(catalog, world, true);
        int same = hardware.Mask.Zip(warp.Mask).Count(pair => pair.First < 0.5f == pair.Second < 0.5f);
        Check(same >= hardware.Mask.Length * 97 / 100, $"Hardware and WARP masks agree at {same} of {hardware.Mask.Length} points.");
        Console.WriteLine($"shadow_devices agree={same}/{hardware.Mask.Length}");
        var trees = new[] { Decorators(catalog, world, false), Decorators(catalog, world, true) };
        Check(MathF.Abs(trees[0] - trees[1]) <= MathF.Max(trees[0], trees[1]) * 0.1f,
            $"Decorator shadows differ between hardware ({trees[0]}) and WARP ({trees[1]}).");
    }

    /** Splits, fits, fades, the stage word and the decorator shadow window, without the GPU. */
    private static void Math()
    {
        var splits = NativeShadowCascades.Splits(0.05f);
        double near = 0.05 * 1.002, previous = near;
        // The first three spread over half the distance; the last ends at it.
        for (int i = 0; i < splits.Length; i++)
        {
            double t = (i + 1) / 3.0, uniform = (150 - 0.05) * t + 0.05;
            double split = i == 3 ? 300 : (System.Math.Pow(150 / 0.05, t) * 0.05 - uniform) * 0.6 + uniform;
            double blend = i == 3 ? split : (split - previous) * 0.05 + split;
            Check(System.Math.Abs(splits[i].Near - previous) < 1e-3 && System.Math.Abs(splits[i].Split - split) < 1e-3
                && System.Math.Abs(splits[i].BlendFar - blend) < 1e-3, $"Cascade {i} split {splits[i]} differs from {previous}, {split}, {blend}.");
            previous = split;
        }
        Check(splits[^1].Split == NativeShadowCascades.Distance, "The last cascade does not end at the shadow distance.");
        var view = Matrix4x4.CreateLookAt(new(10, -20, 15), new(40, 30, 5), Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 1.6f, 0.05f, 10000);
        var light = Vector3.Normalize(new Vector3(0.3f, -0.5f, 0.8f));
        var cascades = NativeShadowCascades.Fit(view, projection, 0.05f, new(10, -20, 15), light);
        Matrix4x4.Invert(view * projection, out var inverse);
        Check(cascades.Select(c => c.Resolution).SequenceEqual([1536, 1536, 1280, 1024]), "Cascade resolutions are not profile row 2's.");
        foreach (var cascade in cascades)
        {
            // Only cascade 3 is stable: square and on whole texels.
            bool stable = cascade.Index == 3;
            Check(stable == MathF.Abs(cascade.Extents.X - cascade.Extents.Y) < 1e-3f, $"Cascade {cascade.Index} squareness is wrong.");
            float texel = cascade.Extents.X / cascade.Resolution;
            // The depth range is half the slice depth plus the backward distance, which is at least that half.
            Check(cascade.Extents.Z <= 2 * cascade.Backward + 1e-2f, $"Cascade {cascade.Index} backward distance is below half its depth.");
            foreach (float distance in new[] { cascade.Near, cascade.BlendFar })
            {
                var depth = Vector4.Transform(new Vector4(0, 0, -distance, 1), projection);
                for (int corner = 0; corner < 4; corner++)
                {
                    var world = Vector4.Transform(new Vector4((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, depth.Z / depth.W, 1), inverse);
                    var clip = Vector4.Transform(new Vector4(world.X / world.W, world.Y / world.W, world.Z / world.W, 1), cascade.ViewProjection);
                    Check(MathF.Abs(clip.X) <= 1.001f && MathF.Abs(clip.Y) <= 1.001f && clip.Z is >= -0.001f and <= 1.001f,
                        $"Cascade {cascade.Index} misses a slice corner at {clip}.");
                }
            }
            if (!stable) continue;
            // Snapping keeps the light-space origin on whole texels of the light basis.
            var basis = Matrix4x4.CreateLookAt(Vector3.Zero, -light, Vector3.UnitX);
            Matrix4x4.Invert(cascade.LightView, out var lightToWorld);
            var origin = Vector3.Transform(new Vector3(lightToWorld.M41, lightToWorld.M42, lightToWorld.M43), basis);
            float stepsX = origin.X / texel, stepsY = origin.Y / texel;
            Check(MathF.Abs(stepsX - MathF.Round(stepsX)) < 0.01f && MathF.Abs(stepsY - MathF.Round(stepsY)) < 0.01f,
                $"Cascade {cascade.Index} origin is off the texel grid: {stepsX}, {stepsY}.");
        }
        // Inside a blend band the outgoing and incoming fades sum to one.
        for (int i = 0; i + 1 < cascades.Length; i++)
        {
            var outgoing = NativeShadowMask.Fades(cascades, i); var incoming = NativeShadowMask.Fades(cascades, i + 1);
            float middle = (cascades[i].Split + cascades[i].BlendFar) * 0.5f;
            float sum = System.Math.Clamp(outgoing.X * middle + outgoing.Y, 0, 1) + System.Math.Clamp(incoming.Z * middle + incoming.W, 0, 1);
            Check(MathF.Abs(sum - 1) < 1e-3f, $"Cascades {i} and {i + 1} do not cross-fade: {sum}.");
        }
        var first = NativeShadowMask.Fades(cascades, 0);
        Check(first.Z == 0 && first.W == 1, "The first cascade fades in.");
        int[][] windows = [[0, 1], [4, 6], [6, 10], [11, 14]];
        for (int level = 0; level < 5; level++)
            for (int index = 0; index <= 16; index++)
                Check(SpeedTreeLod.DrawsShadow(level, index) == (level < 4 && index >= windows[level][0] && index <= windows[level][1]),
                    $"Shadow LOD window of level {level} is wrong at index {index}.");
        var states = NativeScenePlan.ResolveStates(new NativeRenderStateCatalog(), 0, NativeScenePlan.ShadowStage);
        Check(states.Selection == new NativeStateSelection(78, 13, 1, 3) && states.DepthStencil.DepthFunc == Vortice.Direct3D11.ComparisonFunction.LessEqual
            && states.DepthStencil.DepthWriteMask == Vortice.Direct3D11.DepthWriteMask.All && states.Rasterizer.CullMode == Vortice.Direct3D11.CullMode.None
            && states.Rasterizer.DepthBias == 10 && states.Rasterizer.SlopeScaledDepthBias == 4
            && states.Blend.RenderTarget[0].RenderTargetWriteMask == Vortice.Direct3D11.ColorWriteEnable.None,
            $"shadow_generate resolves to {states.Selection}.");
        Console.WriteLine($"shadow_math splits={string.Join(",", splits.Select(s => s.Split.ToString("F1")))} windows=pass states=pass");
    }

    private sealed record MapResult(float[] Mask);

    /**
     * The garden view: technique 37 matches its CPU port, the mask matches a CPU ray over the caster triangles,
     * and shadows off leave the mask at one.
     */
    private static MapResult Map(PackageCatalog catalog, WorldDocument world, bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(world.Scene);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world.Scene, textureBudgetBytes: long.MaxValue));
        viewport.Visibility.Filter = item => item is MeshInstance;
        var native = viewport.NativeRenderer ?? throw new InvalidOperationException(viewport.NativeError);
        Check(native.ShadowCasterPartCount > 100000, $"Only {native.ShadowCasterPartCount} caster parts were prepared.");
        native.SkyLightingEnabled = true;
        var spawn = world.Scene.Markers.Where(x => x.Layer == SceneLayer.Spawns).MinBy(x => Vector3.Distance(x.Position, GardenSpawn))!;
        var facing = Vector3.Transform(Vector3.UnitX, spawn.Rotation); facing.Z = 0; facing = Vector3.Normalize(facing);
        var eye = spawn.Position + Vector3.UnitZ * 1.7f;
        viewport.Camera.LookFrom(eye, eye + facing * 10);
        var all = native.ShadowCasterFilter!;
        // Decorators draw one LOD per shadow window and clip their leaves, which a triangle ray does not see.
        native.ShadowCasterFilter = item => all(item) && NativeStencil.PathOf(item) != NativeStencilPath.Decorator;
        viewport.RenderImage(Width, Height, 2);
        Check(native.SunActive && native.ShadowsActive && native.ShadowError is null && native.LightingError is null,
            native.ShadowError ?? native.LightingError ?? "The sun or its shadows did not run.");
        var check = native.ValidateLighting();
        string drawn = $"parts=[{string.Join(",", native.CascadePartCounts)}] draws={native.LastShadowDraws}";
        Check(check.ShadowedPixels > check.LitPixels / 10 && check.ShadowedPixels < check.LitPixels * 9 / 10 && check.SunPixels > check.LitPixels / 10,
            $"The garden view has {check.ShadowedPixels} shadowed and {check.SunPixels} sunlit of {check.LitPixels} pixels.");
        int recordings = native.ShadowRecordings;
        viewport.RenderImage(Width, Height, 2);
        Check(native.ShadowRecordings == recordings, "A still camera recorded its fixed casters again.");
        var points = Enumerable.Range(0, 20).SelectMany(y => Enumerable.Range(0, 32).Select(x => (x * 12 + 6, y * 12 + 6))).ToArray();
        // Mask mode 1 is half resolution; X shadows the nearest depth of each 2x2 block.
        var maskTexture = native.ShadowMaskTexture!;
        Check(maskTexture.Width == (Width + 1) / 2 && maskTexture.Height == (Height + 1) / 2, "The shadow mask is not half resolution.");
        var halfPoints = points.Select(p => (p.Item1 / 2, p.Item2 / 2)).ToArray();
        var mask = renderer.ReadTexturePixels(maskTexture, halfPoints).Select(v => v.X).ToArray();
        var (agree, compared, lit, shadowed) = RayAgreement(world, native, viewport, renderer, points, mask, eye);
        Check(lit >= 50 && shadowed >= 50 && agree >= compared * 93 / 100,
            $"The mask matches a CPU ray at {agree} of {compared} points ({lit} lit, {shadowed} shadowed).");
        native.ShadowsEnabled = false;
        viewport.RenderImage(Width, Height, 2);
        var off = native.ValidateLighting();
        var unshadowed = renderer.ReadTexturePixels(native.ShadowMaskTexture!, halfPoints);
        Check(off.ShadowedPixels == 0 && unshadowed.All(v => v.X == 1 && v.Y == 1) && off.SunPixels > check.SunPixels,
            "Shadows off still darkened the sun.");
        native.ShadowsEnabled = true;
        native.ShadowCasterFilter = all;
        Console.WriteLine($"shadow_map device={(warp ? "warp" : "hardware")} casters={native.ShadowCasterPartCount} "
            + $"{drawn} lit={check.LitPixels} "
            + $"sun={check.SunPixels} shadowed={check.ShadowedPixels} diffuse_error={check.MaximumError:G3} "
            + $"specular_error={check.MaximumSpecularError:G3} ray={agree}/{compared} unshadowed_sun={off.SunPixels}");
        return new(mask);
    }

    /** A point is shadowed when a ray toward the light hits a caster triangle below the caster plane. */
    private static (int Agree, int Compared, int Lit, int Shadowed) RayAgreement(WorldDocument world, NativeSceneRenderer native,
        AssetViewport viewport, Dx11Renderer renderer, (int X, int Y)[] points, float[] mask, Vector3 eye)
    {
        var casters = world.Scene.Instances.Where(i => i.Mesh.Native?.RenderStage == NativeScenePlan.ShadowStage
            && native.ShadowCasterFilter!(i)).Select(i =>
        {
            var minimum = new Vector3(float.MaxValue); var maximum = new Vector3(float.MinValue);
            foreach (var vertex in i.Mesh.Vertices)
            {
                var p = Vector3.Transform(vertex.Position, i.Transform);
                minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
            }
            return (Instance: i, Center: (minimum + maximum) / 2, Radius: (maximum - minimum).Length() / 2);
        }).ToArray();
        var light = native.LightDirection;
        // Casters above this plane are outside every cascade, as the native fit's backward distance leaves them.
        float plane = NativeShadowCascades.CasterHeight + MathF.Max(eye.Z, 0);
        Matrix4x4.Invert(viewport.Camera.View * viewport.Camera.Projection((float)Width / Height), out var inverse);
        var depth = renderer.ReadNativeDepthPixels(native.Targets, points);
        var normals = renderer.ReadTexturePixels(native.GBufferInputs!.Normal, points);
        int agree = 0, compared = 0, lit = 0, shadowed = 0;
        for (int k = 0; k < points.Length; k++)
        {
            // Skip the sky, PCF edges, far points and faces turned from the light.
            if (depth[k].X >= 1 || mask[k] is > 0.1f and < 0.9f) continue;
            var ndc = new Vector4((points[k].X + .5f) / Width * 2 - 1, 1 - (points[k].Y + .5f) / Height * 2, depth[k].X, 1);
            var w = Vector4.Transform(ndc, inverse); var position = new Vector3(w.X, w.Y, w.Z) / w.W;
            var normal = Vector3.Normalize(new Vector3(normals[k].X, normals[k].Y, normals[k].Z) * 2 - Vector3.One);
            if (Vector3.Distance(position, eye) > 100 || Vector3.Dot(normal, light) < 0.1f) continue;
            var origin = position + normal * 0.05f + light * 0.05f;
            bool hit = casters.Any(c => Hits(c.Instance, c.Center, c.Radius, origin, light, plane));
            bool dark = mask[k] < 0.5f;
            compared++;
            if (hit == dark) { agree++; if (dark) shadowed++; else lit++; }
        }
        return (agree, compared, lit, shadowed);
    }

    private static bool Hits(MeshInstance instance, Vector3 center, float radius, Vector3 origin, Vector3 direction, float plane)
    {
        var toCenter = center - origin;
        float along = Vector3.Dot(toCenter, direction);
        if (along < -radius || (toCenter - direction * along).Length() > radius) return false;
        var vertices = instance.Mesh.Vertices; var indices = instance.Mesh.Indices;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = Vector3.Transform(vertices[indices[t]].Position, instance.Transform);
            var e1 = Vector3.Transform(vertices[indices[t + 1]].Position, instance.Transform) - a;
            var e2 = Vector3.Transform(vertices[indices[t + 2]].Position, instance.Transform) - a;
            var p = Vector3.Cross(direction, e2); float determinant = Vector3.Dot(e1, p);
            if (MathF.Abs(determinant) < 1e-12f) continue;
            var s = origin - a; float u = Vector3.Dot(s, p) / determinant;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1); float v = Vector3.Dot(direction, q) / determinant;
            if (v < 0 || u + v > 1) continue;
            float distance = Vector3.Dot(e2, q) / determinant;
            if (distance > 0.2f && (origin + direction * distance).Z < plane - 10) return true;
        }
        return false;
    }

    /** Decorator casters alone shadow the ground behind a tree; the count is the shadowed lit pixels. */
    private static int Decorators(PackageCatalog catalog, WorldDocument world, bool warp)
    {
        using var renderer = new Dx11Renderer(IntPtr.Zero, Width, Height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(world.Scene);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, world.Scene, textureBudgetBytes: long.MaxValue));
        viewport.Visibility.Filter = item => item is MeshInstance;
        var native = viewport.NativeRenderer ?? throw new InvalidOperationException(viewport.NativeError);
        native.SkyLightingEnabled = true;
        var tree = world.Scene.Instances.Where(i => i.Mesh.Native?.RenderStage == NativeScenePlan.ShadowStage
            && NativeStencil.PathOf(i) == NativeStencilPath.Decorator).MinBy(i => Vector3.Distance(i.Transform.Translation, TreeBase))!;
        var basePosition = tree.Transform.Translation;
        viewport.Camera.LookFrom(basePosition + new Vector3(0, 0, 50), basePosition);
        viewport.RenderImage(Width, Height, 2);
        var light = native.LightDirection;
        var across = Vector3.Normalize(new Vector3(light.X, light.Y, 0));
        viewport.Camera.LookFrom(basePosition + across * 10 + Vector3.UnitZ * 6, basePosition - across * 4);
        var all = native.ShadowCasterFilter!;
        native.ShadowCasterFilter = item => all(item) && NativeStencil.PathOf(item) == NativeStencilPath.Decorator;
        viewport.RenderImage(Width, Height, 2);
        Check(native.ShadowsActive && native.ShadowError is null, native.ShadowError ?? "Decorator shadows did not run.");
        int decorators = native.ValidateLighting().ShadowedPixels;
        int decoratorDraws = native.LastShadowDraws;
        // With no caster at all nothing is shadowed, so the decorators made those shadows.
        native.ShadowCasterFilter = _ => false;
        viewport.RenderImage(Width, Height, 2);
        int withoutTree = native.ValidateLighting().ShadowedPixels;
        Check(decorators > 500 && decoratorDraws > 0 && withoutTree == 0,
            $"Decorator casters shadowed {decorators} pixels in {decoratorDraws} draws; no casters shadowed {withoutTree}.");
        native.ShadowCasterFilter = all;
        Console.WriteLine($"shadow_decorators device={(warp ? "warp" : "hardware")} tree={tree.Mesh.TagHash:X8} shadowed={decorators} "
            + $"draws={decoratorDraws} no_casters={withoutTree}");
        return decorators;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
