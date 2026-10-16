/** File: Material-graph node types. */
namespace Solforge.Graphs;

/**
 * Material graph nodes. Game materials run fixed, precompiled shaders, so a material graph does not generate shader
 * code: it folds its math to constants and writes the native fields the material tag exposes (the same fields as
 * MaterialInterchange): float constants per stage, bank and vector, and texture slots per stage. Material Output
 * names the target material and drives the editor's preview swatch; it never writes guessed shader roles.
 */
public static partial class NodeLibrary
{
    public const string MaterialOutput = "mat.output";
    public const string SetConstant = "mat.set_constant";
    public const string SetTexture = "mat.set_texture";
    public static readonly string[] Stages = ["Pixel", "Vertex", "Compute"];
    public static readonly string[] Banks = ["Constant template", "Expression constants"];

    /** Yields the material node type definitions. */
    private static IEnumerable<NodeType> MaterialTypes()
    {
        const GraphKind kind = GraphKind.Material;
        yield return Node(MaterialOutput, "Material Output", "Output", kind,
            "The material this graph edits (tag) and the preview surface. Set Shader Constant and Set Texture Slot nodes write the native fields.",
            [In("base_color", "Base Color (preview)", PinType.Color, "#808080FF"), In("roughness", "Roughness (preview)", PinType.Float, "0.5"),
             In("metalness", "Metalness (preview)", PinType.Float, "0"), In("emissive", "Emissive (preview)", PinType.Color, "#000000FF"),
             In("opacity", "Opacity (preview)", PinType.Float, "1")],
            [Prop("target", "Material", PropertyKind.Tag, "", "Material tag (0x...) to edit")], "result root final");
        yield return Node(SetConstant, "Set Shader Constant", "Output", kind,
            "Writes a float4 constant of the target material: stage, bank and vector index as the Materials page lists them. Mask picks the components written.",
            [In("value", "Value", PinType.Color, "0, 0, 0, 0", required: true)],
            [Prop("stage", "Stage", PropertyKind.Choice, "Pixel", "", Stages),
             Prop("bank", "Bank", PropertyKind.Choice, "Constant template", "", Banks),
             Prop("vector", "Vector", PropertyKind.Integer, "0", "Vector index within the bank"),
             Prop("mask", "Mask", PropertyKind.Text, "xyzw", "Components to write, e.g. xyz")],
            "cbuffer parameter uniform constant write");
        yield return Node(SetTexture, "Set Texture Slot", "Output", kind,
            "Points a texture slot of the target material at another texture.",
            [In("texture", "Texture", PinType.Texture, required: true)],
            [Prop("stage", "Stage", PropertyKind.Choice, "Pixel", "", Stages),
             Prop("slot", "Slot", PropertyKind.Integer, "0", "Texture slot index")], "sampler bind assign");
        yield return Node("mat.texture", "Texture", "Textures", kind, "A texture tag.",
            [Out("texture", "", PinType.Texture)], [Prop("tag", "Texture", PropertyKind.Tag, "", "Texture tag (0x...)")], "texture2d sample image");
        yield return Node("mat.scalar_param", "Scalar Parameter", "Parameters", kind, "A named number, shown in the parameter list.",
            [Out("value", "", PinType.Float)], [Prop("name", "Name", PropertyKind.Text, "Scalar"), Prop("value", "Value", PropertyKind.Number, "0")],
            "param float");
        yield return Node("mat.vector_param", "Vector Parameter", "Parameters", kind, "A named colour or vector, shown in the parameter list.",
            [Out("value", "", PinType.Color)], [Prop("name", "Name", PropertyKind.Text, "Color"), Prop("value", "Value", PropertyKind.Color, "#FFFFFFFF")],
            "param colour tint");
        yield return Node("mat.srgb_to_linear", "sRGB to Linear", "Color", kind, "Converts an sRGB colour to linear (gamma 2.2).",
            [In("color", "", PinType.Color, "#FFFFFFFF"), Out("result", "", PinType.Color)], keywords: "gamma");
        yield return Node("mat.hsv", "HSV to RGB", "Color", kind, "Builds a colour from hue (0..1), saturation and value.",
            [In("h", "Hue", PinType.Float, "0"), In("s", "Saturation", PinType.Float, "1"), In("v", "Value", PinType.Float, "1"),
             Out("result", "", PinType.Color)], keywords: "hue saturation colour");
    }
}
