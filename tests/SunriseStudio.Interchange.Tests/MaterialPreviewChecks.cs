using System.Numerics;
using System.Text.Json.Nodes;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/** Checks that material preview and export share inputs (object, parameters, clock, camera), texture slots/channels and stage blends. */
internal static class MaterialPreviewChecks
{
    /** Shader component flow, not slot ordering, defines each preview input. */
    internal static void Synthetic()
    {
        var parameters = TfxParameterLookup.FromValues([new(.2f, .3f, .4f, .5f)]);
        var inspection = NativeInspectionMaterialInputs.Create(2.5, parameters);
        var objectStage = new MaterialStageData(GameShaderStage.Pixel, null, [0x4D, 0, 0x43, 0], [], [Vector4.Zero], 0, []);
        var parameterStage = objectStage with { TfxBytecode = [0x4E, 0, 0x43, 0] };
        Check(TfxEvaluator.Evaluate(objectStage, inspection).Constants[0] == Vector4.One
            && TfxEvaluator.Evaluate(parameterStage, inspection).Constants[0] == new Vector4(.2f, .3f, .4f, .5f)
            && inspection.Floats[new(1, 0)] == 2.5f && inspection.Matrices.Count > 0,
            "Export and Studio inspection inputs do not share object, parameter, clock and camera values.");
        const string shader = """
            ps_5_0
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            sample_indexable(texture2d)(float,float,float,float) r0.xyz, v3.xyxx, t7.xyzw, s1
            mov o0.xyz, r0.xyzx
            sample_indexable(texture2d)(float,float,float,float) r1.xy, v3.xyxx, t2.xyzw, s1
            dp2 r1.z, r1.xyxx, r1.xyxx
            sample_indexable(texture2d)(float,float,float,float) r2.xyzw, v3.xyxx, t5.zxyw, s1
            mov o2.x, r2.y
            mad r2.z, r2.z, l(0.125000), l(0.375000)
            mul o0.w, r2.y, l(0.200000)
            """;
        var result = MaterialPreviewAnalyzer.Analyze(shader, 1, "fixture",
            [new(2, 12, 98), new(5, 15, 98), new(7, 17, 99)]);
        Check(result.Bindings.Single(value => value.Role == "BaseColor") is { Slot: 7, Srgb: true }, "Base colour follows the wrong slot.");
        Check(result.Bindings.Single(value => value.Role == "Normal") is { Slot: 2, Channels: "rg", ReconstructNormalZ: true }, "Normal reconstruction source differs.");
        Check(result.Bindings.Single(value => value.Role == "Metallic") is { Slot: 5, Channels: "r" }, "Metal source swizzle differs.");
        Check(result.Bindings.Single(value => value.Role == "Roughness") is { Slot: 5, Channels: "g", Scale: -1, Bias: 1 }, "Smoothness inversion differs.");
        Check(result.Bindings.All(value => value.Role != "Alpha"), "Packed G-buffer alpha became opacity.");
        string masked = shader.Replace("dp2 r1.z", "sample_indexable(texture2d)(float,float,float,float) r4.xy, v3.xyxx, t0.xyzw, s1\nadd r1.xy, r1.xyxx, r4.xxxx\nadd r1.xy, r1.xyxx, r4.yyyy\ndp2 r1.z", StringComparison.Ordinal);
        var maskedResult = MaterialPreviewAnalyzer.Analyze(masked, 1, "fixture",
            [new(0, 10, 98), new(2, 12, 98), new(5, 15, 98), new(7, 17, 99)]);
        Check(maskedResult.Bindings.Single(value => value.Role == "Normal").Slot == 2, "A scalar mask became a normal map.");
        const string inverse = """
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            lt r0.x, l(0.000000), cb0[0].x
            if_nz r0.x
            add_sat r0.x, l(1.000000), -cb0[0].x
            mul r0.yzw, cb0[0].xxxx, cb0[1].xxyz
            else
            mov r0.xyzw, l(1.000000,0,0,0)
            endif
            mov_sat r1.x, cb0[2].y
            mul o0.xyz, r0.yzwy, r1.xxxx
            add r0.x, r0.x, l(-1.000000)
            mad o0.w, r1.x, r0.x, l(1.000000)
            """;
        var transparent = MaterialPreviewAnalyzer.Analyze(inverse, 2, "fixture", [],
            [new Vector4(.25f, 0, 0, 0), new(.2f, .4f, .6f, 0), new(0, .5f, 0, 0)], "InverseAlpha");
        Check(Math.Abs(transparent.Alpha - .125f) < 1e-6 && Math.Abs(transparent.BaseColor[0] - .025f) < 1e-6,
            "Inverse-alpha material lost its premultiplied colour or opacity.");
        const string cutout = """
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            sample_indexable(texture2d)(float,float,float,float) r0.xyzw, v3.xyxx, t0.xyzw, s1
            lt r1.x, r0.w, l(0.400000)
            discard_nz r1.x
            mad o0.xyz, r0.xyzx, cb0[0].xyzx, cb0[1].xyzx
            mov r2.xyzw, l(0,0,0,1.#INF00)
            mov o2.x, r2.x
            """;
        var expression = MaterialPreviewAnalyzer.Analyze(cutout, 3, "fixture", [new(0, 10, 98)],
            [new Vector4(.5f), new Vector4(.1f)]).Program!;
        Check(expression is not null && expression.Outputs.ContainsKey("BaseColor")
            && expression.Outputs.ContainsKey("Visibility"), "Shader expressions lost colour or cutout outputs.");
        float Evaluate(int index, float alpha)
        {
            var node = expression!.Nodes[index];
            float[] values = node.Inputs.Select(value => Evaluate(value, alpha)).ToArray();
            return node.Operation switch
            {
                "constant" => node.Value, "uv" => .5f, "sample" => node.Channel == 3 ? alpha : .6f,
                "add" => values[0] + values[1], "mul" => values[0] * values[1], "neg" => -values[0],
                "mad" => values[0] * values[1] + values[2], "min" => Math.Min(values[0], values[1]),
                "max" => Math.Max(values[0], values[1]), "lt" => values[0] < values[1] ? 1 : 0,
                "ne" => values[0] != values[1] ? 1 : 0, _ => throw new InvalidOperationException(node.Operation)
            };
        }
        Check(Evaluate(expression!.Outputs["Visibility"][0], .2f) == 0
            && Evaluate(expression.Outputs["Visibility"][0], .8f) == 1
            && Math.Abs(Evaluate(expression.Outputs["BaseColor"][0], .8f) - .4f) < .00001f,
            "Shader palette or alpha-test arithmetic changed.");
        Check(expression.Nodes[expression.Outputs["Metallic"][0]].Value == 0,
            "Unused nonfinite shader lanes leaked into metalness.");
        var distinctSamplers = MaterialPreviewAnalyzer.Analyze("""
            dcl_output o0.xyzw
            sample_indexable(texture2d)(float,float,float,float) r0.xyz, v3.xyxx, t0.xyzw, s1
            sample_indexable(texture2d)(float,float,float,float) r1.xyz, v3.xyxx, t0.xyzw, s2
            add o0.xyz, r0.xyzx, r1.xyzx
            """, 1, "fixture", [new(0, 10, 98)]).Program!;
        Check(distinctSamplers.Nodes.Where(node => node.Operation == "sample").Select(node => node.SamplerSlot)
            .Distinct().Order().SequenceEqual(new uint?[] { 1, 2 }), "Different sampler states shared one expression sample.");
        Console.WriteLine("material_preview slots=flow swizzles=checked packed_alpha=excluded inverse_alpha=checked");
        StageBlends();
        TimeAndView();
        GearInputs();
        TerrainInputs();
        ControlFlow();
        ShaderLinks();
    }

