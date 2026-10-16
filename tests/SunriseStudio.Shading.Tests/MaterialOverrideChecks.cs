using System.Numerics;
using SunriseStudio.Rendering;

/** Live material overrides (the node graph preview): what they patch, what they leave alone, and the shared set. */
internal static class MaterialOverrideChecks
{
    public static void Run()
    {
        Vector4[] tfx = [new(1, 2, 3, 4)], template = [new(10, 20, 30, 40), new(5, 6, 7, 8)];
        var stage = new MaterialStageData(GameShaderStage.Pixel, null, [], tfx, template, 0, []);
        var value = new Vector4(100, 101, 102, 103);

        // No override leaves the authored data as it is: the same instance, so the default draw is unchanged.
        Check(ReferenceEquals(MaterialOverrideSet.Apply(stage, []), stage), "An empty override changed the stage.");
        Check(ReferenceEquals(MaterialOverrideSet.Apply(stage,
            [new(GameShaderStage.Vertex, MaterialConstantBank.Template, 0, 15, value)]), stage), "A vertex write changed the pixel stage.");

        var masked = MaterialOverrideSet.Apply(stage,
            [new(GameShaderStage.Pixel, MaterialConstantBank.Template, 1, MaterialOverrideSet.ParseMask("xz"), value)]);
        Check(masked.ConstantTemplate[1] == new Vector4(100, 6, 102, 8) && masked.ConstantTemplate[0] == template[0],
            "A masked template write did not keep the other lanes.");
        Check(template[1] == new Vector4(5, 6, 7, 8) && ReferenceEquals(stage.ConstantTemplate, template),
            "A template write changed the authored array.");
        Check(ReferenceEquals(masked.TfxConstants, tfx), "A template write copied the expression constants.");

        var expression = MaterialOverrideSet.Apply(stage,
            [new(GameShaderStage.Pixel, MaterialConstantBank.Expression, 0, MaterialOverrideSet.ParseMask("rgba"), value)]);
        Check(expression.TfxConstants[0] == value && tfx[0] == new Vector4(1, 2, 3, 4) && ReferenceEquals(expression.ConstantTemplate, template),
            "An expression write did not patch only a copy of the expression constants.");

        var skipped = new List<string>();
        Check(ReferenceEquals(MaterialOverrideSet.Apply(stage,
            [new(GameShaderStage.Pixel, MaterialConstantBank.Template, 2, 15, value)], skipped), stage) && skipped.Count == 1,
            "A write past the material's vectors was not left out.");

        // A stage that reads an external constant buffer does not draw its template, so a template write is left out.
        var external = stage with { ConstantSourceTag = 0x80801234 };
        skipped.Clear();
        var partial = MaterialOverrideSet.Apply(external,
            [new(GameShaderStage.Pixel, MaterialConstantBank.Template, 0, 15, value),
             new(GameShaderStage.Pixel, MaterialConstantBank.Expression, 0, 1, value)], skipped);
        Check(ReferenceEquals(partial.ConstantTemplate, template) && partial.TfxConstants[0] == new Vector4(100, 2, 3, 4)
            && skipped.Count == 1 && skipped[0].Contains("0x80801234"), "An external constant buffer did not keep its template.");

        Check(MaterialOverrideSet.ParseMask("xyzw") == 15 && MaterialOverrideSet.ParseMask("rgb") == 7
            && MaterialOverrideSet.ParseMask("XY") == 3 && MaterialOverrideSet.ParseMask("") == 0, "Mask parsing changed.");

        // The shared set: each change bumps the revision once; snapshots never change afterwards.
        var set = new MaterialOverrideSet();
        var red = new MaterialOverride([new(GameShaderStage.Pixel, MaterialConstantBank.Template, 0, 15, value)], []);
        set.Clear();
        Check(set.Revision == 0 && !set.Remove(1), "Clearing an empty set changed it.");
        set.Set(1, red);
        var (revision, before) = set.Snapshot();
        set.Set(1, red);
        Check(revision == 1 && set.Revision == 1, "Setting the same override again bumped the revision.");
        set.Set(2, red with { });
        Check(set.Revision == 2 && set.Count == 2 && before.Count == 1, "A snapshot changed after a later edit.");
        Check(set.Remove(2) && set.Revision == 3 && set.Get(2) is null && ReferenceEquals(set.Get(1), red), "Removing an override failed.");

        object first = new(), second = new();
        set.Report(first, 1, new(1, 3, ["a"]));
        set.Report(second, 1, new(2, 4, ["a", "b"]));
        var summary = set.Summary(1);
        Check(summary.Materials == 3 && summary.Parts == 7 && summary.Skipped.Count == 2, "Reports did not add up across renderers.");
        set.Forget(first);
        set.Report(second, 1, null);
        Check(set.Summary(1).Parts == 0, "A forgotten renderer still reported.");
        set.Clear();
        Check(set.Count == 0 && set.Revision == 4, "Clear did not put every material back.");
        Console.WriteLine("material_overrides apply=pass set=pass");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
