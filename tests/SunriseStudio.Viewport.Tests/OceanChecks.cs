using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** The ocean: extern 91 settings by map, its TFX, and the Deep Six and Titan plans. */
internal static class OceanChecks
{
    private const uint DeepSix = 0x81508008, Titan = 0x80F3366D, Eternity = 0x80F92002;
    private const uint DefaultSettings = 0x80B35E34, TitanSettings = 0x80BF74EF;
    private const uint OceanMaterial = 0x8158BC49, OceanVertexOnly = 0x80F987DA;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string install)
    {
        Absolute();
        using var catalog = PackageCatalog.Open(install, cacheOptions: new() { Enabled = false });
        Settings(catalog);
        Programs(catalog);
        Plan(catalog, DeepSix, DefaultSettings);
        Plan(catalog, Titan, TitanSettings);
    }

    /** Opcode 0x15 is MAXPS(0 - x, x): NaN and -0 keep x. */
    private static void Absolute()
    {
        var stage = new MaterialStageData(GameShaderStage.Pixel, null, [0x34, 0x00, 0x15, 0x43, 0x00],
            [new(-1.5f, 2, -0f, float.NaN)], [Vector4.Zero], 0, []);
        var value = TfxEvaluator.Evaluate(stage, new TfxInputs()).Constants[0];
        Check(value.X == 1.5f && value.Y == 2 && BitConverter.SingleToInt32Bits(value.Z) == BitConverter.SingleToInt32Bits(-0f)
            && float.IsNaN(value.W), $"TFX opcode 0x15 gives {value}.");
        Check(TfxProgramInspection.Read([0x34, 0x00, 0x15, 0x43, 0x00]).FullyDecoded, "Inspection stops at opcode 0x15.");
        Console.WriteLine("ocean_absolute result=pass");
    }

    /** Fleet's name table names Titan's settings; every other map takes the render globals default. */
    private static void Settings(PackageCatalog catalog)
    {
        var titan = NativeOceanSettings.Resolve(catalog, Titan, NativeScenePlan.RenderGlobalsTag);
        var deepSix = NativeOceanSettings.Resolve(catalog, DeepSix, NativeScenePlan.RenderGlobalsTag);
        var eternity = NativeOceanSettings.Resolve(catalog, Eternity, NativeScenePlan.RenderGlobalsTag);
        var unplaced = NativeOceanSettings.Resolve(catalog, OceanMaterial, NativeScenePlan.RenderGlobalsTag);
        Check(titan is { Tag: TitanSettings, WaveNoise: 0x80C14BE3, DetailNoise: 0x80C14BE6 }
            && titan.Floats[6] == -15 && titan.Floats[7] == 22 && titan.Floats[8] == 700,
            $"Titan resolves {titan?.Tag:X8}.");
        Check(deepSix?.Tag == DefaultSettings && deepSix.Floats[6] == 0 && deepSix.Floats[8] == 600
            && eternity?.Tag == DefaultSettings && unplaced?.Tag == DefaultSettings,
            $"Deep Six resolves {deepSix?.Tag:X8}, Eternity {eternity?.Tag:X8}, a material {unplaced?.Tag:X8}.");
        // 0x80C617B5 unsets both noise handles, so the game rejects it.
        Check(NativeOceanSettings.Read(catalog, 0x80C617B5) is null, "Settings with unset noise read as usable.");
        Check(!titan!.Floats.ContainsKey(5), "Extern 91 float 5 has no reader and must stay unset.");
        Console.WriteLine($"ocean_settings titan={titan.Tag:X8} deep_six={deepSix!.Tag:X8} eternity={eternity!.Tag:X8} result=pass");
    }

    /** The vertex programs complete with extern 91; the base height is float 6. */
    private static void Programs(PackageCatalog catalog)
    {
        var reader = new GameMaterialReader(catalog);
        var settings = NativeOceanSettings.Resolve(catalog, Titan, NativeScenePlan.RenderGlobalsTag)!;
        var inputs = NativeScenePlan.MaterialInputs(10, reader.ReadTfxParameterDefaults(NativeScenePlan.RenderGlobalsTag).CreateSnapshot());
        Check(!TfxEvaluator.Evaluate(reader.ReadExecution(OceanMaterial).Vertex, inputs).IsComplete,
            "The ocean vertex program completes without extern 91.");
        NativeOcean.Apply(inputs, settings);
        var ocean = reader.ReadExecution(OceanMaterial);
        var vertex = TfxEvaluator.Evaluate(ocean.Vertex, inputs);
        vertex.RequireComplete();
        Check(vertex.Constants[5].X == -15 && vertex.Constants[3].X == 700 && vertex.Constants[2].X == 22,
            $"Ocean vertex constants are {vertex.Constants[2]}, {vertex.Constants[3]}, {vertex.Constants[5]}.");
        Check(vertex.ResourceBindings.Values.Contains(NativeOcean.WaveNoise), "The wave noise is not bound.");
        TfxEvaluator.Evaluate(reader.ReadExecution(OceanVertexOnly).Vertex, inputs).RequireComplete();
        Check(NativeOcean.Reads(ocean) && !NativeOcean.Reads(reader.ReadExecution(0x815B839E)),
            "Extern 91 readers are misclassified.");
        Console.WriteLine($"ocean_programs height={vertex.Constants[5].X} wavelength={vertex.Constants[3].X} result=pass");
    }

    /** The map's ocean parts validate and draw without a failed part. */
    private static void Plan(PackageCatalog catalog, uint map, uint expected)
    {
        var scene = new AssetSceneLoader(catalog).Load(map);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 256L * 1024 * 1024);
        var settings = NativeOceanSettings.Resolve(catalog, map, NativeScenePlan.RenderGlobalsTag)!;
        var ocean = plan.Groups.Where(group => NativeOcean.Reads(group.Material.Execution)).ToArray();
        var refusals = plan.StageSkippedReasons.Keys.Where(reason => reason.Contains("extern 91")).ToArray();
        Check(settings.Tag == expected && ocean.Length != 0 && refusals.Length == 0,
            $"{map:X8}: settings {settings.Tag:X8}, {ocean.Length} ocean groups, refusals {string.Join("; ", refusals)}.");
        Check(plan.Textures.TryGetValue(settings.WaveNoise, out var noise) && noise.IsVolume,
            "The wave noise did not decode as a 3D texture.");
        const int width = 320, height = 180;
        using var renderer = new Dx11Renderer(IntPtr.Zero, width, height, false);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false };
        viewport.SetScene(scene);
        viewport.SetNativeScene(plan);
        // Looks down at the sea from 60 above its base height.
        float sea = settings.Floats[6];
        viewport.Camera.LookFrom(new(0, -400, sea + 60), new(200, 0, sea));
        viewport.RenderImage(width, height);
        viewport.RenderImage(width, height);
        Check(viewport.NativeError is null && viewport.NativeRenderer!.LastFrame.FailedParts == 0,
            viewport.NativeError ?? $"{viewport.NativeRenderer!.LastFrame.FailedParts} parts failed.");
        Console.WriteLine($"ocean_plan map={map:X8} settings={settings.Tag:X8} groups={ocean.Length} result=pass");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
