using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Decorators: the native draw of 80FAA33B and a ground payload; the wind ports against a reference file. */
internal static class DecoratorChecks
{
    // 80FAA33B places payload 80FAA33A; type 80FE0E68 and wind settings 80C239AB come with it.
    private const uint SceneTag = 0x80FAA33B, TypeTag = 0x80FE0E68, WindTag = 0x80C239AB;
    // 80F101F3 holds ground payload 80B50087 at +0xD8; its one type is 80F101F1.
    private const uint GroundSceneTag = 0x80F101F3, GroundTypeTag = 0x80F101F1;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        Curve();
        foreach (bool warp in new[] { false, true }) Draw(catalog, warp);
        foreach (bool warp in new[] { false, true }) Ground(catalog, warp);
        GroundWorld(catalog);
    }

    /** Map 80EDC19B has wind settings: its ground cells prepare, draw and move with the wind. */
    private static void GroundWorld(PackageCatalog catalog)
    {
        const int width = 320, height = 240;
        var scene = new WorldLoader(catalog).LoadMap(0x80EDC19B, new() { IncludeLowerLods = false }).Scene;
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 1024L * 1024 * 1024);
        var ground = plan.Groups.SelectMany(group => group.Parts).Where(part => part.Decorator?.Payload.Ground == true).ToArray();
        Check(plan.WindSettings is not null && ground.Length > 0 && !plan.SkippedReasons.ContainsKey(NativeScenePlan.DecoratorSkip),
            $"Map 80EDC19B: wind {plan.WindSettings is not null}, ground parts {ground.Length}, decorator skips "
            + plan.SkippedReasons.GetValueOrDefault(NativeScenePlan.DecoratorSkip));
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, false);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.RenderImage(width, height, 1);
        Check(viewport.UseGameShaders && viewport.NativeError is null, viewport.NativeError ?? "Native rendering fell back to geometry.");
        var native = viewport.NativeRenderer!;
        native.AutoExposure = false;
        Vector4[] Frame(double seconds) => renderer.ReadTexturePixels(viewport.RenderImage(width, height, seconds), points);
        int Changed(Vector4[] first, Vector4[] second) => first.Zip(second).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        // Look down on the densest LOD 0 cells until one is not hidden by other geometry.
        int pixels = 0, moved = 0;
        foreach (var cell in ground.Where(part => part.Decorator!.MeshIndex == 0)
            .OrderByDescending(part => part.Instance.NativeInstanceData.Length).Take(40))
        {
            var box = cell.Instance.CellBounds!.Value;
            var center = (box.Min + box.Max) * 0.5f;
            viewport.Camera.LookFrom(new(center.X + 3, center.Y + 3, box.Max.Z + 4), center);
            var drawn = Frame(20);
            viewport.Visibility.Filter = item => item is not MeshInstance part || NativeStencil.PathOf(part) != NativeStencilPath.Decorator;
            var bare = Frame(20);
            viewport.Visibility.Filter = null;
            pixels = Changed(drawn, bare);
            if (pixels < 1000) continue;
            moved = Changed(drawn, Frame(21.5));
            break;
        }
        Check(native.LastFrame.FailedParts == 0 && native.DecoratorWindFallback is null && pixels >= 1000 && moved > 0,
            $"Map ground decorators: pixels {pixels}, wind pixels {moved}, failed {native.LastFrame.FailedParts}, "
            + $"wind '{native.DecoratorWindFallback}'.");
        Console.WriteLine($"ground_decorator_world map=80EDC19B parts={ground.Length} pixels={pixels} wind_pixels={moved} result=pass");
    }

    /** The SpeedTree and ground wind ports against native values from a JSON file the caller supplies. */
    public static void Reference(string install, string reference)
    {
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        using var json = JsonDocument.Parse(File.ReadAllBytes(reference));
        static float[] Values(JsonElement value) => value.EnumerateArray().Select(lane => lane.GetSingle()).ToArray();
        static Vector4 V4(JsonElement value) => new(Values(value));
        var speedTree = json.RootElement.GetProperty("speedTree");
        Wind(catalog, speedTree.GetProperty("frames").GetInt32(), speedTree.GetProperty("rate").GetSingle(),
            speedTree.GetProperty("rows").EnumerateArray().Select(row => (row.GetProperty("row").GetInt32(), V4(row.GetProperty("value")))).ToArray());
        var ground = json.RootElement.GetProperty("ground");
        var scene = new AssetSceneLoader(catalog).Load(GroundSceneTag);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 256L * 1024 * 1024);
        var type = plan.Groups.Where(group => group.RenderStage != NativeScenePlan.ShadowStage).SelectMany(group => group.Parts)
            .First(part => part.Decorator?.Payload.Ground == true).Decorator!.Type;
        Check(type.Tag == GroundTypeTag, $"Ground payload type is {type.Tag:X8}, not {GroundTypeTag:X8}.");
        float[] direction = Values(ground.GetProperty("direction"));
        GroundWind(type, new(direction[0], direction[1], direction[2]), ground.GetProperty("strength").GetSingle(),
            V4(ground.GetProperty("value")));
    }

    /** The rows after the given number of frames at the given rate. */
    private static void Wind(PackageCatalog catalog, int frames, float rate, (int Row, Vector4 Value)[] expected)
    {
        byte[] type = catalog.Read(TypeTag);
        static ReadOnlySpan<byte> Array(byte[] data, int field, int bytes) =>
            data.AsSpan((int)(field + 8 + BitConverter.ToInt64(data, field + 8) + 16), bytes);
        var parameters = MemoryMarshal.Cast<byte, float>(Array(type, 0x18, SpeedTreeWind.ParameterCount * 4)).ToArray();
        var wind = new SpeedTreeWind(parameters, Array(type, 0x28, SpeedTreeWind.OptionCount));
        var settings = NativeWindSettings.Parse(catalog.Read(WindTag));
        Check(frames > 0 && expected.Length > 0, "The wind reference has no frames or rows.");
        Vector4[] rows = [];
        for (int frame = 1; frame <= frames; frame++)
        {
            float time = frame / rate;
            var (direction, strength) = settings.Sample(time);
            rows = wind.Update(time, direction, strength);
        }
        foreach (var (row, value) in expected) Near(rows[row], value, $"row {row}");
        Console.WriteLine($"decorator_wind frames={frames} rows={expected.Length} reference=file result=pass");
    }

    private static void Near(Vector4 actual, Vector4 expected, string name)
    {
        for (int lane = 0; lane < 4; lane++)
            Check(MathF.Abs(actual[lane] - expected[lane]) <= 1e-5f * MathF.Max(1, MathF.Abs(expected[lane])),
                $"Wind {name} lane {lane} is {actual[lane]}, the reference {expected[lane]}.");
    }

    /** One near the camera, then zero, then minus one; LOD 0 draws only near. */
    private static void Curve()
    {
        var curve = SpeedTreeLod.ForInstance(2, 0.2f, new(5, 70, 150, 160), 1, 1, 13.5f);
        Check(SpeedTreeLod.Fade(curve, 1) == 1 && SpeedTreeLod.Fade(curve, 120) == 0 && SpeedTreeLod.Fade(curve, 400) == -1
            && SpeedTreeLod.Draws(0, SpeedTreeLod.Index(curve, 1)) && !SpeedTreeLod.Draws(0, SpeedTreeLod.Index(curve, 120)),
            "The decorator LOD curve does not fade with distance.");
        Console.WriteLine("decorator_curve result=pass");
    }

    /** Every stage 0 part prepares and draws; wind moves leaves; LOD levels follow the distance. */
    private static void Draw(PackageCatalog catalog, bool warp)
    {
        const int width = 320, height = 240;
        var scene = new AssetSceneLoader(catalog).Load(SceneTag);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 256L * 1024 * 1024);
        int stage0 = scene.Instances.Count(row => row.Mesh.Native?.RenderStage == 0);
        // Shadow caster parts prepare beside the colour parts.
        int stage3 = scene.Instances.Count(row => row.Mesh.Native?.RenderStage == NativeScenePlan.ShadowStage);
        int colour = plan.Groups.Where(group => group.RenderStage != NativeScenePlan.ShadowStage).Sum(group => group.Parts.Count);
        Check(colour == stage0 && plan.PreparedPartCount == stage0 + stage3 && plan.Groups.All(group => group.Parts[0].Decorator is not null),
            $"{colour} of {stage0} decorator parts and {plan.PreparedPartCount - colour} of {stage3} casters prepared: "
            + string.Join("; ", plan.SkippedReasons.Select(r => $"{r.Key}={r.Value}")));
        var tree = scene.Instances.First(row => row.Mesh.Native?.RenderStage == 0);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in tree.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, tree.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        var center = (min + max) * 0.5f;
        float radius = Vector3.Distance(min, max) * 0.5f;
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.Camera.LookFrom(center + new Vector3(radius * 2.5f, 0, radius * 0.3f), center);
        viewport.RenderImage(width, height, 1);
        Check(viewport.UseGameShaders && viewport.NativeError is null, viewport.NativeError ?? "Native rendering fell back to geometry.");
        var native = viewport.NativeRenderer!;
        native.AutoExposure = false;
        Vector4[] Frame(double seconds) => renderer.ReadTexturePixels(viewport.RenderImage(width, height, seconds), points);
        var first = Frame(20);
        int nearLod0 = native.DecoratorLodCounts[0];
        var second = Frame(21.5);
        int windPixels = first.Zip(second).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        viewport.Visibility.Filter = item => item is not MeshInstance part || part.NativeInstanceData.Length != DecoratorRecords.Size;
        var bare = Frame(20);
        viewport.Visibility.Filter = null;
        int treePixels = first.Zip(bare).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        Check(native.LastFrame.FailedParts == 0 && native.DecoratorWindFallback is null && nearLod0 > 0 && treePixels > 1000
            && windPixels > 0, $"Near decorators: LOD 0 {nearLod0}, tree pixels {treePixels}, wind pixels {windPixels}, "
            + $"failed {native.LastFrame.FailedParts}, wind '{native.DecoratorWindFallback}'.");
        viewport.Camera.LookFrom(center + new Vector3(90, 0, radius * 0.3f), center);
        Frame(20);
        int midLod0 = native.DecoratorLodCounts[0], midOther = native.DecoratorLodCounts.Skip(1).Sum();
        // The billboard is the model's last mesh; it alone takes the last LOD level.
        var decorators = plan.Groups.SelectMany(group => group.Parts).Where(part => part.Decorator is not null).ToArray();
        int billboardLevel = decorators.Max(part => part.Decorator!.FirstLevel);
        var billboards = decorators.Where(part => part.Decorator!.FirstLevel == billboardLevel)
            .Select(part => part.Instance).ToHashSet();
        viewport.Camera.LookFrom(center + new Vector3(400, 0, radius * 0.3f), center);
        var far = Frame(20);
        int farLod0 = native.DecoratorLodCounts[0], farBillboards = native.DecoratorLodCounts[billboardLevel];
        viewport.Visibility.Filter = item => item is not MeshInstance part || !billboards.Contains(part);
        var noBillboards = Frame(20);
        viewport.Visibility.Filter = null;
        int billboardPixels = far.Zip(noBillboards).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        Check(midLod0 == 0 && midOther > 0 && farLod0 == 0 && farBillboards > 0 && billboardPixels > 0,
            $"Decorator LOD by distance: at 90 LOD 0 {midLod0} and others {midOther}; at 400 LOD 0 {farLod0}, "
            + $"billboards {farBillboards}, billboard pixels {billboardPixels}.");
        Console.WriteLine($"decorator_draw adapter={(warp ? "warp" : "hardware")} prepared={colour}/{stage0} casters={plan.PreparedPartCount - colour}/{stage3} "
            + $"near_lod0={nearLod0} tree_pixels={treePixels} wind_pixels={windPixels} mid_other_lods={midOther} "
            + $"billboard_level={billboardLevel} far_billboards={farBillboards} billboard_pixels={billboardPixels} result=pass");
    }

    /** Extern 47 of the ground type for one wind direction and strength. */
    private static void GroundWind(NativeDecoratorType type, Vector3 direction, float strength, Vector4 expected)
    {
        Near(NativeDecorators.GroundWind(type, direction, strength), expected, "extern 47");
        Console.WriteLine($"ground_decorator_wind type={type.Tag:X8} reference=file result=pass");
    }

    /** Ground payload 80B50087: every cell part prepares and draws, its LOD follows the band distances, and the wind of the map that places it moves it. */
    private static void Ground(PackageCatalog catalog, bool warp)
    {
        const int width = 320, height = 240;
        var scene = new AssetSceneLoader(catalog).Load(GroundSceneTag);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 256L * 1024 * 1024);
        var cells = scene.Instances.Where(row => row.Mesh.Native?.RenderStage == 0
            && NativeStencil.PathOf(row) == NativeStencilPath.Decorator).ToArray();
        var prepared = plan.Groups.Where(group => group.RenderStage != NativeScenePlan.ShadowStage)
            .SelectMany(group => group.Parts).Where(part => part.Decorator?.Payload.Ground == true).ToArray();
        Check(cells.Length > 0 && prepared.Length == cells.Length, $"{prepared.Length} of {cells.Length} ground decorator parts prepared: "
            + string.Join("; ", plan.SkippedReasons.Select(r => $"{r.Key}={r.Value}")));
        var cell = prepared[0].Instance;
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in cell.Mesh.Vertices)
        {
            var point = Vector3.Transform(vertex.Position, cell.Transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        var center = (min + max) * 0.5f;
        float radius = Vector3.Distance(min, max) * 0.5f;
        var points = Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x => (x, y))).ToArray();
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, warp);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        viewport.Camera.LookFrom(center + new Vector3(radius * 2.5f, 0, radius * 0.3f), center);
        viewport.RenderImage(width, height, 1);
        Check(viewport.UseGameShaders && viewport.NativeError is null, viewport.NativeError ?? "Native rendering fell back to geometry.");
        var native = viewport.NativeRenderer!;
        native.AutoExposure = false;
        Vector4[] Frame(double seconds = 20) => renderer.ReadTexturePixels(viewport.RenderImage(width, height, seconds), points);
        var near = Frame();
        int nearLod0 = native.DecoratorLodCounts[0], failed = native.LastFrame.FailedParts;
        int windPixels = near.Zip(Frame(21.5)).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        viewport.Visibility.Filter = item => item is not MeshInstance part || NativeStencil.PathOf(part) != NativeStencilPath.Decorator;
        var bare = Frame();
        viewport.Visibility.Filter = null;
        int pixels = near.Zip(bare).Count(pair => Vector4.Distance(pair.First, pair.Second) > .01f);
        Check(failed == 0 && nearLod0 > 0 && pixels > 1000 && native.DecoratorWindFallback is null && windPixels > 0,
            $"Near ground decorators: LOD 0 {nearLod0}, pixels {pixels}, wind pixels {windPixels}, failed {failed}, "
            + $"wind '{native.DecoratorWindFallback}'.");
        viewport.Camera.LookFrom(center + new Vector3(155, 0, radius * 0.3f), center);
        Frame();
        int midLod0 = native.DecoratorLodCounts[0], midOther = native.DecoratorLodCounts.Skip(1).Sum();
        viewport.Camera.LookFrom(center + new Vector3(400, 0, radius * 0.3f), center);
        Frame();
        int far = native.DecoratorLodCounts.Sum();
        Check(midLod0 == 0 && midOther > 0 && far == 0,
            $"Ground decorator LOD by distance: at 155 LOD 0 {midLod0} and others {midOther}; at 400 {far}.");
        Console.WriteLine($"ground_decorator_draw adapter={(warp ? "warp" : "hardware")} prepared={prepared.Length}/{cells.Length} "
            + $"near_lod0={nearLod0} pixels={pixels} wind_pixels={windPixels} mid_other_lods={midOther} far={far} "
            + $"wind='{prepared[0].Decorator!.Payload.WindSource}' result=pass");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