    /** Loops unroll, a switch becomes a selection, and nonfinite constants fold as Direct3D evaluates them. */
    private static void ControlFlow()
    {
        float Evaluate(BundleMaterialProgram program, int index, float u)
        {
            var node = program.Nodes[index];
            float[] values = node.Inputs.Select(value => Evaluate(program, value, u)).ToArray();
            return node.Operation switch
            {
                "constant" => node.Value, "uv" => node.Channel == 0 ? u : 0, "add" => values[0] + values[1],
                "mul" => values[0] * values[1], "mad" => values[0] * values[1] + values[2], "neg" => -values[0],
                "min" => Math.Min(values[0], values[1]), "max" => Math.Max(values[0], values[1]), "round_z" => MathF.Truncate(values[0]),
                "eq" => values[0] == values[1] ? 1 : 0, "ge" => values[0] >= values[1] ? 1 : 0, "lt" => values[0] < values[1] ? 1 : 0,
                "movc" => values[0] != 0 ? values[1] : values[2], _ => throw new InvalidOperationException(node.Operation)
            };
        }
        float Color(BundleMaterialPreview preview, float u) => Evaluate(preview.Program!, preview.Program!.Outputs["BaseColor"][0], u);
        var fixedLoop = MaterialPreviewAnalyzer.Analyze("""
            dcl_output o0.xyzw
            mov r0.x, l(0)
            mov r1.x, l(0)
            loop
              itof r1.y, r1.x
              ge r1.y, r1.y, l(3.000000)
              breakc_nz r1.y
              add r0.x, r0.x, v3.x
              iadd r1.x, r1.x, l(1)
            endloop
            mov o0.xyz, r0.xxxx
            mov o0.w, l(1.000000)
            """, 1, "fixture", []);
        Check(Math.Abs(Color(fixedLoop, .25f) - .75f) < 1e-6f && !fixedLoop.Notes.Contains(MaterialPreviewAnalyzer.ApproximateLoopNote),
            "A loop with a constant trip count did not unroll exactly.");
        var pixelLoop = MaterialPreviewAnalyzer.Analyze("""
            dcl_output o0.xyzw
            ftoi r2.x, v3.x
            mov r0.x, v3.x
            mov r1.x, l(0)
            loop
              ige r1.y, r1.x, r2.x
              breakc_nz r1.y
              add r0.x, r0.x, l(1.000000)
              iadd r1.x, r1.x, l(1)
            endloop
            mov o0.xyz, r0.xxxx
            mov o0.w, l(1.000000)
            """, 1, "fixture", []);
        Check(Math.Abs(Color(pixelLoop, 3.5f) - 3.5f) < 1e-6f && pixelLoop.Notes.Contains(MaterialPreviewAnalyzer.ApproximateLoopNote),
            "A loop whose exit depends on the pixel did not stop at that exit with an approximation note.");
        var selection = MaterialPreviewAnalyzer.Analyze("""
            dcl_output o0.xyzw
            ftoi r0.x, v3.x
            switch r0.x
              case l(0)
              mov r1.x, l(0.250000)
              break
              case l(1)
              case l(2)
              mov r1.x, l(0.500000)
              break
              default
              mov r1.x, l(0.750000)
              break
            endswitch
            mov o0.xyz, r1.xxxx
            mov o0.w, l(1.000000)
            """, 1, "fixture", []);
        Check(new[] { .5f, 1.5f, 2.5f, 3.5f }.Select(u => Color(selection, u)).SequenceEqual([.25f, .5f, .5f, .75f]),
            "A switch on an integer UV did not select its case values.");
        var unbounded = MaterialPreviewAnalyzer.Analyze("""
            dcl_output o0.xyzw
            mad_sat r0.x, v3.x, cb0[0].x, cb0[0].y
            log r0.y, l(0.000000)
            exp r0.y, r0.y
            add r0.y, r0.y, v3.x
            mov o0.xyz, r0.xyyx
            mov o0.w, l(1.000000)
            """, 1, "fixture", [], [new(float.PositiveInfinity, float.NegativeInfinity, 0, 0)]);
        var lanes = unbounded.Program!.Outputs["BaseColor"];
        Check(unbounded.Program.Nodes[lanes[0]] is { Operation: "constant", Value: 0 } && Evaluate(unbounded.Program, lanes[1], .5f) == .5f,
            "Saturated infinite constants or a finite fold of an infinite one did not resolve.");
        Console.WriteLine("material_preview loop_exact=checked loop_pixel_exit=approximate switch=checked nonfinite_fold=checked");
    }

