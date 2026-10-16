using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SunriseStudio.Rendering;

/** CB11 palette forms of entity vertex shaders. */
public enum NativeEntityPalette { None, Matrix, DualQuaternion }

/** Text helpers for DXBC disassembly. */
public static class DxbcText
{
    /** Splits a DXBC operand list on commas outside brackets and literals. */
    public static string[] SplitOperands(string text)
    {
        var operands = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '[') depth++;
            else if (text[i] is ')' or ']') depth--;
            else if (text[i] == ',' && depth == 0) { operands.Add(text[start..i].Trim()); start = i + 1; }
        }
        operands.Add(text[start..].Trim());
        return operands.ToArray();
    }
}

/**
 * Checks that a shader image only reads data the studio can supply, so unsupported shaders are
 * rejected instead of drawn wrongly.
 */
public static class NativeShaderContracts
{
    /** The image must read CB11 as four rows per instance and read packed word 15 only through bits 0..25. */
    public static bool CanUsePartialStaticInstances(ReadOnlySpan<byte> vertexBytecode)
    {
        string digest = Convert.ToHexString(SHA256.HashData(vertexBytecode));
        if (PinnedStaticVertex(digest)) return true;
        var usage = NativeShaderUsage.Get(vertexBytecode);
        return usage.InstanceMasks is { } masks && (masks[15] & ~0x03FFFFFFu) == 0
            && masks[3] != 0 && masks[7] != 0 && masks[11] != 0;
    }

    /**
     * Classifies an entity VS whose CB11 holds a transform palette from row 8.
     * @return None unless every palette read starts at row 8 and fixed reads stay in rows 5 to 7.
     */
    public static NativeEntityPalette ClassifyEntityPalette(ReadOnlySpan<byte> vertexBytecode, string disassembly)
    {
        var usage = NativeShaderUsage.Get(vertexBytecode);
        if (!usage.DynamicSlots.Contains(11)) return NativeEntityPalette.None;
        if (usage.Words.TryGetValue(11, out var fixedWords) && fixedWords.Any(word => word is < 20 or > 31))
            return NativeEntityPalette.None;
        var declared = Regex.Match(disassembly, @"dcl_constantbuffer CB11\[(\d+)\], dynamicIndexed");
        if (!declared.Success || int.Parse(declared.Groups[1].Value, CultureInfo.InvariantCulture) > 64)
            return NativeEntityPalette.None;
        var offsets = Regex.Matches(disassembly, @"cb11\[r\d+\.[xyzw] \+ (\d+)\]").Select(m => m.Groups[1].Value).ToArray();
        if (offsets.Length == 0 || offsets.Any(offset => offset != "8")) return NativeEntityPalette.None;
        return ClassifyPaletteIndex(disassembly);
    }

