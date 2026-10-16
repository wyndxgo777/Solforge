namespace SunriseStudio.Interchange;

/** Recipes are pinned to pixel bytecode; unrelated shaders never inherit their slot mappings. */
internal static class MaterialPreviewRecipes
{
    public const string WaterRecipe = "Water";
    // Calm water reflects almost like a mirror; the game's ripple normals carry the detail.
    private const float WaterRoughness = .05f;
    // Depth gradient coordinate for a shallow pool; the shader reads the scene depth there.
    private const float WaterGradientDepth = .5f;
    // In-scatter colour for a water shader with no pinned colour rows.
    private static readonly float[] DefaultWaterScatter = [.01f, .06f, .08f];

    /**
     * Water draws opaque past a few centimetres of depth, so it is a smooth surface lit by its in-scatter colour.
     * Its normal is the shader's reflect() normal; refraction, depth fades and screen reflections are left out.
     */
    internal static BundleMaterialPreview Water(BundleMaterialPreview preview)
    {
        var nodes = preview.Program?.Nodes.ToList() ?? [];
        var outputs = new Dictionary<string, int[]>();
        if (preview.Program?.Outputs.TryGetValue("Normal", out var normal) == true) outputs["Normal"] = normal;
        int Add(BundleShaderNode node) { nodes.Add(node); return nodes.Count - 1; }
        int Constant(float value) => Add(new("constant", [], value));
        int Mul(int a, int b) => Add(new("mul", [a, b]));
        float? Row(int row, int lane) => preview.PixelConstants is { } rows && row < rows.Length
            && preview.PixelConstantMasks?[row] is uint mask && (mask & 1u << lane) != 0 ? rows[row][lane] : null;
        int[]? scatter = null;
        bool pinned = true;
        switch (preview.ShaderSha256)
        {
            // The t6 depth gradient at mid depth, times row 36, lit by 1 + row 33. Blender has no scene depth.
            case "C8C2D4929187D40D4C30B87D801EE595A7A5B6B58F781FB672B494CCEBEE6E5C"
                when Enumerable.Range(0, 3).All(lane => Row(36, lane) is not null && Row(33, lane) is not null)
                    && preview.TextureFormats?.ContainsKey(6) == true:
                int u = Constant(WaterGradientDepth), v = Constant(.5f);
                scatter = Enumerable.Range(0, 3).Select(lane => Mul(Add(new("sample", [u, v], Slot: 6, Channel: lane) { SamplerSlot = 9 }),
                    Constant(Row(36, lane)!.Value * (1 + Row(33, lane)!.Value)))).ToArray();
                break;
            // Row 93 lit by row 90 plus an unshadowed sun term of 1.
            case "E841CDF8C8FDAE233D2D062F725F8B78800397AB035278A681F7791B867D7DA5"
                when Enumerable.Range(0, 3).All(lane => Row(93, lane) is not null && Row(90, lane) is not null):
                scatter = Enumerable.Range(0, 3).Select(lane => Constant(Row(93, lane)!.Value * (1 + Row(90, lane)!.Value))).ToArray();
                break;
            default: pinned = false; break;
        }
        scatter ??= DefaultWaterScatter.Select(Constant).ToArray();
        int zero = Constant(0);
        outputs["BaseColor"] = [zero, zero, zero];
        outputs["Emission"] = scatter;
        outputs["Metallic"] = [zero];
        outputs["Roughness"] = [Constant(WaterRoughness)];
        var notes = preview.Notes.Where(note => note != MaterialPreviewAnalyzer.UnresolvedBlendNote)
            .Append("Water is an opaque smooth surface lit by its in-scatter colour; refraction and shoreline fades are left out.")
            .Append(outputs.ContainsKey("Normal") ? "Water ripples use the shader's normal; runtime ripple and shadow maps are flat."
                : "Water ripples are not resolved; the surface is flat.");
        if (!pinned) notes = notes.Append("This water shader has no pinned colour; the in-scatter colour is a default.");
        return preview with
        {
            Recipe = WaterRecipe, Mode = "Water surface approximation", Bindings = [], BaseColor = [0, 0, 0, 1],
            Metallic = 0, Roughness = WaterRoughness, Alpha = 1, Emission = [.. DefaultWaterScatter],
            Program = new(nodes, outputs), Notes = notes.ToArray()
        };
    }

    /** Applies the recipe for a known shader (by hash) to a preview; other shaders are returned unchanged. */
    internal static BundleMaterialPreview Apply(BundleMaterialPreview preview)
    {
        var recipe = preview.ShaderSha256 switch
        {
            "FA1AEE2E02AD12913E38900964276B60AF3B7B5FF4E6DDE310201E40CF257193" => "VexDye49",
            "11ABFA455782372D9087C14457A5923EF55E771BD81337CE2B45E3BF01641035" => "VexDye49",
            "DD432AE242C7D6714AC187488148555806DC0D929AB54CAC03BE97D782909E24" => "VexDye46",
            "BFF2280B7DCA0AEF2531107E3A1E9B9D7599139AD3575B91C172BAFE7A82E467" => "VexDye46",
            "421D0C5427EA846E99BF7B029E98406085E90EA67B17C68FCA890C2DA9D4CF98" => "VexDye47",
            "B96DBD234F84D2A182A7C531510632756C4FA8991342C08714DC6DCA2824C833" => "VexDye47",
            // VexDye46 with a patina mask at t0, as on miniboss and ultra Vex; the second also reads v5, which is zero here.
            "A89C2C3029CB59E9160235DC1D852ECAC214B286678BB835B811FB9C6733EC85" => "VexDye52",
            "C605AECBB9E6E5FBBFFCA470A815664073050B185E7D0D5A9BC534EEEAF66A36" => "VexDye52",
            _ => null
        };
        if (recipe is null || preview.PixelConstants is null) return preview;
        int count = recipe switch { "VexDye49" => 49, "VexDye46" => 46, "VexDye47" => 47, _ => 52 };
        if (preview.PixelConstants.Length < count) return preview;
        return preview with
        {
            Recipe = recipe,
            Mode = "Authored shader recipe",
            Notes = preview.Notes.Concat(["Material constants use Studio's inspection defaults at time zero. Unavailable vertex inputs use zero; the global emission exposure factor uses one. Scene lighting remains approximate."]).ToArray()
        };
    }
}
