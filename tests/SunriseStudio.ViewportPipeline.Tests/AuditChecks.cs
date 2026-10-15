using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;
using static Checks;

/**
 * Phase 14 audit: 60+ FPS at 1080p with 1,000,000+ triangles, a destination map in the viewport, and no growth
 * across 50 load/unload cycles. It needs the install and a hardware adapter; without either it prints a
 * "skipped:" line and passes. Trostland church and Tower plaza need their map tags: see todo.json Phase 14.
 */
internal static class AuditChecks
{
    private const int Width = 1920, Height = 1080, BenchmarkFrames = 120, Cycles = 50;
    private const float MinimumFps = 60;
    // Leviathan, the map the other installed checks already load.
    private const uint DestinationMap = 0x80EDC19B;
    private const long MaximumGrowthBytes = 96L * 1024 * 1024;

    public static void Run(string? game, List<string> checks)
    {
        if (game is null)
        {
            Console.WriteLine("skipped: Phase 14 viewport audit (needs --game <install> and a hardware adapter)");
            return;
        }
        Dx11Renderer renderer;
        try { renderer = new Dx11Renderer(IntPtr.Zero, Width, Height); }
        catch (Exception error)
        {
            Console.WriteLine($"skipped: Phase 14 viewport audit (no Direct3D 11 device: {error.Message})");
            return;
        }
        using (renderer)
        {
            if (renderer.IsWarp)
            {
                Console.WriteLine("skipped: Phase 14 viewport audit (no hardware adapter; WARP is not a benchmark)");
                return;
            }
            Benchmark(renderer, checks);
            using var catalog = PackageCatalog.Open(game, cacheOptions: new() { Enabled = false });
            var scene = new AssetSceneLoader(catalog).LoadMap(DestinationMap);
            Destination(renderer, scene, checks);
            Cycling(renderer, scene, checks);
        }
    }

    /** A flat grid of 709 x 709 quads: 1,002,528 triangles facing the camera. */
    private static SceneDocument MillionTriangles()
    {
        const int quads = 709;
        var vertices = new SceneVertex[(quads + 1) * (quads + 1)];
        for (int y = 0; y <= quads; y++)
            for (int x = 0; x <= quads; x++)
                vertices[y * (quads + 1) + x] = new(new(x - quads / 2f, 0, y - quads / 2f), -Vector3.UnitY,
                    new(x / (float)quads, y / (float)quads), Vector4.One);
        var indices = new uint[quads * quads * 6];
        int at = 0;
        for (int y = 0; y < quads; y++)
            for (int x = 0; x < quads; x++)
            {
                uint a = (uint)(y * (quads + 1) + x), b = a + 1, c = a + (uint)(quads + 1), d = c + 1;
                indices[at++] = a; indices[at++] = c; indices[at++] = b;
                indices[at++] = b; indices[at++] = c; indices[at++] = d;
            }
        var scene = new SceneDocument();
        scene.Instances.Add(new MeshInstance { Mesh = new MeshData { Vertices = vertices, Indices = indices } });
        return scene;
    }

    private static void Benchmark(Dx11Renderer renderer, List<string> checks)
    {
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(MillionTriangles());
        viewport.Camera.LookFrom(new(0, -500, 0), Vector3.Zero);
        long before = renderer.Triangles;
        renderer.ReadTexturePixel(viewport.RenderImage(Width, Height), 0, 0);
        long drawn = renderer.Triangles - before;
        Require(drawn >= 1_000_000, $"The benchmark drew {drawn:N0} triangles, not 1,000,000+.");
        var clock = Stopwatch.StartNew();
        GpuTexture? image = null;
        for (int frame = 0; frame < BenchmarkFrames; frame++) image = viewport.RenderImage(Width, Height, frame / 60.0);
        renderer.ReadTexturePixel(image!, 0, 0);
        double fps = BenchmarkFrames / clock.Elapsed.TotalSeconds;
        Console.WriteLine($"audit adapter={renderer.AdapterName} triangles={drawn} fps={fps:0.0}");
        Require(fps >= MinimumFps, $"{fps:0.0} FPS at {Width}x{Height} with {drawn:N0} triangles; the target is {MinimumFps}.");
        checks.Add($"{drawn:N0} triangles render at {fps:0} FPS at {Width}x{Height} on {renderer.AdapterName}");
    }

    private static void Destination(Dx11Renderer renderer, SceneDocument scene, List<string> checks)
    {
        Require(scene.Instances.Count > 0, $"Map {DestinationMap:X8} loaded no meshes.");
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.Frame();
        long before = renderer.Triangles;
        renderer.ReadTexturePixel(viewport.RenderImage(Width, Height), 0, 0);
        long drawn = renderer.Triangles - before;
        Require(drawn > 0, $"Map {DestinationMap:X8} drew no triangles.");
        checks.Add($"map {DestinationMap:X8} shows {scene.Instances.Count:N0} meshes ({drawn:N0} triangles in view)");
    }

    private static long Settled()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /** Loads and clears the destination 50 times; private bytes after warm-up may not keep growing. */
    private static void Cycling(Dx11Renderer renderer, SceneDocument scene, List<string> checks)
    {
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        long warm = 0;
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            viewport.SetScene(scene);
            renderer.ReadTexturePixel(viewport.RenderImage(640, 360), 0, 0);
            viewport.Clear();
            if (cycle == 4) warm = Settled();
        }
        long growth = Settled() - warm;
        Console.WriteLine($"audit cycles={Cycles} growth_bytes={growth}");
        Require(growth < MaximumGrowthBytes, $"Private bytes grew {growth / (1024 * 1024)} MiB over {Cycles - 5} load/unload cycles.");
        checks.Add($"{Cycles} load/unload cycles grow private bytes by {growth / (1024 * 1024)} MiB (limit {MaximumGrowthBytes / (1024 * 1024)})");
    }
}