    /**
     * Names the palette form from how the VS scales its bone or chunk index into CB11 rows.
     * @return None when neither scale or both scales appear.
     */
    public static NativeEntityPalette ClassifyPaletteIndex(string disassembly)
    {
        bool dualQuaternion = false, matrix = false;
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) continue;
            string[] operands = DxbcText.SplitOperands(line[(space + 1)..]);
            switch (line[..space])
            {
                // Shift by 1: two rows per entry, the real and dual parts of a dual quaternion.
                case "ishl" when operands.Length == 3 && Regex.IsMatch(operands[1], @"^[rv]\d+\.[xyzw]+$"):
                    dualQuaternion |= WrittenLanesAre(operands[0], operands[2], "1");
                    break;
                // Index times 3: three rows of a transposed 3x4 matrix per entry.
                case "imul" when operands.Length == 4 && operands[0] == "null":
                    matrix |= WrittenLanesAre(operands[1], operands[2], "3") || WrittenLanesAre(operands[1], operands[3], "3");
                    break;
                case "imad" when operands.Length == 4:
                    matrix |= WrittenLanesAre(operands[0], operands[1], "3") || WrittenLanesAre(operands[0], operands[2], "3");
                    break;
            }
        }
        // TODO: shift by 2 is four rows per bone; it needs its row order and a palette builder.
        return dualQuaternion == matrix ? NativeEntityPalette.None
            : dualQuaternion ? NativeEntityPalette.DualQuaternion : NativeEntityPalette.Matrix;
    }

    /** True when each written destination lane gets this literal value; l(v) fills every lane. */
    private static bool WrittenLanesAre(string destination, string operand, string value)
    {
        var mask = Regex.Match(destination, @"^r\d+\.([xyzw]+)$");
        var literal = Regex.Match(operand, @"^l\(([^)]*)\)$");
        if (!mask.Success || !literal.Success) return false;
        string[] lanes = literal.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        return mask.Groups[1].Value.All(lane =>
            (lanes.Length == 1 ? lanes[0] : lanes.ElementAtOrDefault("xyzw".IndexOf(lane, StringComparison.Ordinal))) == value);
    }

    /** True when every word the shader reads from the frame constant buffer (CB13) is known. */
    public static bool CanUseFrameConstants(ReadOnlySpan<byte> bytecode, GameShaderStage stage,
        NativeFrameScopeConstants constants) =>
        MissingWords(bytecode, new(stage, 13), constants.KnownWordMasks) is null;

    /**
     * Checks one partially known buffer against the words the image reads.
     * @return Null when every read bit is known, else the first unknown word.
     */
    public static string? MissingWords(ReadOnlySpan<byte> bytecode, NativeBindingKey binding,
        IReadOnlyDictionary<uint, uint> knownWordMasks)
    {
        string digest = Convert.ToHexString(SHA256.HashData(bytecode));
        if (!PinnedStaticVertex(digest)) NativeShaderUsage.Get(bytecode);
        foreach (var word in knownWordMasks)
        {
            uint required = RequiredWordMask(digest, binding, word.Key);
            if ((word.Value & required) != required)
                return $"{binding.Stage} CB{binding.Slot} word {word.Key} needs unknown bits 0x{required & ~word.Value:X8}.";
        }
        return null;
    }

    private static bool PinnedStaticVertex(string digest) =>
        digest is "D67606C0E9766D10759890F5A32A555662887C25533D81A2951E2D4DEACD0D66"
            or "BB63C36058C9066366929319EDBD956BB944854EFD4EC9F54DD77A30C4DD907E"
            or "D5E81CEFDC52EA3A253729EE518A9D36939C3162099E3500CA27A97B6E9D0465"
            or "5DE9122766E81DE66D722A5232537AB55D26E9D81D06E1E5FFAF1787AF7E16C1";

    /** Pinned images keep their hardware-checked masks; other analyzed images use their read words. */
    public static uint RequiredWordMask(string digest, NativeBindingKey binding, uint wordIndex)
    {
        bool knownStaticVertex = PinnedStaticVertex(digest);
        if (binding.Stage == GameShaderStage.Vertex && knownStaticVertex
            && binding.Slot == 11 && wordIndex % 16 == 15)
            return 0x03FFFFFF;
        if (binding.Stage == GameShaderStage.Vertex && knownStaticVertex
            && binding.Slot == 12 && wordIndex is >= 36 and <= 39)
            return 0;
        // The global lighting quad VS reads CB12 row 9 X only.
        if (binding.Stage == GameShaderStage.Vertex && binding.Slot == 12 && wordIndex is >= 37 and <= 39
            && digest == "1A1A21BC9200E1CCBA992ADD8147BF63F6A0597935795A0C1E899AFC16B3339C")
            return 0;
        if (binding.Stage == GameShaderStage.Pixel && binding.Slot == 13)
        {
            bool? read = digest switch
            {
                "2F0E4F06F8C8FA5F64C45C9386A13BD704D7EC4978FA857185C8B21CDEB1C734" => wordIndex == 24,
                "1EE079E1EB87C665B04D12AC813BB37120BBE2133545BBAF4BF0C3E8A414E426" => wordIndex is 4 or 7 or 22,
                "827A66CB1ED088FCCB8476D5553CBDD08BBA025E5DC7113A64AE76093B8BEFDF" => wordIndex is 16 or 17 or 19,
                "C026D4E225EB238A54E2E728F437AF56C48BE6A19FD0DC0DA14FA3DC0A4806A9" => wordIndex == 19,
                _ => null
            };
            if (read.HasValue) return read.Value ? uint.MaxValue : 0;
        }
        if (!knownStaticVertex && NativeShaderUsage.Find(digest) is { } usage)
            return usage.RequiredMask(binding, wordIndex);
        return uint.MaxValue;
    }

    /** Only immediate PS view reads in rows 0 to 13 can use this buffer; callers check row 13 lanes. */
    public static bool CanUseKnownPixelView(GameShaderInfo? shader)
    {
        if (shader is null || shader.Stage != GameShaderStage.Pixel) return false;
        var declaration = shader.Declarations.Select(line => Regex.Match(line,
            @"^dcl_constantbuffer CB12\[(\d+)\], immediateIndexed$")).FirstOrDefault(match => match.Success);
        if (declaration is null || !int.TryParse(declaration.Groups[1].Value, out int count) || count is < 1 or > 14)
            return false;
        bool found = false;
        foreach (string raw in shader.Disassembly.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            foreach (Match read in Regex.Matches(line, @"\bcb12\[([^\]]+)\]"))
            {
                found = true;
                if (!int.TryParse(read.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int row)
                    || row is < 0 or > 13) return false;
            }
        }
        return found;
    }
}