    /** Terrain shaders read screen derivatives of the world position, integer signs and vertex-computed blend weights. */
    private static void TerrainInputs()
    {
        float Evaluate(BundleMaterialProgram program, int index, Vector3 position, Vector3 normal)
        {
            var node = program.Nodes[index];
            float[] values = node.Inputs.Select(value => Evaluate(program, value, position, normal)).ToArray();
            return node.Operation switch
            {
                "constant" => node.Value, "position" => position[node.Channel], "normal" => normal[node.Channel],
                "abs" => Math.Abs(values[0]), "neg" => -values[0], "add" => values[0] + values[1], "mul" => values[0] * values[1],
                "div" => values[0] / values[1], "lt" => values[0] < values[1] ? 1 : 0, "ne" => values[0] != values[1] ? 1 : 0,
                "mad" => values[0] * values[1] + values[2], "min" => Math.Min(values[0], values[1]),
                "max" => Math.Max(values[0], values[1]), "rsq" => 1 / MathF.Sqrt(values[0]),
                "movc" => values[0] != 0 ? values[1] : values[2], _ => throw new InvalidOperationException(node.Operation)
            };
        }
        const string derivative = """
            dcl_output o0.xyzw
            deriv_rtx_coarse r0.xz, v4.xxyx
            deriv_rty_coarse r0.yw, v4.xxxy
            mov o0.xyz, r0.xzwx
            mov o0.w, r0.y
            """;
        var footprint = MaterialPreviewAnalyzer.Analyze(derivative, 6, "fixture", [], blendMode: "Alpha", worldVaryings: true).Program!;
        Check(footprint.Outputs["BaseColor"].Select(index => footprint.Nodes[index]).Select(node => node.Value)
            .SequenceEqual([.01f, 0, .01f]) && footprint.Nodes[footprint.Outputs["Alpha"][0]].Value == 0,
            "A screen derivative of the world position is not the fixed footprint with screen x along x.");
        const string textured = """
            dcl_output o0.xyzw
            sample_indexable(texture2d)(float,float,float,float) r1.xy, v3.xyxx, t0.xyzw, s1
            deriv_rtx_coarse r0.xyz, r1.xyxx
            mov o0.xyz, r0.xyzx
            mov o0.w, l(1.000000)
            """;
        var sampled = MaterialPreviewAnalyzer.Analyze(textured, 6, "fixture", [new(0, 10, 98)], worldVaryings: true).Program;
        Check(sampled?.Outputs.ContainsKey("BaseColor") != true, "A screen derivative of a texture value resolved.");
        const string sign = """
            dcl_output o0.xyzw
            lt r0.x, l(0.000000), v4.z
            movc r0.x, r0.x, l(1), l(-1)
            itof r0.y, r0.x
            lt r1.x, l(0.000000), v4.z
            itof r0.z, r1.x
            mov o0.xyzw, r0.yyyz
            """;
        var signs = MaterialPreviewAnalyzer.Analyze(sign, 7, "fixture", [], blendMode: "Alpha", worldVaryings: true).Program!;
        int signed = signs.Outputs["BaseColor"][0];
        Check(Evaluate(signs, signed, new(0, 0, 2), Vector3.Zero) == 1 && Evaluate(signs, signed, new(0, 0, -2), Vector3.Zero) == -1,
            "itof of an integer sign select changed its value.");
        // A set mask is integer -1, so its saturated alpha is 0 on both sides.
        Check(signs.Outputs.TryGetValue("Alpha", out var maskAlpha) && Evaluate(signs, maskAlpha[0], new(0, 0, 2), Vector3.Zero) == 0
            && Evaluate(signs, maskAlpha[0], new(0, 0, -2), Vector3.Zero) == 0, "itof of a comparison mask read the mask as 1 rather than -1.");
        const string vertex = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // NORMAL                   0   xyzw        1     NONE   float   xyz
            //
            //
            // Output signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 5   xyz         5     NONE   float   xyz
            //
            vs_5_0
            mul r1.xyz, |v1.xyzx|, |v1.xyzx|
            dp3 r0.z, r1.xyzx, l(1.000000, 1.000000, 1.000000, 0.000000)
            div o5.xyz, r1.xyzx, r0.zzzz
            """;
        const string pixel = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 5   xyz         5     NONE   float   xyz
            //
            ps_5_0
            dcl_output o0.xyzw
            mov o0.xyz, v5.xyzx
            mov o0.w, l(1.000000)
            """;
        var weights = MaterialPreviewAnalyzer.Analyze(pixel, 8, "fixture", [], varyings: ShaderVaryings.Read(vertex, pixel)).Program!;
        var lanes = weights.Outputs["BaseColor"].Select(index => Evaluate(weights, index, Vector3.Zero, new(.6f, 0, -.8f))).ToArray();
        Check(Math.Abs(lanes[0] - .36f) < 1e-5f && lanes[1] == 0 && Math.Abs(lanes[2] - .64f) < 1e-5f,
            "A pixel input the vertex shader computes from NORMAL0 evaluates wrongly.");
        const string deferred = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 5   xyz         5     NONE   float   xyz
            //
            ps_5_0
            dcl_globalFlags refactoringAllowed
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            mov o0.xyzw, l(0.500000, 0.500000, 0.500000, 1.000000)
            mul r2.x, v7.x, l(0.000000)
            add o0.x, r2.x, l(0.500000)
            mad r0.w, l(0.500000), l(0.125000), l(0.375000)
            dp3 r1.x, v5.xyzx, v5.xyzx
            rsq r1.x, r1.x
            mul r0.xyz, v5.xyzx, r1.xxxx
            mad_sat o1.xyz, r0.xyzx, r0.wwww, l(0.500000, 0.500000, 0.500000, 0.000000)
            mov o2.xyzw, l(0, 0.500000, 0, 0)
            """;
        string copied = vertex.Replace("dp3 r0.z, r1.xyzx, l(1.000000, 1.000000, 1.000000, 0.000000)\ndiv o5.xyz, r1.xyzx, r0.zzzz",
            "mul o5.xyz, v1.xyzx, l(2.000000, 2.000000, 2.000000, 0.000000)", StringComparison.Ordinal);
        var shaded = MaterialPreviewAnalyzer.Analyze(deferred, 9, "fixture", [], varyings: ShaderVaryings.Read(copied, deferred)).Program!;
        var normal = shaded.Outputs["Normal"].Select(index => Evaluate(shaded, index, Vector3.Zero, new(.6f, 0, -.8f))).ToArray();
        Check(Math.Abs(normal[0] - .6f) < 1e-5f && Math.Abs(normal[1]) < 1e-5f && Math.Abs(normal[2] + .8f) < 1e-5f,
            "A G-buffer normal built on NORMAL0 did not become the Normal output.");
        Check(shaded.Nodes[shaded.Outputs["BaseColor"][0]] is { Operation: "constant", Value: .5f },
            "A shader that allows refactoring kept an unknown times zero.");
        Check(MaterialPreviewAnalyzer.Analyze(derivative, 6, "fixture", [], worldVaryings: true).Program?.Outputs.ContainsKey("Alpha") == false,
            "An opaque forward draw took its written alpha as opacity.");
        var strict = MaterialPreviewAnalyzer.Analyze(deferred.Replace("dcl_globalFlags refactoringAllowed\n", "", StringComparison.Ordinal),
            9, "fixture", [], varyings: ShaderVaryings.Read(copied, deferred)).Program!;
        Check(!strict.Outputs.ContainsKey("BaseColor"), "A shader that forbids refactoring folded an unknown times zero.");
        var reached = new HashSet<int>();
        var pending = new Stack<int>(shaded.Outputs.Values.SelectMany(values => values));
        while (pending.TryPop(out int index))
            if (reached.Add(index)) foreach (int input in shaded.Nodes[index].Inputs) pending.Push(input);
        Check(reached.Count == shaded.Nodes.Count, "A program kept nodes no output reads.");
        const string water = """
            dcl_globalFlags refactoringAllowed
            dcl_output o0.xyzw
            sample_l_indexable(texture2d)(float,float,float,float) r0.xy, v4.xyxx, t1.xyzw, s1, l(0.000000)
            sample_indexable(texture2d)(float,float,float,float) r0.zw, v4.xyxx, t2.zwxy, s2
            add r0.xy, r0.xyxx, r0.zwzz
            mad r1.xyz, v1.xyzx, r0.xxxx, v0.xyzx
            dp3 r1.w, r1.xyzx, r1.xyzx
            rsq r1.w, r1.w
            mul r1.xyz, r1.wwww, r1.xyzx
            add r2.xyz, -v4.xyzx, cb12[7].xyzx
            dp3 r0.w, r2.xyzx, r1.xyzx
            add r0.w, r0.w, r0.w
            mad r3.xyz, r1.xyzx, r0.wwww, -r2.xyzx
            sincos r4.x, r4.y, v4.x
            mov o0.xyz, r3.xyzx
            mov o0.w, r4.x
            """;
        var surface = MaterialPreviewAnalyzer.Analyze(water, 10, "fixture", [new(1, 10, 98)], blendMode: "InverseAlpha",
            worldVaryings: true, standIns: new Dictionary<uint, Vector4> { [2] = new(.5f) }, water: true).Program!;
        bool Reaches(int[] roots, Func<BundleShaderNode, bool> test)
        {
            var seen = new HashSet<int>(); var stack = new Stack<int>(roots);
            while (stack.TryPop(out int index))
            {
                if (!seen.Add(index)) continue;
                if (test(surface.Nodes[index])) return true;
                foreach (int input in surface.Nodes[index].Inputs) stack.Push(input);
            }
            return false;
        }
        Check(surface.Outputs.TryGetValue("Normal", out var waterNormal) && Reaches(waterNormal, node => node is { Operation: "sample", Slot: 1 })
            && !Reaches(waterNormal, node => node is { Operation: "sample", Slot: 2 }), "A water reflect() normal or its stand-in ripple did not resolve.");
        Check(surface.Outputs.TryGetValue("Alpha", out var waterAlpha) && Reaches(waterAlpha, node => node.Operation == "sin"),
            "sincos did not resolve its sine.");
        const string entityVertex = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // POSITION                 0   xyzw        0     NONE   float   xyzw
            // NORMAL                   0   xyz         2     NONE   float   xyz
            //
            //
            // Output signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 0   xyzw        0     NONE   float   xyzw
            // TEXCOORD                 1   xyzw        1     NONE   float   xyzw
            //
            vs_5_0
            dcl_constantbuffer CB11[24], dynamicIndexed
            mad r0.w, v0.w, l(32767.000000), l(0.100000)
            ftou r0.w, r0.w
            ishl r1.x, r0.w, l(1)
            mul r1.yzw, v2.xxyz, cb11[r1.x + 8].yyzx
            mad r1.yzw, cb11[r1.x + 8].xxyz, v2.yyzx, -r1.yyzw
            mad r1.yzw, cb11[r1.x + 8].wwww, v2.zzxy, r1.yyzw
            add r2.xyzw, cb11[r1.x + 8].xyzw, cb11[r1.x + 8].xyzw
            mad r0.xyz, -r2.zxyz, r1.wyzw, v2.xyzx
            mad r0.xyz, r2.yzxy, r1.yzwy, r0.xyzx
            mul_sat r1.y, r0.z, cb11[7].z
            add_sat o0.w, r1.y, cb11[7].w
            mov o0.xyz, r0.xyzx
            mov o1.w, v0.z
            """;
        const string entityPixel = """
            //
            // Input signature:
            //
            // Name                 Index   Mask Register SysValue  Format   Used
            // -------------------- ----- ------ -------- -------- ------- ------
            // TEXCOORD                 0   xyzw        0     NONE   float   xyzw
            // TEXCOORD                 1   xyzw        1     NONE   float      w
            //
            ps_5_0
            dcl_output o0.xyzw
            mov o0.x, v0.x
            mov o0.y, v0.w
            mov o0.z, v1.w
            mov o0.w, l(1.000000)
            """;
        var entity = MaterialPreviewAnalyzer.Analyze(entityPixel, 11, "fixture", [], blendMode: "Alpha",
            varyings: ShaderVaryings.Read(entityVertex, entityPixel)).Program!;
        var rest = entity.Outputs["BaseColor"].Select(index => entity.Nodes[index]).ToArray();
        Check(rest[0] is { Operation: "surface", Slot: 0, Channel: 0 } && rest[1] is { Operation: "constant", Value: 1 }
            && rest[2] is { Operation: "rawposition", Channel: 2 },
            "A palette vertex shader at rest did not name its world normal, row 7 term and raw POSITION0 lane.");
        Console.WriteLine("material_preview screen_footprint=checked itof=checked vertex_computed=checked normal0_normal=checked identity_fold=checked pruned=checked water_normal=checked sincos=checked entity_rest=checked");
    }

