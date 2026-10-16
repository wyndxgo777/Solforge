using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using System.Numerics;

/** Checks that installed Grave materials keep their shader outputs and sampler address modes when interpreted. */
internal static class GraveMaterialChecks
{
    /** Native remaps must survive expression export instead of becoming direct mask channels. */
    internal static void Owned(PackageCatalog catalog)
    {
        uint[] materials = [0x81584C79, 0x81584C9E, 0x81584CA4, 0x81584CB5, 0x81584CB8, 0x81584CBB];
        foreach (uint tag in materials)
        {
            var preview = MaterialPreviewAnalyzer.Read(catalog, tag, renderStages: [0]);
            Check(preview.BlendMode == "Opaque" && preview.Program is not null
                && new[] { "BaseColor", "Metallic", "Roughness" }.All(preview.Program.Outputs.ContainsKey),
                $"Grave material {tag:X8} lost a shader output.");
        }
        var source = MaterialPreviewAnalyzer.Read(catalog, 0x81584CBB, renderStages: [0]);
        Check(source.PixelShaderTag == 0x81584CB9
            && source.ShaderSha256 == "BF9CF88C80016D7807FF335F4C40AF9C86AAABA3D007D4C4F466A4B49623B7F5",
            "Grave shader fixture changed.");
        var program = source.Program!;
        Check(source.Samplers is { } samplers && samplers[1].AddressU == "Wrap" && samplers[1].AddressV == "Wrap"
            && samplers[2].AddressU == "Clamp" && samplers[2].AddressV == "Clamp" && samplers[2].MipLodBias == -.5f,
            "Grave mask sampler lost its native clamp or mip-bias metadata.");
        Check(program.Nodes.Where(node => node.Operation == "sample" && node.Slot == 1).All(node => node.SamplerSlot == 2),
            "Grave mask samples use the wrong sampler.");
        Check(program.Outputs.ContainsKey("Normal") && program.Nodes.Any(node => node.Operation == "basis"),
            "Grave normal layers lost the verified vertex basis.");
        var material = new GameMaterialReader(catalog).Read(0x81584CBB);
        var inputs = material.Textures.Where(texture => texture.Stage == GameShaderStage.Pixel)
            .Select(texture => new MaterialPreviewAnalyzer.TextureInput(texture.Slot, texture.TextureTag, (int)texture.Format)).ToArray();
        var constants = source.PixelConstants!.Select(row => new Vector4(row[0], row[1], row[2], row[3])).ToArray();
        var unknown = MaterialPreviewAnalyzer.Analyze(material.PixelShader!.Disassembly, source.PixelShaderTag,
            source.ShaderSha256, inputs, constants).Program!;
        // Read resolves world varyings, so node numbers differ; the expressions must not.
        Check(!unknown.Outputs.ContainsKey("Normal") && new[] { "BaseColor", "Metallic", "Roughness" }
            .All(role => unknown.Outputs[role].Select(index => Expression(unknown, index))
                .SequenceEqual(program.Outputs[role].Select(index => Expression(program, index)))),
            "Normal export changed existing surface outputs or trusted an unknown vertex contract.");
        var unpacked = MaterialPreviewAnalyzer.Analyze(material.PixelShader.Disassembly.Replace(
            "l(0.500000, 0.500000, 0.500000, 0.000000)", "l(0.400000, 0.400000, 0.400000, 0.000000)", StringComparison.Ordinal),
            source.PixelShaderTag, source.ShaderSha256, inputs, constants, nativeTangentInputs: true).Program!;
        Check(!unpacked.Outputs.ContainsKey("Normal"), "An unknown G-buffer packing became a native normal.");
        int count = 0;
        foreach (float mask in new[] { 0f, .25f, .5f, .75f, 1f })
            foreach (float blue0 in new[] { 0f, .5f, 1f })
                foreach (float blue3 in new[] { 0f, .5f, 1f })
                {
                    float[][] textures = [[.35f, .25f, blue0, .5f], [mask, .4f, .2f, .5f],
                        [.2f, .6f, .4f, .3f], [.1f, .2f, blue3, .4f], [.55f, .45f, .3f, 1f]];
                    float first = Saturate(.7f - 1.7f * mask), second = Saturate(-1 + 2 * mask);
                    float smooth = first + second * (-.6f - first);
                    smooth = Math.Min(smooth, Math.Min(Saturate(blue0 + .6818935f),
                        Math.Min(Saturate(blue3 + .39999998f), Saturate(.3f + .7420627f))));
                    float[] paletteA = [.21431717f, .34831396f, .60263443f];
                    float[] deltaA = [.7856828f, .6516861f, .39736557f];
                    float[] paletteB = [.6390232f, .47180408f, .24975905f];
                    float[] deltaB = [.16097683f, .32819593f, .550241f];
                    for (int lane = 0; lane < 3; lane++)
                        Near(Evaluate(program.Outputs["BaseColor"][lane]),
                            (paletteA[lane] + deltaA[lane] * .25f)
                            * (paletteB[lane] + deltaB[lane] * Saturate(2 * mask)), "BaseColor");
                    Near(Evaluate(program.Outputs["Metallic"][0]), Saturate(1 - 2 * mask), "Metallic");
                    Near(Evaluate(program.Outputs["Roughness"][0]), 1 - smooth, "Roughness");
                    count++;

                    float Evaluate(int index)
                    {
                        var node = program.Nodes[index];
                        float[] values = node.Inputs.Select(Evaluate).ToArray();
                        return node.Operation switch
                        {
                            "constant" => node.Value, "uv" => .25f,
                            "sample" => textures[node.Slot][node.Channel],
                            "add" => values[0] + values[1], "mul" => values[0] * values[1],
                            "neg" => -values[0], "mad" => values[0] * values[1] + values[2],
                            "min" => Math.Min(values[0], values[1]), "max" => Math.Max(values[0], values[1]),
                            _ => throw new InvalidDataException($"Unexpected grave expression {node.Operation}.")
                        };
                    }
                }
        Console.WriteLine($"material_grave root=80F74ABE materials={materials.Length} samples={count} palette=checked metal=remapped roughness=remapped");
    }

    private static string Expression(BundleMaterialProgram program, int index)
    {
        var node = program.Nodes[index];
        return $"{node.Operation}:{node.Value}:{node.Slot}:{node.Channel}:{node.SamplerSlot}("
            + string.Join(",", node.Inputs.Select(input => Expression(program, input))) + ")";
    }

    private static float Saturate(float value) => Math.Clamp(value, 0, 1);
    private static void Near(float actual, float expected, string role)
        => Check(Math.Abs(actual - expected) < .000002f, $"Grave {role} differs: {actual} != {expected}.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
