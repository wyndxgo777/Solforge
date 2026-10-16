namespace SunriseStudio.Scene;

/** Materials chosen for an entity, or why they could not be resolved. This file also selects them. */
public sealed record EntityMaterialSelection(uint[] Materials, string? Unresolved);
/** A warning raised while selecting an entity's materials, with its source tag and offset. */
public sealed record EntityMaterialWarning(uint EntityTag, uint ConfigTag, string Message,
    uint SourceTag = 0, int SourceOffset = -1);

/** Material alternatives follow the component's enabled-part conditions in authored order. */
public static class EntityMaterialSelector
{
    /** Unknown initial state retains a labelled preview choice without treating it as native state. */
    public static EntityMaterialSelection Read(byte[] config, int resource, IReadOnlyList<bool>? enabledParts,
        string? unresolved = null)
    {
        var data = new TagData(config);
        if (!data.Has(resource - 4, 0x324) || data.U32(resource - 4) is not (0x808072BD or 0x80807286))
            throw new InvalidDataException("External materials need a typed model resource.");
        int[] groups = data.Rows(resource + 0x2D0, 0x808072C4, 12);
        if (groups.Length == 0) return new([], null);
        int[] materials = data.Rows(resource + 0x310, 0x80800014, 4);
        int[] conditions = data.Rows(resource + 0x300, 0x808072C5, 8);
        int[] indices = data.Rows(resource + 0x2F0, 0x80800006, 2);
        var result = Enumerable.Repeat(uint.MaxValue, groups.Length).ToArray();
        bool fallback = enabledParts is null;
        for (int group = 0; group < groups.Length; group++)
        {
            int row = groups[group], count = data.I16(row), start = data.I32(row + 4), firstCondition = data.I16(row + 8);
            if (count <= 0) continue;
            if (start < 0 || start > materials.Length - count || firstCondition < 0 || firstCondition > conditions.Length - count)
                throw new InvalidDataException("External material choices leave their authored arrays.");
            for (int candidate = 0; candidate < count; candidate++)
            {
                uint material = data.U32(materials[start + candidate]);
                int condition = conditions[firstCondition + candidate];
                bool required = Test(data.I16(condition), data.I16(condition + 2), true);
                bool excluded = Test(data.I16(condition + 4), data.I16(condition + 6), false);
                if (material == uint.MaxValue || !fallback && (!required || !excluded)) continue;
                result[group] = material;
                break;
            }
        }
        return new(result, fallback ? unresolved ?? "Initial material conditions need runtime state." : null);

        bool Test(int count, int index, bool required)
        {
            if (count <= 0) return true;
            if (index < 0 || count > 1 && index > indices.Length - count)
                throw new InvalidDataException("External material condition indices leave their array.");
            bool passed = true;
            for (int ordinal = 0; ordinal < count; ordinal++)
            {
                int bit = count == 1 ? index : data.I16(indices[index + ordinal]);
                if (bit is < 0 or >= 512 || enabledParts is not null && bit >= enabledParts.Count)
                    throw new InvalidDataException("External material condition names an absent part bit.");
                if (enabledParts is not null && enabledParts[bit] != required) passed = false;
            }
            return passed;
        }
    }
}