    /** Gear shaders read dye-scope buffers, mask ANDs, the screen position and the detail UV pair. */
    private static void GearInputs()
    {
        const string shader = """
            ps_5_0
            dcl_input_ps linear v3.xyzw
            dcl_input_ps_siv linear noperspective v5.xy, position
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            sample_indexable(texture2d)(float,float,float,float) r0.xyz, v3.zwzz, t3.xyzw, s1
            ge r1.x, r0.x, l(0.500000)
            and r1.y, r1.x, l(0x3f800000)
            lt r1.z, r0.y, l(0.900000)
            and r1.w, r1.x, r1.z
            mul r2.xy, v5.xyxx, cb12[12].zwzz
            mul r0.xyz, r0.xyzx, cb7[2].xyzx
            mad o0.xyz, r1.yyyy, r0.xyzx, r2.xyxx
            mov o0.w, l(1.000000)
            mov o2.x, r1.w
            mad r3.x, r0.x, l(0.125000), l(0.375000)
            """;
        var buffers = new Dictionary<uint, Vector4[]> { [7] = [Vector4.Zero, Vector4.Zero, new(.5f, .25f, 1, 0)] };
        var program = MaterialPreviewAnalyzer.Analyze(shader, 5, "fixture", [new(3, 13, 98)], [Vector4.Zero],
            worldVaryings: true, uvPairs: true, constantBuffers: buffers, detailUv: true).Program;
        Check(program is not null && program.Outputs.ContainsKey("BaseColor") && program.Outputs.ContainsKey("Metallic"),
            "Dye buffers, mask ANDs or the screen position left the gear colour unresolved.");
        float Evaluate(int index)
        {
            var node = program!.Nodes[index];
            float[] values = node.Inputs.Select(Evaluate).ToArray();
            return node.Operation switch
            {
                "constant" => node.Value, "uv" => node.Channel < 2 ? .1f : .9f, "screen" => node.Channel == 0 ? .3f : .7f,
                "sample" => node.Channel == 0 ? .6f : .5f, "mul" => values[0] * values[1], "add" => values[0] + values[1],
                "mad" => values[0] * values[1] + values[2], "ge" => values[0] >= values[1] ? 1 : 0, "lt" => values[0] < values[1] ? 1 : 0,
                "movc" => values[0] != 0 ? values[1] : values[2], _ => throw new InvalidOperationException(node.Operation)
            };
        }
        var color = program!.Outputs["BaseColor"];
        Check(Math.Abs(Evaluate(color[0]) - .6f) < 1e-5f && Math.Abs(Evaluate(color[1]) - (.5f * .25f + .7f)) < 1e-5f,
            "The dye buffer, mask select or screen position evaluates wrongly.");
        Check(Evaluate(program.Outputs["Metallic"][0]) == 1, "AND of two masks is not their product.");
        var sample = program.Nodes.First(node => node.Operation == "sample");
        Check(sample.Inputs.Select(input => program.Nodes[input]).All(node => node is { Operation: "uv", Channel: >= 2 }),
            "The detail sample does not read UV map 1.");
        var repeated = MaterialPreviewAnalyzer.Analyze(shader, 5, "fixture", [new(3, 13, 98)], [Vector4.Zero],
            worldVaryings: true, uvPairs: true, constantBuffers: buffers).Program!;
        Check(repeated.Nodes.Where(node => node.Operation == "uv").All(node => node.Channel < 2),
            "A second UV pair that repeats UV map 0 read UV map 1.");
        var plain = MaterialPreviewAnalyzer.Analyze(shader, 5, "fixture", [new(3, 13, 98)], [Vector4.Zero], worldVaryings: true).Program;
        Check(plain?.Outputs.ContainsKey("BaseColor") != true, "The gear colour resolved without the part's dye buffer and UV pair.");
        Console.WriteLine("material_preview dye_buffers=checked mask_and=checked screen=checked detail_uv=checked");
    }

