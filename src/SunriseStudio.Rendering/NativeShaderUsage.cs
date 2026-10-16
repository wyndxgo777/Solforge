using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;

namespace SunriseStudio.Rendering;

/** Constant-buffer words one DXBC image reads, taken from its disassembly. */
public sealed partial class NativeShaderUsage
{
    private static readonly ConcurrentDictionary<string, NativeShaderUsage> Cache = new();

    /** Immediate reads by slot; each word is a 32-bit lane of one constant vector. */
    public IReadOnlyDictionary<uint, HashSet<uint>> Words => words;
    /** Slots read through an index this analysis cannot bound. */
    public IReadOnlySet<uint> DynamicSlots => dynamicSlots;
    /** Bits of each of the 16 per-instance CB11 words, or null when CB11 is not read per instance. */
    public uint[]? InstanceMasks { get; private set; }
    public string Digest { get; }

    private readonly Dictionary<uint, HashSet<uint>> words = [];
    private readonly HashSet<uint> dynamicSlots = [];

    private NativeShaderUsage(string digest) => Digest = digest;

    /** SHA-256 of the bytecode as upper-case hex; the cache key. */
    public static string DigestOf(ReadOnlySpan<byte> bytecode) => Convert.ToHexString(SHA256.HashData(bytecode));

    /** The result is cached by SHA-256 digest; the same image always gives the same usage. */
    public static NativeShaderUsage Get(ReadOnlySpan<byte> bytecode)
    {
        string digest = DigestOf(bytecode);
        if (Cache.TryGetValue(digest, out var cached)) return cached;
        var usage = new NativeShaderUsage(digest);
        // A container whose checksum fails is never analyzed; Direct3D would refuse to create it.
        if (DxbcChecksum.Matches(bytecode)) usage.Analyze(Disassemble(bytecode));
        else usage.Refuse();
        return Cache.GetOrAdd(digest, usage);
    }

    /** Null when the image has not been analyzed in this process. */
    public static NativeShaderUsage? Find(string digest) => Cache.GetValueOrDefault(digest);

    /**
     * Bits of one constant word the image needs.
     * @return All bits for a dynamic slot; zero for a word the image never reads.
     */
    public uint RequiredMask(NativeBindingKey binding, uint word)
    {
        if (binding.Slot == 11 && binding.Stage == GameShaderStage.Vertex && InstanceMasks is { } masks)
            return masks[word % 16];
        if (dynamicSlots.Contains(binding.Slot)) return uint.MaxValue;
        return words.TryGetValue(binding.Slot, out var read) && read.Contains(word) ? uint.MaxValue : 0;
    }

    /** Every slot counts as fully read. */
    private void Refuse() { for (uint slot = 0; slot < 16; slot++) dynamicSlots.Add(slot); }

    private static unsafe string Disassemble(ReadOnlySpan<byte> bytecode)
    {
        fixed (byte* pointer = bytecode)
        {
            using var blob = Compiler.Disassemble((IntPtr)pointer, (nuint)bytecode.Length, DisasmFlags.None, null);
            return blob.AsString();
        }
    }

