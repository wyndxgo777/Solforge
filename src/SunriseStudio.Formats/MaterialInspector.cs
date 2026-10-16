namespace SunriseStudio.Formats;

/** Material slots retain native stage identities; texture roles are not inferred from order. */
public sealed class MaterialInspector : IAssetInspector
{
    public string Id => "sunrise.material";
    public string DisplayName => "Material";
    /** True for the material class (0x808071E8). */
    public bool CanInspect(AssetInspectionContext context) => context.ClassHash == 0x808071E8;

    /** Texture assignments and inline constants can be edited without changing shader bytecode. */
    public AssetInspection Inspect(AssetInspectionContext context)
    {
        var r = new TagReader(context.Data);
        if (!r.Has(0, 0x400) || r.U64(0) != (ulong)r.Length)
            throw new InvalidDataException("Invalid material header.");
        List<AssetReference> references = [];
        List<AssetNode> sections = [new("Material", [r.UInt("Raw state overrides", 0x20, true, true)])];
        foreach (var (name, start) in new[] { ("Vertex", 0x48), ("Pixel", 0x2C8), ("Compute", 0x368) })
        {
            var textures = r.Array(start + 8, 0x80807211, 8);
            List<AssetNode> children = [];
            uint shader = r.U32(start);
            if (shader is not (0 or uint.MaxValue))
                references.Add(new(start, shader, context.ResolveName?.Invoke(shader) ?? $"{shader:X8}"));
            for (int i = 0; i < textures.Count; i++)
            {
                int p = textures.Data + i * 8;
                uint tag = r.U32(p + 4);
                references.Add(new(p + 4, tag, context.ResolveName?.Invoke(tag) ?? $"{tag:X8}"));
                children.Add(new($"Texture {r.U32(p)}", [r.UInt("Slot", p), r.UInt("Texture", p + 4, true, true)]));
            }
            foreach (var (label, field) in new[] { ("Expression constants", start + 0x30), ("Constant template", start + 0x50) })
            {
                var constants = r.Array(field, 0x80800090, 16);
                List<AssetNode> rows = [];
                for (int i = 0; i < constants.Count; i++)
                {
                    int p = constants.Data + i * 16;
                    rows.Add(new($"Vector {i}", [r.Float("X", p, true), r.Float("Y", p + 4, true),
                        r.Float("Z", p + 8, true), r.Float("W", p + 12, true)]));
                }
                children.Add(new(label, [], rows));
            }
            sections.Add(new(name, [r.UInt("Shader", start, hex: true),
                r.UInt("Constant buffer", start + 0x84, hex: true)], children));
        }
        return new("Material", context.Name, sections, references, []);
    }
}