    /** Clock-driven constants, the frame scope and the view inputs become program nodes. */
    private static void TimeAndView()
    {
        // cos(clock), then the clock modulo one second, written to two output vectors.
        var stage = new MaterialStageData(GameShaderStage.Pixel, null,
            [0x3C, 1, 0, 0x1F, 0x43, 0, 0x3C, 1, 2, 0x43, 1], [], [Vector4.Zero, Vector4.Zero], 0, []);
        var inputs = NativeInspectionMaterialInputs.Create(0, null);
        NativeFrameInputs.ApplyTimeTerms(inputs);
        var evaluation = TfxEvaluator.Evaluate(stage, inputs);
        Check(evaluation.IsComplete && evaluation.Terms.Count == 8, "Clock outputs did not carry time terms.");
        foreach (float seconds in new[] { .3f, 1.7f, 12.25f })
        {
            var native = TfxEvaluator.Evaluate(stage, NativeInspectionMaterialInputs.Create(seconds, null)).Constants;
            Check(evaluation.Terms.All(pair => Math.Abs(pair.Value.Evaluate(seconds) - native[pair.Key / 4][pair.Key % 4]) < 1e-5f),
                $"Time terms differ from the native clock at {seconds} seconds.");
        }
        const string shader = """
            dcl_output o0.xyzw
            add r0.xyz, -v4.xyzx, cb12[7].xyzx
            mul r1.x, cb0[0].x, cb13[1].z
            mad o0.xyz, r0.xyzx, r1.xxxx, v1.xyzx
            mov o0.w, l(1.000000)
            """;
        var program = MaterialPreviewAnalyzer.Analyze(shader, 4, "fixture", [], [Vector4.One], "PremultipliedAlpha",
            timeTerms: new Dictionary<int, TfxTerm> { [0] = TfxTerm.Time }, worldVaryings: true).Program!;
        var operations = program.Nodes.Select(node => node.Operation).ToHashSet();
        Check(program.Outputs.ContainsKey("BaseColor") && operations.IsSupersetOf(["time", "camera", "position", "surface"]),
            "Time, camera, world position or surface inputs did not reach the colour program.");
        var withoutVaryings = MaterialPreviewAnalyzer.Analyze(shader, 4, "fixture", [], [Vector4.One], "PremultipliedAlpha");
        Check(withoutVaryings.Program?.Outputs.ContainsKey("BaseColor") != true,
            "An unverified vertex shader's varyings were read as world vectors.");
        Console.WriteLine("material_preview time_terms=checked frame_scope=checked view_inputs=checked");
    }