    /** Index registers must hold instance_id * 4 + c, assigned once, for CB11 to count as per instance. */
    private void Analyze(string disassembly)
    {
        string? instanceRegister = null;
        var index = new Dictionary<(int Register, int Lane), int>();
        var writes = new Dictionary<(int Register, int Lane), int>();
        var usedIndexLanes = new HashSet<(int Register, int Lane)>();
        var instance = new uint[16];
        bool instanceRefused = false, instanceRead = false, immediateRead = false, branches = false;
        foreach (string raw in disassembly.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("dcl_input_sgv", StringComparison.Ordinal) && line.EndsWith("instance_id", StringComparison.Ordinal))
                instanceRegister = Regex.Match(line, @"\b(v\d+)\.").Groups[1].Value;
            if (line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            string[] operands = space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..]);
            string baseOpcode = opcode.Split('(')[0].Replace("_sat", "", StringComparison.Ordinal);
            if (baseOpcode.StartsWith("if", StringComparison.Ordinal) || baseOpcode.StartsWith("break", StringComparison.Ordinal)
                || baseOpcode.StartsWith("continue", StringComparison.Ordinal) || baseOpcode.StartsWith("call", StringComparison.Ordinal)
                || baseOpcode.StartsWith("retc", StringComparison.Ordinal) || baseOpcode is "else" or "endif" or "loop"
                    or "endloop" or "switch" or "case" or "default" or "endswitch" or "label")
                branches = true;
            int destinations = baseOpcode is "udiv" or "umul" or "imul" or "uaddc" or "usubb" or "sincos" or "swapc" ? 2 : 1;
            if (operands.Length < destinations) destinations = operands.Length;
            var defined = new Dictionary<int, int>();
            if (operands.Length > 0 && Destination(operands[0]) is { } destination)
                for (int lane = 0; lane < 4; lane++)
                    if (destination.Mask.Contains("xyzw"[lane]) && IndexValue(baseOpcode, operands, lane, instanceRegister, index) is { } value)
                        defined[lane] = value;
            for (int operand = destinations; operand < operands.Length; operand++)
                foreach (Match read in ConstantRead().Matches(operands[operand]))
                {
                    uint slot = uint.Parse(read.Groups["slot"].Value, CultureInfo.InvariantCulture);
                    string swizzle = read.Groups["swizzle"].Success ? read.Groups["swizzle"].Value : "xyzw";
                    if (read.Groups["row"].Success)
                    {
                        uint row = uint.Parse(read.Groups["row"].Value, CultureInfo.InvariantCulture);
                        if (!words.TryGetValue(slot, out var set)) words[slot] = set = [];
                        foreach (char component in swizzle) set.Add(row * 4 + (uint)"xyzw".IndexOf(component));
                        if (slot == 11) immediateRead = true;
                        continue;
                    }
                    var key = (int.Parse(read.Groups["register"].Value, CultureInfo.InvariantCulture),
                        "xyzw".IndexOf(read.Groups["lane"].Value[0]));
                    int offset = int.Parse(read.Groups["offset"].Value, CultureInfo.InvariantCulture);
                    if (slot != 11 || !index.TryGetValue(key, out int baseRow) || baseRow + offset is < 0 or > 3)
                    {
                        dynamicSlots.Add(slot);
                        if (slot == 11) instanceRefused = true;
                        continue;
                    }
                    usedIndexLanes.Add(key);
                    instanceRead = true;
                    int instanceRow = baseRow + offset;
                    foreach (char component in swizzle)
                    {
                        int word = instanceRow * 4 + "xyzw".IndexOf(component);
                        instance[word] |= word == 15 ? PackedBits(baseOpcode, operands, operand) : uint.MaxValue;
                    }
                }
            for (int operand = 0; operand < destinations; operand++)
                if (Destination(operands[operand]) is { } written)
                    for (int lane = 0; lane < 4; lane++)
                        if (written.Mask.Contains("xyzw"[lane]))
                        {
                            var key = (written.Register, lane);
                            writes[key] = writes.GetValueOrDefault(key) + 1;
                            if (operand == 0 && defined.TryGetValue(lane, out int value)) index[key] = value;
                            else index.Remove(key);
                        }
        }
        // Straight-line code gives each read the latest write; with branches an index must be written once.
        if (branches && usedIndexLanes.Any(lane => writes.GetValueOrDefault(lane) != 1)) instanceRefused = true;
        // Per-instance windows and fixed rows in one image leave the window base unproved.
        if (instanceRefused || instanceRead && immediateRead) dynamicSlots.Add(11);
        else if (instanceRead) InstanceMasks = instance;
    }

    /** Bits of packed word 15 an instruction consumes; unknown forms consume every bit. */
    private static uint PackedBits(string opcode, IReadOnlyList<string> operands, int operand)
    {
        uint Union(string text) => Literal(text) is { } values ? values.Aggregate(0u, (a, b) => a | b) : uint.MaxValue;
        uint Maximum(string text) => Literal(text) is { } values ? values.Max() : uint.MaxValue;
        if (opcode == "and" && operands.Count == 3)
            return Union(operands[operand == 1 ? 2 : 1]);
        if (opcode == "bfi" && operands.Count == 5 && operand == 3)
            return Maximum(operands[1]) is var width and < 32 ? (1u << (int)width) - 1 : uint.MaxValue;
        if (opcode is "ubfe" or "ibfe" && operands.Count == 4 && operand == 3)
        {
            uint width = Maximum(operands[1]), offset = Maximum(operands[2]);
            return width + offset < 32 ? ((1u << (int)(width + offset)) - 1) & ~((1u << (int)offset) - 1) : uint.MaxValue;
        }
        return uint.MaxValue;
    }

    /** Recognized index forms: bfi/ishl/imad of instance_id and iadd/mov of a tracked lane. */
    private static int? IndexValue(string opcode, IReadOnlyList<string> operands, int lane,
        string? instanceRegister, Dictionary<(int Register, int Lane), int> index)
    {
        bool IsInstance(string text) => instanceRegister is not null
            && Regex.IsMatch(text, $@"^{instanceRegister}\.x+$");
        int? LiteralLane(string text) => Literal(text) is { } values
            ? (int)values[Math.Min(lane, values.Length - 1)] : null;
        int? Tracked(string text)
        {
            var match = Regex.Match(text, @"^r(\d+)\.([xyzw]+)$");
            if (!match.Success) return null;
            string swizzle = match.Groups[2].Value;
            char component = swizzle[Math.Min(swizzle.Length == 1 ? 0 : lane, swizzle.Length - 1)];
            return index.TryGetValue((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                "xyzw".IndexOf(component)), out int value) ? value : null;
        }
        switch (opcode)
        {
            case "bfi" when operands.Count == 5 && IsInstance(operands[3])
                && LiteralLane(operands[1]) == 30 && LiteralLane(operands[2]) == 2 && LiteralLane(operands[4]) is >= 0 and <= 3:
                return LiteralLane(operands[4]);
            case "ishl" when operands.Count == 3 && IsInstance(operands[1]) && LiteralLane(operands[2]) == 2:
                return 0;
            case "imad" when operands.Count == 4 && LiteralLane(operands[3]) is { } add
                && (IsInstance(operands[1]) && LiteralLane(operands[2]) == 4 || IsInstance(operands[2]) && LiteralLane(operands[1]) == 4):
                return add;
            case "iadd" when operands.Count == 3:
                if (Tracked(operands[1]) is { } left && LiteralLane(operands[2]) is { } right) return left + right;
                if (Tracked(operands[2]) is { } second && LiteralLane(operands[1]) is { } first) return first + second;
                return null;
            case "mov" when operands.Count == 2:
                return Tracked(operands[1]);
            default:
                return null;
        }
    }

    private static (int Register, string Mask)? Destination(string operand)
    {
        var match = Regex.Match(operand, @"^r(\d+)\.([xyzw]+)$");
        return match.Success ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value) : null;
    }

    /** Integer lanes of an l(...) operand; null for anything else, including floats. */
    private static uint[]? Literal(string text)
    {
        var match = Regex.Match(text, @"^l\(([^)]*)\)$");
        if (!match.Success) return null;
        var values = new List<uint>();
        foreach (string part in match.Groups[1].Value.Split(','))
        {
            string value = part.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)) values.Add(hex);
            else if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint number)) values.Add(number);
            else return null;
        }
        return values.Count == 0 ? null : values.ToArray();
    }

    [GeneratedRegex(@"cb(?<slot>\d+)\[(?:(?<row>\d+)|r(?<register>\d+)\.(?<lane>[xyzw]) \+ (?<offset>\d+))\](?:\.(?<swizzle>[xyzw]+))?")]
    private static partial Regex ConstantRead();
}
