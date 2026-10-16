using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Environment-view stencil: stage 0 state 8 with reference 4 and the per-draw object bits. */
internal static class StencilChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        Resolution();
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        // 80EDC202 holds static placements; 81A6B357 is an entity model in the environment view.
        Scene(catalog, 0x80EDC202, NativeMeshKind.Static, NativeStencil.EnvironmentStaticReference,
            NativeStencil.EnvironmentStaticBits, [4]);
        Scene(catalog, 0x81A6B357, NativeMeshKind.Entity, NativeStencil.EnvironmentEntityReference,
            NativeStencil.EnvironmentEntityBits, [4, 0x24]);
        // Terrain (80BEA850) needs a map to prepare; DecoratorChecks draws the decorators of 80FAA33B.
        DecoratorPaths(catalog);
    }

    /** Decorator records give the decorator path; terrain meshes the terrain path. */
    private static void DecoratorPaths(PackageCatalog catalog)
    {
        var decorators = new AssetSceneLoader(catalog).Load(0x80FAA33B).Instances;
        var terrain = new AssetSceneLoader(catalog).Load(0x80BEA850).Instances;
        Check(decorators.Count != 0 && decorators.All(row => NativeStencil.PathOf(row) == NativeStencilPath.Decorator)
            && terrain.Count != 0 && terrain.All(row => NativeStencil.PathOf(row) == NativeStencilPath.Terrain),
            "Decorator or terrain parts take another stencil path.");
        Console.WriteLine($"stencil_paths decorators={decorators.Count} terrain={terrain.Count} result=pass");
    }

    /** The draw classes resolve to the states and references the game sets. */
    private static void Resolution()
    {
        var states = new NativeRenderStateCatalog();
        var entity = NativeStencil.Resolve(states, 0, 0, NativeStencilPath.Entity, false);
        var decorator = NativeStencil.Resolve(states, 0, 0, NativeStencilPath.Decorator, false);
        var placed = NativeStencil.Resolve(states, 0, 0, NativeStencilPath.Static, false);
        var terrain = NativeStencil.Resolve(states, 0, 0, NativeStencilPath.Terrain, false);
        var decal = NativeStencil.Resolve(states, 0, 1, NativeStencilPath.Static, false);
        var transparent = NativeStencil.Resolve(states, 0, 7, NativeStencilPath.Entity, false);
        var firstPersonDecal = NativeStencil.Resolve(states, 0, 1, NativeStencilPath.Entity, true);
        Check(entity is { Reference: 0x24, States.DepthStencil: { StencilReadMask: 4, StencilWriteMask: 0x28 } }
            && decorator is { Reference: 0x24, States.DepthStencil.StencilWriteMask: 0xA8 }
            && placed is { Reference: 4, States.DepthStencil.StencilWriteMask: 0xA8 }
            && terrain is { Reference: 4, States.DepthStencil.StencilWriteMask: 0x18 }
            && decal is { Reference: 4, States.DepthStencil: { StencilReadMask: 4, StencilWriteMask: 0 } }
            && transparent is { Reference: 0xFF, States.DepthStencil: { StencilReadMask: 4, StencilWriteMask: 0 } }
            && firstPersonDecal is { States: null, Reference: null },
            "Environment stencil paths resolve differently.");
        Console.WriteLine("stencil_resolution entity=8/0x24 decorator=8/0x24 static=8/0x04 terrain=8/0x04 decal=64/0x04 transparents=64/0xFF result=pass");
    }

    /** State 8 passes only on stencil bit 2, so a clear of 0 leaves stage 0 undrawn. */
    private static void Scene(PackageCatalog catalog, uint tag, NativeMeshKind kind, uint reference, byte bits, byte[] expected)
    {
        const int width = 320, height = 240;
        var scene = new AssetSceneLoader(catalog).Load(tag);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var instance in scene.Instances.Where(row => row.Mesh.Native?.RenderStage == 0))
            foreach (var vertex in instance.Mesh.Vertices)
            {
                var point = Vector3.Transform(vertex.Position, instance.Transform);
                min = Vector3.Min(min, point); max = Vector3.Max(max, point);
            }
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        foreach (bool warp in new[] { false, true })
        {
            var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024);
            var stage0 = plan.Groups.Where(group => group.RenderStage == 0 && group.Mesh.Kind == kind).ToArray();
            Check(stage0.Length != 0 && stage0.All(group => group.Material.StencilReference == reference
                && group.Material.States.DepthStencil.StencilWriteMask == bits),
                $"Stage 0 {kind} draws of {tag:X8} do not use reference 0x{reference:X2} and write mask 0x{bits:X2}.");
            using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, warp);
            using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
            viewport.SetScene(scene);
            viewport.SetNativeScene(plan);
            viewport.Camera.Frame(min, max, (float)width / height);
            viewport.RenderImage(width, height);
            Check(viewport.UseGameShaders && viewport.NativeError is null, viewport.NativeError ?? "Native rendering fell back to geometry.");
            var native = viewport.NativeRenderer!;
            native.AutoExposure = false;
            Vector4[] Frame()
            {
                native.RequestExposureWarmup();
                return renderer.ReadTexturePixels(viewport.RenderImage(width, height), points);
            }
            var drawn = Frame();
            var stencil = native.ReadStencilCopy();
            Check(native.LastFrame.FailedParts == 0 && stencil.All(value => expected.Contains(value))
                && expected.All(value => stencil.Contains(value)),
                $"{tag:X8} stencil holds {string.Join(",", stencil.Distinct().Select(v => $"0x{v:X2}"))}.");
            // Control: with bit 2 clear, state 8 fails everywhere and stage 0 draws nothing.
            native.StencilClear = 0;
            var blocked = Frame();
            var blockedStencil = native.ReadStencilCopy();
            int changed = drawn.Zip(blocked).Count(pair => Vector4.Distance(pair.First, pair.Second) > .003f);
            Check(blockedStencil.All(value => value == 0) && changed > 0,
                $"{tag:X8} stage 0 drew with stencil bit 2 clear: {changed} pixels changed.");
            native.StencilClear = NativeStencil.ClearValue;
            Console.WriteLine($"environment_stencil asset={tag:X8} kind={kind} adapter={(warp ? "warp" : "hardware")} "
                + $"values={string.Join("/", expected.Select(v => $"0x{v:X2}"))} blocked_pixels={changed} result=pass");
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