    /** A material without a blend override takes its blend from the part's render stage. */
    private static void StageBlends()
    {
        int[] stages = [0, 1, 6, 7, 12];
        string[] plain = stages.Select(stage => MaterialPreviewAnalyzer.ResolveBlendMode(0, stage)).ToArray();
        Check(plain.SequenceEqual(["Opaque", "Alpha", "Additive", "Additive", "NoColor"]),
            $"Stage default blends differ: {string.Join(',', plain)}.");
        const uint alphaOverride = 0x80 | 12;
        Check(MaterialPreviewAnalyzer.ResolveBlendMode(0, -1) == "Opaque"
            && MaterialPreviewAnalyzer.ResolveBlendMode(alphaOverride, 7) == "Alpha"
            && MaterialPreviewAnalyzer.ResolveBlendMode(alphaOverride, 8) == "Additive"
            && MaterialPreviewAnalyzer.ResolveBlendMode(alphaOverride, 12) == "NoColor",
            "Material override or forced stage blend has the wrong precedence.");
        Console.WriteLine("material_stage_blend " + string.Join(' ', stages.Zip(plain, (stage, blend) => $"stage{stage}={blend}"))
            + " override_stage7=Alpha forced_stage8=Additive forced_stage12=NoColor");
    }

    /** One shared material resolves a preview per visible stage blend. */
    internal static void OwnedStageBlends(PackageCatalog catalog, uint materialTag)
    {
        Check((new GameMaterialReader(catalog).Read(materialTag).StateOverrides & 0x80) == 0,
            "Stage blend fixture material has its own blend override.");
        var preview = MaterialPreviewAnalyzer.Read(catalog, materialTag, renderStages: [0, 1, 6, 7, 12]);
        var modes = preview.StageBlendModes!;
        Check(modes[0] == "Opaque" && modes[1] == "Alpha" && modes[6] == "Additive" && modes[7] == "Additive"
            && modes[12] == "NoColor", "Owned material stage blends differ.");
        Check(preview.BlendMode == "Additive" && preview.BlendVariants?.Keys.Order().SequenceEqual(["Alpha", "Opaque"]) == true
            && preview.BlendVariants["Opaque"].BlendMode == "Opaque" && preview.BlendVariants["Alpha"].BlendMode == "Alpha",
            "Owned material lacks a preview per visible stage blend.");
        Check(preview.Alpha == 0 && preview.Bindings.All(value => value.Role != "Normal"),
            "Additive stage preview kept surface terms that ONE, ONE blending ignores.");
        Console.WriteLine($"material_stage_blend owned={materialTag:X8} base={preview.BlendMode} variants={string.Join(',', preview.BlendVariants!.Keys.Order())}");
    }

