using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** Checks the animation palette shader code and its bit-packing against expected disassembly. */
internal static class PaletteChecks
{
    /** The palette form follows the index scale on the lanes an instruction writes. */
    public static void Forms()
    {
        (string Text, NativeEntityPalette Form)[] cases =
        [
            ("  ishl r0.zw, v1.xxxy, l(0, 0, 1, 1)", NativeEntityPalette.DualQuaternion),
            ("ishl r1.xy, v1.xyxx, l(1, 1, 0, 0)", NativeEntityPalette.DualQuaternion),
            ("ishl r0.xyzw, v2.xyzw, l(1, 1, 1, 1)", NativeEntityPalette.DualQuaternion),
            ("ishl r0.x, v1.x, l(1)", NativeEntityPalette.DualQuaternion),
            // Unwritten lanes hold 1 and written lanes hold 0: no doubling.
            ("ishl r0.zw, v1.xxxy, l(1, 1, 0, 0)", NativeEntityPalette.None),
            // Four rows per bone is a separate form.
            ("ishl r1.xy, v1.xyxx, l(2, 2, 0, 0)", NativeEntityPalette.None),
            ("imul null, r1.xyzw, v2.xyzw, l(3, 3, 3, 3)", NativeEntityPalette.Matrix),
            ("imad r4.xyzw, l(3, 3, 3, 3), v2.xxyy, l(1, 2, 1, 2)", NativeEntityPalette.Matrix),
            ("imad r0.zw, v2.xxxy, l(0, 0, 3, 3), l(0, 0, 1, 1)", NativeEntityPalette.Matrix),
            // A literal 3 that is only added does not scale the index.
            ("imad r0.x, v2.x, l(2), l(3)", NativeEntityPalette.None),
            ("ishl r0.zw, v1.xxxy, l(0, 0, 1, 1)\nimul null, r1.xy, v2.xyxx, l(3, 3, 0, 0)", NativeEntityPalette.None)
        ];
        foreach (var (text, form) in cases)
        {
            var actual = NativeShaderContracts.ClassifyPaletteIndex(text);
            if (actual != form) throw new InvalidDataException($"'{text}' classified {actual}, expected {form}.");
        }
        Console.WriteLine($"palette_forms cases={cases.Length} result=pass");
    }

    /** Installed shaders keep their form, and each listed part of 0x80C19B1F draws natively. */
    public static void Installed(string gameRoot)
    {
        using var catalog = PackageCatalog.Open(gameRoot);
        var reader = new GameMaterialReader(catalog);
        (uint Material, uint Shader, NativeEntityPalette Form)[] materials =
        [
            (0x815B2522, 0x815B91C9, NativeEntityPalette.DualQuaternion),
            (0x80FEE1DE, 0x815B91C9, NativeEntityPalette.DualQuaternion),
            (0x815B251F, 0x815B91CF, NativeEntityPalette.DualQuaternion),
            (0x80FEE4DD, 0x80C7019A, NativeEntityPalette.DualQuaternion),
            (0x80FEE385, 0x80C22C0A, NativeEntityPalette.DualQuaternion),
            (0x80A5C1FF, 0x80A5C1F9, NativeEntityPalette.Matrix),
            (0x80A5C54E, 0x80A5C54C, NativeEntityPalette.None)
        ];
        foreach (var (material, shader, form) in materials)
        {
            var vs = reader.Read(material).VertexShader ?? throw new InvalidDataException($"Material {material:X8} has no VS.");
            var actual = NativeShaderContracts.ClassifyEntityPalette(reader.ReadShaderBytecode(vs), vs.Disassembly);
            if (vs.HeaderTag != shader || actual != form)
                throw new InvalidDataException($"Material {material:X8} VS {vs.HeaderTag:X8} classified {actual}, expected {shader:X8} {form}.");
        }
        var scene = new AssetSceneLoader(catalog) { IncludeLowerLods = true }.Load(0x80C19B1F);
        var plan = AssetViewport.PrepareNativeScene(catalog, scene, textureBudgetBytes: 128L * 1024 * 1024);
        // 10192 and 10256 read extern 1 float 28.
        (int Part, int Lod)[] parts = [(8784, 2), (8816, 0), (10192, 0), (10256, 2)];
        foreach (var (part, lod) in parts)
        {
            var instance = scene.Instances.FirstOrDefault(i => i.Mesh.PartIndex == part && i.Mesh.LodLevel == lod)
                ?? throw new InvalidDataException($"Part {part} at LOD {lod} of 80C19B1F is missing.");
            if (!plan.Groups.Any(g => g.Parts.Any(p => p.Instance == instance)))
                throw new InvalidDataException($"Part {part} of 80C19B1F was not prepared: {string.Join("; ", plan.Diagnostics)}");
            DrawsNatively(catalog, instance);
        }
        Console.WriteLine($"palette_installed materials={materials.Length} asset=80C19B1F "
            + $"parts={string.Join(",", parts.Select(p => p.Part))} native=yes result=pass");
    }

    /** Draws one part alone on WARP; it must take the game shader with no fallback part. */
    private static void DrawsNatively(PackageCatalog catalog, MeshInstance instance)
    {
        var scene = new SceneDocument();
        scene.Instances.Add(instance);
        using var renderer = new Dx11Renderer(IntPtr.Zero, 64, 64, true);
        using var viewport = new AssetViewport(renderer) { ShowGrid = false, ShowCursor = false, Catalog = catalog };
        viewport.SetScene(scene);
        viewport.SetNativeScene(AssetViewport.PrepareNativeScene(catalog, scene, assetPreview: true));
        viewport.Frame();
        viewport.RenderImage(64, 64);
        if (viewport.NativePartCount != 1 || viewport.NativeFallbackPartCount != 0 || viewport.NativeError is not null)
            throw new InvalidDataException($"Part {instance.Mesh.PartIndex} drew native={viewport.NativePartCount} "
                + $"fallback={viewport.NativeFallbackPartCount}: {viewport.NativeError}");
    }
}
