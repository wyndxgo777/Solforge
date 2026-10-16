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
 * The destination and every cycle use the game-shader path, read from the packages each time; the benchmark mesh
 * is a synthetic flat grid on the geometry path.
 */
internal static class AuditChecks
{
    private const int Width = 1920, Height = 1080, BenchmarkFrames = 120, Cycles = 50;
    private const float MinimumFps = 60;
    // Leviathan, the map the other installed checks already load.
    private const uint DestinationMap = 0x80EDC19B;
    private const long MaximumGrowthBytes = 96L * 1024 * 1024;
    private const long MaximumVideoGrowthBytes = 64L * 1024 * 1024;
    private const long TextureBudgetBytes = 256L * 1024 * 1024;

    /** Entry point: runs every check in this class and throws on the first failure. */
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
            Destination(renderer, catalog, checks);
            Cycling(renderer, catalog, checks);
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

    /** Reads the destination from the packages and shows it with game shaders, as the Studio opens a map. */
    private static SceneDocument LoadNative(AssetViewport viewport, PackageCatalog catalog, int width, int height)
    {
        var scene = new WorldLoader(catalog).LoadMap(DestinationMap, new() { IncludeLowerLods = false }).Scene;
        Require(scene.Instances.Count > 0, $"Map {DestinationMap:X8} loaded no meshes.");
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: TextureBudgetBytes);
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.Frame();
        viewport.RenderImage(width, height, 1);
        Require(viewport.UseGameShaders && viewport.NativeError is null,
            viewport.NativeError ?? $"Map {DestinationMap:X8} fell back to geometry instead of game shaders.");
        return scene;
    }

    private static void Destination(Dx11Renderer renderer, PackageCatalog catalog, List<string> checks)
    {
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        var scene = LoadNative(viewport, catalog, Width, Height);
        long before = renderer.Triangles;
        renderer.ReadTexturePixel(viewport.RenderImage(Width, Height, 2), 0, 0);
        long drawn = renderer.Triangles - before;
        Require(drawn > 0, $"Map {DestinationMap:X8} drew no triangles.");
        checks.Add($"map {DestinationMap:X8} shows {scene.Instances.Count:N0} meshes with game shaders ({drawn:N0} triangles in view)");
    }

    /** Private bytes and adapter video memory once released objects are collected and their GPU frees are flushed. */
    private static (long Private, long Video) Settled(Dx11Renderer renderer)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        renderer.Context.Flush();
        using var process = Process.GetCurrentProcess();
        return (process.PrivateMemorySize64, renderer.VideoMemoryUsage);
    }

    /**
     * Reads the destination from the packages, shows it with game shaders and clears it, 50 times; after warm-up
     * private bytes and video memory may not keep growing.
     */
    private static void Cycling(Dx11Renderer renderer, PackageCatalog catalog, List<string> checks)
    {
        const int width = 640, height = 360, warmCycle = 4;
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        (long Private, long Video) warm = default;
        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            LoadNative(viewport, catalog, width, height);
            renderer.ReadTexturePixel(viewport.RenderImage(width, height, 2), 0, 0);
            viewport.Clear();
            if (cycle == warmCycle) warm = Settled(renderer);
        }
        var end = Settled(renderer);
        long growth = end.Private - warm.Private;
        bool video = warm.Video >= 0 && end.Video >= 0;
        long videoGrowth = video ? end.Video - warm.Video : 0;
        int measured = Cycles - warmCycle - 1;
        Console.WriteLine($"audit cycles={Cycles} growth_bytes={growth} video_growth_bytes={(video ? videoGrowth : -1)}");
        Require(growth < MaximumGrowthBytes, $"Private bytes grew {growth / (1024 * 1024)} MiB over {measured} load/unload cycles.");
        Require(!video || videoGrowth < MaximumVideoGrowthBytes,
            $"Video memory grew {videoGrowth / (1024 * 1024)} MiB over {measured} load/unload cycles.");
        checks.Add($"{Cycles} game-shader load/unload cycles of map {DestinationMap:X8} grow private bytes by "
            + $"{growth / (1024 * 1024)} MiB (limit {MaximumGrowthBytes / (1024 * 1024)})"
            + (video ? $" and video memory by {videoGrowth / (1024 * 1024)} MiB (limit {MaximumVideoGrowthBytes / (1024 * 1024)})"
                : "; the adapter reports no video memory use"));
    }
}