    /** Counts exported parts per effective blend and checks each drawn stage has one. */
    internal static void CountStageBlends(AssetBundleManifest manifest)
    {
        var previews = manifest.Materials.Where(value => value.Preview is not null).ToDictionary(value => value.TagHash, value => value.Preview!);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var stageCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var mesh in manifest.Meshes)
        {
            string blend = "NoPreview";
            if (previews.TryGetValue(mesh.MaterialTag, out var preview))
            {
                Check(mesh.RenderStage < 0 || preview.StageBlendModes?.ContainsKey(mesh.RenderStage) == true,
                    $"Material {mesh.MaterialTag:X8} has no blend for stage {mesh.RenderStage}.");
                blend = mesh.RenderStage >= 0 ? preview.StageBlendModes![mesh.RenderStage] : preview.BlendMode;
                Check(blend == preview.BlendMode || blend == "NoColor" || preview.Mode == "No pixel shader"
                    || preview.BlendVariants?.ContainsKey(blend) == true,
                    $"Material {mesh.MaterialTag:X8} has no {blend} variant.");
            }
            counts[blend] = counts.GetValueOrDefault(blend) + 1;
            string key = $"{mesh.RenderStage}:{blend}";
            stageCounts[key] = stageCounts.GetValueOrDefault(key) + 1;
        }
        Console.WriteLine($"stage_blend root={manifest.RootTag:X8} parts={manifest.Meshes.Count} "
            + string.Join(' ', counts.Select(pair => $"{pair.Key}={pair.Value}"))
            + " by_stage=" + string.Join(',', stageCounts.Select(pair => $"{pair.Key}={pair.Value}")));
    }

    /** Owned fixtures cover direct colour, swapped channels and authored palette shaders. */
    internal static void Owned(PackageCatalog catalog)
    {
        var target = MaterialPreviewAnalyzer.Read(catalog, 0x815B8158);
        Check(target.Bindings.Single(value => value.Role == "BaseColor").TextureTag == 0x80C6B0A9, "Requested asset colour texture differs.");
        Check(target.Bindings.Single(value => value.Role == "Normal").TextureTag == 0x80C6B0AD, "Requested asset normal texture differs.");
        var swapped = MaterialPreviewAnalyzer.Read(catalog, 0x80FEE888);
        Check(swapped.Bindings.Single(value => value.Role == "BaseColor").Slot == 1, "Swapped shader uses a fixed colour slot.");
        var palette = MaterialPreviewAnalyzer.Read(catalog, 0x80C712FE);
        Check(palette.Palette is { Colors.Length: 7 } && palette.Bindings.All(value => value.Role != "BaseColor"), "Packed palette mask was wired as colour.");
        var transparent = MaterialPreviewAnalyzer.Read(catalog, 0x80FEA6B5);
        Check(transparent.BlendMode == "InverseAlpha" && transparent.PixelConstants?.Length >= 3,
            "Native blended material did not retain evaluated constants and blend mode.");
        var hob = MaterialPreviewAnalyzer.Read(catalog, 0x80C4429C);
        Check(hob.Recipe == "VexDye49" && hob.PixelConstants?.Length == 49
            && Math.Abs(hob.PixelConstants[4][0] - .24455422f) < .000001f
            && hob.PixelConstants[1][0] == 1 && hob.TextureFormats?.ContainsKey(0) == true,
            "Hob dye preview lost authored constants, object inputs or native texture formats.");
        var glow = MaterialPreviewAnalyzer.Read(catalog, 0x80C4423D);
        Check(glow.Recipe is null && glow.Program?.Outputs.ContainsKey("BaseColor") == true && glow.BlendMode == "PremultipliedAlpha" && glow.Alpha == 0
            && glow.PixelConstantMasks?[8] == 3 && glow.PixelConstants![8][0] == 4
            && glow.PixelConstants[8][2] == 0 && glow.PixelConstants[8][3] == 0,
            "Unused nonfinite glow lanes discarded colour constants or changed premultiplied blending.");
        string encoded = System.Text.Json.JsonSerializer.Serialize(glow);
        Check(!encoded.Contains("Infinity", StringComparison.Ordinal) && !encoded.Contains("NaN", StringComparison.Ordinal),
            "Material preview wrote invalid nonfinite JSON numbers.");
        var eye = MaterialPreviewAnalyzer.Read(catalog, 0x80C44229);
        Check(eye.Recipe is null && eye.Program?.Outputs.ContainsKey("BaseColor") == true && eye.PixelConstants?[2][0] == 50
            && eye.TextureFormats?.GetValueOrDefault(0u) is 29 or 72 or 75 or 78 or 91 or 93 or 99,
            "Glow program lost native sRGB format or evaluated intensity.");
        Console.WriteLine($"material_preview target=80FEBE71 swapped=checked palette=checked effect_alpha={transparent.Alpha} effect_rgb={string.Join(',', transparent.BaseColor)}");
        OwnedStageBlends(catalog, 0x815B8158);
        GraveMaterialChecks.Owned(catalog);
        OwnedOverlays(catalog);
    }

    /** The Centurion shield and War Beast head layers keep their animated colour. */
    private static void OwnedOverlays(PackageCatalog catalog)
    {
        // Shield 815B251F scrolls by clock; head 80C751C3 also reads the view and the world basis.
        foreach (var (tag, view) in new[] { (0x815B251Fu, false), (0x80C751C3u, true) })
        {
            var program = MaterialPreviewAnalyzer.Read(catalog, tag, renderStages: [7]).Program;
            var operations = program?.Nodes.Select(node => node.Operation).ToHashSet() ?? [];
            Check(program?.Outputs.ContainsKey("BaseColor") == true && operations.Contains("time")
                && (!view || operations.IsSupersetOf(["camera", "position", "surface"])),
                $"Overlay material {tag:X8} lost its animated colour.");
        }
        // 81560964 takes log2 of a negative clock value.
        var reader = new GameMaterialReader(catalog);
        foreach (uint tag in new uint[] { 0x815B251F, 0x80C751D9, 0x81560964 })
        {
            var stage = reader.ReadExecution(tag).Pixel;
            var terms = TfxEvaluator.Evaluate(stage, MaterialPreviewAnalyzer.CreateInputs(catalog)).Terms;
            Check(terms.Count > 0, $"Material {tag:X8} has no time terms.");
            foreach (float seconds in new[] { .37f, 2.5f, 61.7f })
            {
                var inputs = MaterialPreviewAnalyzer.CreateInputs(catalog);
                NativeFrameInputs.ApplySimulationTime(inputs, seconds);
                var native = TfxEvaluator.Evaluate(stage, inputs).Constants;
                Check(terms.All(pair => native[pair.Key / 4][pair.Key % 4] is var value
                    && Math.Abs(pair.Value.Evaluate(seconds) - value) <= 1e-4f * Math.Max(1, Math.Abs(value))),
                    $"Material {tag:X8} time terms differ from native evaluation at {seconds} seconds.");
            }
        }
        Console.WriteLine("material_preview overlays=815B251F,80C751C3 time_terms=815B251F,80C751D9,81560964");
    }

    /** Preview metadata never changes native bytes and remains optional for older bundles. */
    internal static void RoundTrip(PackageCatalog catalog, AssetBundleExportResult exported, string projectPath)
    {
        var project = StudioProject.Create(projectPath, "Material preview round trip");
        var prepared = AssetBundle.PrepareImport(project, catalog, exported.ManifestPath, new(AssetBundleImportMode.ApplyDeltas));
        Check(prepared.Edits.Count == 0 && prepared.AddedEdits.Count == 0, "Material preview metadata changed native assets.");
        var legacy = JsonNode.Parse(File.ReadAllText(exported.ManifestPath))!;
        foreach (var material in legacy["Materials"]!.AsArray()) material!.AsObject().Remove("Preview");
        foreach (var mesh in legacy["Meshes"]!.AsArray()) mesh!.AsObject().Remove("RenderStage");
        string legacyPath = Path.Combine(Path.GetDirectoryName(exported.ManifestPath)!, "asset.legacy.sunrise-bundle.json");
        File.WriteAllText(legacyPath, legacy.ToJsonString());
        var original = AssetBundle.PrepareImport(project, catalog, legacyPath, new(AssetBundleImportMode.ApplyDeltas));
        Check(original.Edits.Count == 0 && original.AddedEdits.Count == 0, "Legacy material bundle compatibility failed.");
        File.Delete(legacyPath);
        Console.WriteLine("material_preview new_metadata=noop legacy_metadata=noop renderstage=optional");
    }

    /** A radius below the smoothness range still packs the normal, and a 3D read with resolved coordinates is a volume node. */
    private static void ShaderLinks()
    {
        const string rough = """
            dcl_output o0.xyzw
            dcl_output o1.xyzw
            dcl_output o2.xyzw
            sample_indexable(texture2d)(float,float,float,float) r0.xyz, v3.xyxx, t0.xyzw, s1
            mov o0.xyz, r0.xyzx
            sample_indexable(texture2d)(float,float,float,float) r1.x, v3.xyxx, t1.xyzw, s1
            mad r0.w, r1.x, l(-0.250000), l(0.375000)
            mad_sat o1.xyz, v0.xyzx, r0.wwww, l(0.500000, 0.500000, 0.500000, 0.000000)
            mov o2.x, l(0)
            """;
        var packed = MaterialPreviewAnalyzer.Analyze(rough, 1, "fixture", [new(0, 10, 99), new(1, 11, 61)], worldVaryings: true).Program!;
        bool Reads(BundleMaterialProgram program, int[] roots, Func<BundleShaderNode, bool> test)
        {
            var pending = new Stack<int>(roots); var seen = new HashSet<int>();
            while (pending.TryPop(out int index))
                if (seen.Add(index))
                {
                    if (test(program.Nodes[index])) return true;
                    foreach (int input in program.Nodes[index].Inputs) pending.Push(input);
                }
            return false;
        }
        Check(packed.Outputs.TryGetValue("Normal", out var normal) && Reads(packed, normal, node => node.Operation == "surface")
            && packed.Outputs.TryGetValue("Roughness", out var roughness) && Reads(packed, roughness, node => node is { Operation: "sample", Slot: 1 }),
            "A normal packed below the smoothness range lost its normal or roughness.");
        const string volume = """
            dcl_output o0.xyzw
            mov r1.xy, v3.xyxx
            mov r1.z, l(0.250000)
            sample_indexable(texture3d)(float,float,float,float) r0.xyz, r1.xyzx, t4.xyzw, s1
            mov o0.xyz, r0.xyzx
            """;
        var noise = new BundleVolumeTexture(0x80B9F68D, 4, 4, 4, 2, 80);
        var sampled = MaterialPreviewAnalyzer.Analyze(volume, 1, "fixture", [], volumes: new Dictionary<uint, BundleVolumeTexture> { [4] = noise });
        Check(sampled.Program?.Outputs.ContainsKey("BaseColor") == true
            && sampled.Program.Nodes.Any(node => node is { Operation: "volume", Slot: 4, Inputs.Length: 3 })
            && sampled.VolumeTextures?[4] == noise, "A 3D texture read did not become a volume node.");
        Check(MaterialPreviewAnalyzer.Analyze(volume, 1, "fixture", []).Program is null,
            "A 3D read without a volume texture resolved.");
        Console.WriteLine("material_preview radius_pack=checked volume=checked");
    }

    /**
     * Owned shaders keep their animated and moving terms: the Ogre head's vertex pulse, a 3D noise read,
     * a laser's world-position varying, the glow shaders' clock, and an effect gate with an open bound.
     */
    internal static void ShaderLinksOwned(PackageCatalog catalog, string root)
    {
        var ogre = AssetBundle.Export(catalog, 0x80BF8A10, Path.Combine(root, "ogre"));
        var heads = ogre.Manifest.Meshes.Where(mesh => mesh.MaterialTag == 0x815B2FA6).ToArray();
        Check(heads.Length != 0 && heads.All(mesh =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(root, "ogre", mesh.Path)))?["VertexMotion"] is { } motion
            && (string?)motion["Profile"] == "VertexProgram"
            && motion["Program"]!["Nodes"]!.AsArray().Select(node => (string?)node!["Operation"]).ToHashSet()
                .IsSupersetOf(["time", "color", "normal"])), "The Ogre head lost its vertex pulse.");
        var noise = MaterialPreviewAnalyzer.Read(catalog, 0x81578B9F, renderStages: [0]);
        Check(noise.Program?.Nodes.Any(node => node.Operation == "volume") == true && noise.Program.Nodes.Any(node => node.Operation == "time")
            && noise.VolumeTextures?.Values.All(value => value is { Depth: 64, Columns: 8 }) == true,
            "The Ogre body's moving 3D noise did not resolve.");
        var laser = MaterialPreviewAnalyzer.Read(catalog, 0x81592B59, renderStages: [7]);
        Check(laser.Program?.Outputs.ContainsKey("BaseColor") == true && !laser.Notes.Contains(MaterialPreviewAnalyzer.UnresolvedBlendNote),
            "The Altar of Flame laser lost its world-position varying.");
        var glow = MaterialPreviewAnalyzer.Read(catalog, 0x80C75E00, renderStages: [7]);
        Check(glow.Recipe is null && glow.Program?.Nodes.Any(node => node.Operation == "time") == true,
            "A glow shader froze its clock in a recipe.");
        var effects = AssetBundle.Export(catalog, 0x80BBB2E6, Path.Combine(root, "effects"));
        var particles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "effects", effects.Manifest.ParticleMetadataPath!)))!;
        Check(particles.ToJsonString().Contains("-3.4028235E+38", StringComparison.Ordinal),
            "An open effect gate bound was not written as the float limit.");
        Console.WriteLine($"material_preview ogre_motion={heads.Length} volume=checked laser=checked glow_clock=checked particles=finite");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
