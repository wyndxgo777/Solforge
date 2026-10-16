using System.Numerics;
using System.Text.RegularExpressions;
using SunriseStudio.Rendering;

namespace SunriseStudio.Interchange;

/**
 * Pixel input lanes the vertex shader fills from a vertex stream, a decorator input or a world vector.
 * Any other lane is the vertex output it reads, which the program computes from NORMAL0.
 * The UV stream is the lowest TEXCOORD input; the export writes the same stream.
 */
public sealed class ShaderVaryings
{
    // Program node operations; "uv" is UV map 0, "uvzw" the raw z and w, "color" COLOR0.
    internal const string Uv = "uv", UvZw = "uvzw", Color = "color";
    // POSITION0 before the model's offset and scale.
    internal const string RawPosition = "rawposition";
    // NORMAL0 in object space.
    internal const string Normal = "normal";
    // Per-placement decorator inputs: channels 0-3 the unpacked TEXCOORD7 record lanes, 4-7 BLENDINDICES0 AO.
    internal const string Instance = "instance";
    internal const int OcclusionChannel = 4;
    // World vector kinds: normal, tangent, signed bitangent, position, cross(N, T) and position minus camera.
    internal const int WorldNormal = 0, WorldTangent = 1, WorldBitangent = 2, WorldPosition = 3, WorldCross = 4, FromCamera = 5;
    private readonly Dictionary<(string Register, int Lane), (string Operation, int Channel)> inputs = [];
    private readonly Dictionary<(string Register, int Lane), float> constants = [];
    private readonly Dictionary<(string Register, int Lane), (int Kind, int Channel, float Sign)> world = [];
    private readonly HashSet<(string Register, int Lane)> linked = [];
    private readonly Dictionary<(string Register, int Lane), string> outputs = [];
    private readonly Dictionary<(string Register, int Lane), string> streams = [];
    /** The vertex shader text; it branches only when the entity run read it. */
    internal string Vertex { get; private init; } = "";
    // Entity draws bind CB11 row 7 as (0,0,0,1); an 8-row immediate CB11 is the entity layout without a palette.
    private static readonly float[] EntityRow7 = [0, 0, 0, 1];

    private sealed record Row(string Name, int Index, string Mask, int Register, string Format = "float");
    private readonly record struct Source(string Kind, int Lane);

    /**
     * Reads the stream each pixel input lane copies.
     * An entity vertex shader is also run on sample vertices; what that run names wins.
     * @param decoratorRows Vertex cb0 rows holding extern 72 vectors 6 and 7; null for a non-decorator material.
     * @param vertexConstants Vertex cb0 as the material's TFX fills it; NaN lanes are unknown.
     * @return Null when a shader has no signature, or a vertex shader that is not an entity shader branches.
     */
    public static ShaderVaryings? Read(string vertex, string pixel, (int Scale, int Offset)? decoratorRows = null,
        IReadOnlyList<Vector4>? vertexConstants = null)
    {
        var vertexInputs = Signature(vertex, "Input signature");
        var vertexOutputs = Signature(vertex, "Output signature");
        var pixelInputs = Signature(pixel, "Input signature");
        if (vertexInputs is null || vertexOutputs is null || pixelInputs is null) return null;
        bool branched = Regex.IsMatch(vertex, @"^\s*(?:if_nz|if_z|loop|switch|call|callc|retc_nz|retc_z)\b", RegexOptions.Multiline);
        var probed = EntityProbe.Read(vertex, vertexInputs, vertexOutputs, vertexConstants ?? []);
        if (branched && probed is null) return null;
        int uvIndex = vertexInputs.Where(row => row.Name == "TEXCOORD").Select(row => row.Index).DefaultIfEmpty(-1).Min();
        var lanes = new Dictionary<(string Register, int Lane), Source>();
        var known = new Dictionary<(string Register, int Lane), float>();
        bool entity = Regex.IsMatch(vertex, @"dcl_constantbuffer CB11\[8\], immediateIndexed");
        float? Known(string text, int lane)
        {
            text = text.Trim();
            bool negate = text.StartsWith('-'); if (negate) text = text[1..];
            bool absolute = text.StartsWith('|') && text.EndsWith('|'); if (absolute) text = text[1..^1];
            float? value = null;
            if (text.StartsWith("l(", StringComparison.Ordinal))
            {
                string[] parts = text[2..^1].Split(',');
                value = MaterialPreviewAnalyzer.ParseLiteral(parts[Math.Min(lane, parts.Length - 1)].Trim());
            }
            else if (Regex.Match(text, @"^(?<reg>[ro]\d+|cb11\[7\])(?:\.(?<swizzle>[xyzw]+))?$") is { Success: true } match)
            {
                string register = match.Groups["reg"].Value, swizzle = match.Groups["swizzle"].Value;
                int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
                value = register[0] == 'c' ? entity ? EntityRow7[component] : null
                    : known.TryGetValue((register, component), out float held) ? held : null;
            }
            if (value is not float result || !float.IsFinite(result)) return null;
            if (absolute) result = Math.Abs(result);
            return negate ? -result : result;
        }
        // A product with a zero factor is zero whatever the other factor holds.
        static float? Product(float? a, float? b) => a == 0 || b == 0 ? 0 : a * b;
        Source? Operand(string text, int lane)
        {
            var match = Regex.Match(text.Trim(), @"^(?<reg>[rvo]\d+)(?:\.(?<swizzle>[xyzw]+))?$");
            if (!match.Success) return null;
            string register = match.Groups["reg"].Value, swizzle = match.Groups["swizzle"].Value;
            int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
            if (register[0] != 'v') return lanes.TryGetValue((register, component), out var held) ? held : null;
            var row = vertexInputs.FirstOrDefault(value => value.Register == int.Parse(register[1..])
                && value.Mask.Contains("xyzw"[component]));
            if (row is { Name: "COLOR", Index: 0 }) return new(Color, component);
            if (row is { Name: "TEXCOORD" } && row.Index == uvIndex) return new(component < 2 ? Uv : UvZw, component & 1);
            // A decorator draw binds its AO bytes as BLENDINDICES0; elsewhere they are bone indices.
            if (decoratorRows is not null && row is { Name: "BLENDINDICES", Index: 0 }) return new(Instance, OcclusionChannel + component);
            return null;
        }
        (string Register, int Component)? Lane(string text, int lane)
        {
            var match = Regex.Match(text.Trim(), @"^(?<reg>v\d+|cb0\[\d+\])(?:\.(?<swizzle>[xyzw]+))?$");
            if (!match.Success) return null;
            string swizzle = match.Groups["swizzle"].Value;
            return (match.Groups["reg"].Value, swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]));
        }
        // cb0[Scale].c * TEXCOORD7.c + cb0[Offset].c is record lane c, unpacked by its payload's quantization.
        Source? Unpacked(string[] operands, int lane)
        {
            if (decoratorRows is not { } rows) return null;
            var (first, second, offset) = (Lane(operands[1], lane), Lane(operands[2], lane), Lane(operands[3], lane));
            var (scale, stream) = first?.Register == $"cb0[{rows.Scale}]" ? (first, second) : (second, first);
            if (scale?.Register != $"cb0[{rows.Scale}]" || offset?.Register != $"cb0[{rows.Offset}]" || stream is not { } record
                || record.Register[0] != 'v' || scale.Value.Component != record.Component || offset.Value.Component != record.Component)
                return null;
            var row = vertexInputs.FirstOrDefault(value => value.Register == int.Parse(record.Register[1..])
                && value.Mask.Contains("xyzw"[record.Component]));
            return row is { Name: "TEXCOORD", Index: 7 } ? new(Instance, record.Component) : null;
        }
        // Copies are traced only through straight-line code; a branched entity shader keeps what the run names.
        foreach (string raw in branched ? [] : vertex.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            int space = line.IndexOf(' ');
            string opcode = space < 0 ? line : line[..space];
            string[] args = space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..]);
            if (args.Length < 2) continue;
            var destination = Regex.Match(args[0], @"^(?<name>[ro]\d+)(?:\.(?<mask>[xyzw]+))?$");
            if (!destination.Success) continue;
            string name = destination.Groups["name"].Value, mask = destination.Groups["mask"].Value;
            bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
            string operation = saturate ? opcode[..^4] : opcode;
            float? Arg(int index, int lane) => index < args.Length ? Known(args[index], lane) : null;
            var folded = (mask.Length == 0 ? "xyzw" : mask).Select(channel => "xyzw".IndexOf(channel)).ToDictionary(lane => lane,
                lane => operation switch
                {
                    "mov" => Arg(1, lane),
                    "add" => Arg(1, lane) + Arg(2, lane),
                    "mul" => Product(Arg(1, lane), Arg(2, lane)),
                    "mad" => Product(Arg(1, lane), Arg(2, lane)) + Arg(3, lane),
                    _ => null
                });
            foreach (var (lane, value) in folded)
                if (value is float number) known[(name, lane)] = saturate ? Math.Clamp(number, 0, 1) : number;
                else known.Remove((name, lane));
            foreach (char channel in mask.Length == 0 ? "xyzw" : mask)
            {
                int lane = "xyzw".IndexOf(channel);
                Source? value = opcode switch
                {
                    "mov" => Operand(args[1], lane),
                    "mad" when args.Length == 4 && Unpacked(args, lane) is { } record => record,
                    // A transformed UV is the exported UV map, which carries the same transform.
                    "mad" when args.Length == 4 && Constant(args[3]) => Scaled(args[1], args[2], lane),
                    "mul" when args.Length == 3 => Scaled(args[1], args[2], lane),
                    "add" when args.Length == 3 => Scaled(args[1], args[2], lane),
                    _ => null
                };
                if (value is { } held) lanes[(name, lane)] = held;
                else lanes.Remove((name, lane));
            }
        }
        Source? Scaled(string first, string second, int lane)
        {
            var value = Constant(second) ? Operand(first, lane) : Constant(first) ? Operand(second, lane) : null;
            return value is { Kind: Uv } ? value : null;
        }
        var result = new ShaderVaryings { Vertex = vertex };
        foreach (var row in vertexInputs.Where(row => row is { Name: "NORMAL", Index: 0 }))
            foreach (char channel in row.Mask.Where(channel => channel is 'x' or 'y' or 'z'))
                result.streams[($"v{row.Register}", "xyzw".IndexOf(channel))] = Normal;
        var world = branched ? new Dictionary<int, int>() : WorldLanes(vertex, vertexInputs);
        foreach (var input in pixelInputs)
        {
            var output = vertexOutputs.FirstOrDefault(row => row.Name == input.Name && row.Index == input.Index);
            if (output is null) continue;
            foreach (char channel in input.Mask)
            {
                int lane = "xyzw".IndexOf(channel);
                if (lane < 0 || !output.Mask.Contains(channel)) continue;
                string register = $"v{input.Register}";
                result.linked.Add((register, lane));
                if (probed?.TryGetValue(($"o{output.Register}", lane), out var meaning) == true)
                {
                    if (meaning.Stream is { } stream) result.inputs[(register, lane)] = (stream, meaning.Channel);
                    else if (meaning.Constant is float value) result.constants[(register, lane)] = value;
                    else result.world[(register, lane)] = (meaning.Kind, meaning.Channel, meaning.Sign);
                }
                else if (lanes.TryGetValue(($"o{output.Register}", lane), out var source))
                    result.inputs[(register, lane)] = (source.Kind, source.Lane);
                else if (known.TryGetValue(($"o{output.Register}", lane), out float constant))
                    result.constants[(register, lane)] = constant;
                else if (world.TryGetValue(output.Register, out int kind) && lane < 3)
                    result.world[(register, lane)] = (kind, lane, 1);
                else result.outputs[(register, lane)] = $"o{output.Register}";
            }
        }
        return result;
    }

    /**
     * Output registers whose x, y and z hold a world normal (0), tangent (1), bitangent (2) or position (3).
     * A lane may read only its vertex streams, CB11, the vertex or instance id, and for the position CB12[7].
     */
    private static Dictionary<int, int> WorldLanes(string vertex, List<Row> vertexInputs)
    {
        var reads = new Dictionary<(string Register, int Lane), HashSet<string>>();
        HashSet<string> Sources(string text, int lane)
        {
            text = text.Trim().TrimStart('-').Trim('|');
            if (text.StartsWith("l(", StringComparison.Ordinal)) return [];
            var match = Regex.Match(text, @"^(?<kind>[a-z]+)(?<number>\d+)(?<index>\[[^\]]*\])?(?:\.(?<swizzle>[xyzw]+))?$");
            if (!match.Success) return [Other];
            string kind = match.Groups["kind"].Value, swizzle = match.Groups["swizzle"].Value;
            int number = int.Parse(match.Groups["number"].Value);
            int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
            switch (kind)
            {
                case "r" or "o":
                    return reads.TryGetValue(($"{kind}{number}", component), out var held) ? [.. held] : [];
                case "v":
                    var row = vertexInputs.FirstOrDefault(value => value.Register == number && value.Mask.Contains("xyzw"[component]));
                    return row is null ? [Other] : row.Name.StartsWith("SV_", StringComparison.Ordinal) ? []
                        : [$"{row.Name}{row.Index}.{"xyzw"[component]}"];
                // A register index into CB11 only picks the instance's rows.
                case "cb" when number == 11: return [Object];
                case "cb" when number == 12 && match.Groups["index"].Value == "[7]": return [Camera];
                default: return [Other];
            }
        }
        foreach (string raw in vertex.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)) continue;
            int space = line.IndexOf(' ');
            if (space < 0) continue;
            string opcode = line[..space].Replace("_sat", "", StringComparison.Ordinal);
            string[] args = DxbcText.SplitOperands(line[(space + 1)..]);
            if (args.Length < 2) continue;
            // These write a second destination register; both lanes become unknown.
            int first = opcode is "sincos" or "udiv" or "imul" or "umul" or "swapc" ? 2 : 1;
            bool resource = opcode.StartsWith("sample", StringComparison.Ordinal) || opcode.StartsWith("ld", StringComparison.Ordinal)
                || opcode.StartsWith("gather", StringComparison.Ordinal) || opcode is "resinfo" or "bufinfo";
            int dot = opcode is "dp2" or "dp3" or "dp4" ? opcode[^1] - '0' : 0;
            for (int target = 0; target < first; target++)
            {
                var destination = Regex.Match(args[target], @"^(?<name>[ro]\d+)(?:\.(?<mask>[xyzw]+))?$");
                if (!destination.Success) continue;
                string name = destination.Groups["name"].Value, mask = destination.Groups["mask"].Value;
                var values = (mask.Length == 0 ? "xyzw" : mask).ToDictionary(channel => "xyzw".IndexOf(channel), channel =>
                {
                    int lane = "xyzw".IndexOf(channel);
                    if (resource || first == 2) return new HashSet<string> { Other };
                    var union = new HashSet<string>();
                    foreach (string operand in args.Skip(first))
                        foreach (int source in dot == 0 ? [lane] : Enumerable.Range(0, dot)) union.UnionWith(Sources(operand, source));
                    return union;
                });
                foreach (var (lane, value) in values) reads[(name, lane)] = value;
            }
        }
        var result = new Dictionary<int, int>();
        foreach (var output in Signature(vertex, "Output signature") ?? [])
        {
            var lanes = Enumerable.Range(0, 3).Select(lane => reads.GetValueOrDefault(($"o{output.Register}", lane))).ToArray();
            var kinds = lanes.Select(value => value is null ? null : WorldKind(value)).Distinct().ToArray();
            // A vector reads x, y and z of its stream over its lanes; a broadcast scalar such as a height mask does not.
            string stream = kinds is [int found] ? found switch { WorldPosition => "POSITION0", 0 => "NORMAL0", _ => "TANGENT0" } : "";
            if (kinds is [int kind] && "xyz".All(axis => lanes.Any(value => value!.Contains($"{stream}.{axis}"))))
                result[output.Register] = kind;
        }
        return result;
    }

    // What a lane reads besides its vertex streams.
    private const string Other = "other", Object = "cb11", Camera = "cb12[7]";

    /** Normal, tangent, bitangent or position from the inputs one output lane reads; null for anything else. */
    private static int? WorldKind(HashSet<string> reads)
    {
        if (reads.Contains(Other) || !reads.Contains(Object)) return null;
        var streams = reads.Where(value => value.Contains('.')).ToArray();
        bool Has(string name) => streams.Any(value => value.StartsWith(name + ".", StringComparison.Ordinal));
        if (streams.Any(value => !value.StartsWith("POSITION0.", StringComparison.Ordinal)
            && !value.StartsWith("NORMAL0.", StringComparison.Ordinal) && !value.StartsWith("TANGENT0.", StringComparison.Ordinal))) return null;
        if (Has("POSITION0")) return Has("NORMAL0") || Has("TANGENT0") ? null : WorldPosition;
        if (reads.Contains(Camera)) return null;
        if (!Has("TANGENT0")) return Has("NORMAL0") ? 0 : null;
        // The bitangent carries the tangent's handedness in w.
        return streams.Contains("TANGENT0.w") ? Has("NORMAL0") ? 2 : null : 1;
    }

    /** An entity vertex shader that writes UV map 1, the detail UVs, to some output lane. */
    public static bool WritesDetailUv(string vertex) =>
        Signature(vertex, "Input signature") is { } inputs && Signature(vertex, "Output signature") is { } outputs
        && EntityProbe.Read(vertex, inputs, outputs, []) is { } probed && probed.Values.Any(value => value.Stream == Uv && value.Channel >= 2);

    /** The stream a pixel input lane copies; false for a lane the vertex shader computes. */
    internal bool TryGet(string register, int lane, out string operation, out int channel)
    {
        bool found = inputs.TryGetValue((register, lane), out var value);
        (operation, channel) = value;
        return found;
    }

    /**
     * A pixel input lane the vertex shader fills with one lane of a world-space vector.
     * @param kind Receives WorldNormal through FromCamera.
     * @param channel Receives the vector lane, 0 to 2.
     * @param sign Receives 1, or -1 for the negated vector.
     */
    internal bool TryWorld(string register, int lane, out int kind, out int channel, out float sign)
    {
        bool found = world.TryGetValue((register, lane), out var value);
        (kind, channel, sign) = value;
        return found;
    }

    /** A pixel input lane the vertex shader sets to the same value for every vertex. */
    internal bool TryConstant(string register, int lane, out float value) => constants.TryGetValue((register, lane), out value);

    /** The vertex shader writes this pixel input lane. */
    internal bool Writes(string register, int lane) => linked.Contains((register, lane));

    /** The vertex output register a computed pixel input lane reads; false for a copied stream or a constant. */
    internal bool TryOutput(string register, int lane, out string output) => outputs.TryGetValue((register, lane), out output!);

    /** The program operation for a vertex input lane, such as NORMAL0; false for a stream the program cannot read. */
    internal bool TryStream(string register, int lane, out string operation) => streams.TryGetValue((register, lane), out operation!);

    private static bool Constant(string operand)
    {
        string value = operand.Trim().TrimStart('-').Trim('|');
        return value.StartsWith("l(", StringComparison.Ordinal) || Regex.IsMatch(value, @"^cb\d+\[[^\]]*\](?:\.[xyzw]+)?$");
    }

    /** Rows of a disassembly signature comment, cut at the columns of its dash line. */
    private static List<Row>? Signature(string text, string title)
    {
        string[] lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int start = Array.FindIndex(lines, line => line.Trim() == $"// {title}:");
        if (start < 0) return null;
        int dashes = Array.FindIndex(lines, start, line => line.StartsWith("// ---", StringComparison.Ordinal));
        if (dashes < 0) return null;
        var columns = Regex.Matches(lines[dashes], "-+").Select(match => (match.Index, match.Length)).ToArray();
        if (columns.Length < 4) return null;
        var rows = new List<Row>();
        for (int index = dashes + 1; index < lines.Length && lines[index].Trim() != "//"; index++)
        {
            string line = lines[index];
            string Cell(int column) => columns[column].Index >= line.Length ? ""
                : line.Substring(columns[column].Index, Math.Min(columns[column].Length, line.Length - columns[column].Index)).Trim();
            // A system value such as oDepth has no register number.
            if (!int.TryParse(Cell(1), out int semantic) || !int.TryParse(Cell(3), out int register)) continue;
            rows.Add(new(Cell(0), semantic, Cell(2).Replace(" ", ""), register, columns.Length > 5 ? Cell(5) : "float"));
        }
        return rows;
    }

    /**
     * Names what an entity vertex shader writes to each output lane by running it on sample vertices.
     * CB11 holds the viewport's rest pose: rows 0 to 7 of the part, then every palette entry as the placement.
     */
    private static class EntityProbe
    {
        /** What a varying carries: a source stream and channel, a constant, or a kind with sign. */
        internal sealed record Meaning(string? Stream = null, int Channel = 0, float? Constant = null, int Kind = -1, float Sign = 1);
        private sealed record Instruction(string Opcode, bool Saturate, string[] Args);
        private readonly record struct Cell(uint Bits, bool Known)
        {
            internal float Float => BitConverter.UInt32BitsToSingle(Bits);
            /** A known float cell. */
            internal static Cell Of(float value) => new(BitConverter.SingleToUInt32Bits(value), true);
            /** A known integer cell. */
            internal static Cell Int(int value) => new((uint)value, true);
        }
        private static readonly Cell Unknown = new(0, false);

        // An arbitrary placement, object row and UV row, so world, object, raw and scaled lanes all differ.
        private static readonly Quaternion Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 3)), .7f);
        private static readonly Vector3 Offset = new(1.5f, -2, .7f), CameraPosition = new(3, -4, 5);
        // The clip run's view-projection: a plain shift, so a world position and a copy of the clip position differ.
        private static readonly Vector3 ClipShift = new(.5f, -.25f, .75f);
        private static readonly Vector4 ObjectRow = new(.25f, -.5f, .75f, 1.5f), UvRow = new(2, 3, .1f, .2f);
        // Entity draws bind CB11 row 7 as (0,0,0,1).
        private static readonly Vector4 Row7 = new(0, 0, 0, 1);
        // Every index a sample can form stays inside this window.
        private const int WindowRows = 4096, FirstPaletteRow = 8;
        // Rigid bone 5; BLENDINDICES bones 1 and 2 with 8-bit weights summing to 255; BLENDWEIGHT summing to 1.
        private const float RigidBone = 5 / 32767f;
        private static readonly int[] BlendIndices = [1, 2, 153, 102];
        private static readonly float[] BlendWeights = [.4f, .3f, .2f, .1f];
        private const int Samples = 4;
        private const float Tolerance = 2e-3f;
        private static readonly Regex OperandText = new(@"^(?<file>[a-z]+)(?<number>\d+)(?:\[(?<index>[^\]]+)\])?(?:\.(?<swizzle>[xyzw]+))?$");

        /** @return Null for a vertex shader that is not an entity layout this run can bind. */
        internal static Dictionary<(string Register, int Lane), Meaning>? Read(string vertex, List<Row> inputs, List<Row> outputs,
            IReadOnlyList<Vector4> constants)
        {
            if (Window(vertex) is not { } window || Parse(vertex) is not { } code) return null;
            int uvIndex = inputs.Where(row => row.Name == "TEXCOORD").Select(row => row.Index).DefaultIfEmpty(-1).Min();
            var random = new Random(1);
            float Next(float low, float high) => low + (float)random.NextDouble() * (high - low);
            var runs = new Dictionary<string, Cell[]>?[3, Samples];
            var vectors = new Dictionary<int, Vector3>[Samples];
            var streams = new List<(string Operation, int Channel, float[] Values)>();
            var values = new Dictionary<(string Operation, int Channel, int Variant), float[]>();
            for (int sample = 0; sample < Samples; sample++)
            {
                var normal = Vector3.Normalize(new(Next(-1, 1), Next(-1, 1), Next(-1, 1)));
                var tangent = Vector3.Normalize(Vector3.Cross(normal, Vector3.Normalize(new(Next(-1, 1), Next(-1, 1), Next(-1, 1)))));
                float handedness = sample % 2 == 0 ? 1 : -1;
                var position = new Vector3(Next(-.9f, .9f), Next(-.9f, .9f), Next(-.9f, .9f));
                var lanes = new Dictionary<int, Cell[]>();
                foreach (var row in inputs)
                {
                    bool integer = row.Format is "uint" or "int";
                    var cells = new Cell[4];
                    for (int lane = 0; lane < 4; lane++)
                    {
                        float value = (row.Name, row.Index) switch
                        {
                            ("POSITION", 0) => lane < 3 ? position[lane] : RigidBone,
                            ("NORMAL", 0) => lane < 3 ? normal[lane] : 0,
                            ("TANGENT", 0) => lane < 3 ? tangent[lane] : handedness,
                            ("BLENDINDICES", _) => integer ? BlendIndices[lane] : BlendIndices[lane] / 255f,
                            ("BLENDWEIGHT", _) => BlendWeights[lane],
                            _ when row.Name.StartsWith("SV_", StringComparison.Ordinal) => 0,
                            _ => integer ? random.Next(4) : Next(0, 1)
                        };
                        cells[lane] = integer ? Cell.Int((int)value) : Cell.Of(value);
                        if (integer || row.Name.StartsWith("SV_", StringComparison.Ordinal)) continue;
                        void Stream(string operation, int channel, float stored, int variant = 0)
                        {
                            if (!values.TryGetValue((operation, channel, variant), out var held)) values[(operation, channel, variant)] = held = new float[Samples];
                            held[sample] = stored;
                        }
                        if (row is { Name: "COLOR", Index: 0 }) Stream(Color, lane, value);
                        else if (row is { Name: "POSITION", Index: 0 } && lane < 3) Stream(RawPosition, lane, value);
                        else if (row is { Name: "NORMAL", Index: 0 } && lane < 3) Stream(Normal, lane, value);
                        else if (row.Name == "TEXCOORD" && row.Index == uvIndex)
                        {
                            Stream(lane < 2 ? Uv : UvZw, lane & 1, value);
                            // The exported UV map carries the row 6 transform.
                            if (lane < 2) Stream(Uv, lane, value * UvRow[lane] + UvRow[lane + 2], 1);
                        }
                    }
                    lanes[row.Register] = cells;
                }
                // The export writes UV map 1 as the transformed UV times the TEXCOORD2 stream.
                var detail = inputs.FirstOrDefault(row => row is { Name: "TEXCOORD", Index: 2 } && row.Index != uvIndex && row.Format == "float");
                if (detail is not null && values.TryGetValue((Uv, 0, 1), out var u) && values.TryGetValue((Uv, 1, 1), out var v))
                    for (int lane = 0; lane < 2; lane++)
                    {
                        if (!values.TryGetValue((Uv, lane + 2, 2), out var held)) values[(Uv, lane + 2, 2)] = held = new float[Samples];
                        held[sample] = (lane == 0 ? u : v)[sample] * lanes[detail.Register][lane].Float;
                    }
                var worldNormal = Vector3.Transform(normal, Rotation);
                var worldTangent = Vector3.Transform(tangent, Rotation);
                var cross = Vector3.Cross(worldNormal, worldTangent);
                var worldPosition = Vector3.Transform(position * ObjectRow.W + new Vector3(ObjectRow.X, ObjectRow.Y, ObjectRow.Z), Rotation) + Offset;
                vectors[sample] = new()
                {
                    [WorldNormal] = worldNormal, [WorldTangent] = worldTangent, [WorldBitangent] = cross * handedness,
                    [WorldCross] = cross, [WorldPosition] = worldPosition, [FromCamera] = worldPosition - CameraPosition
                };
                for (int zero = 0; zero < 2; zero++)
                    if (Execute(code, lanes, window, constants, zero == 1) is { } run) runs[zero, sample] = run;
                    else return null;
                runs[2, sample] = Execute(code, lanes, window, constants, false, clip: true);
            }
            foreach (var ((operation, channel, _), held) in values) streams.Add((operation, channel, held));
            Cell Lane(int zero, int sample, string register, int lane) =>
                runs[zero, sample]?.TryGetValue(register, out var cells) == true ? cells[lane] : Unknown;
            bool Match(int zero, string register, int lane, Func<int, float> expected) => Enumerable.Range(0, Samples).All(sample =>
                Lane(zero, sample, register, lane) is { Known: true } cell && MathF.Abs(cell.Float - expected(sample)) <= Tolerance * MathF.Max(1, MathF.Abs(expected(sample))));
            // Blender draws the undisplaced mesh, so a vector is matched again with material constants and textures at zero.
            bool Vector(string register, int lane, Func<int, float> expected) => Match(0, register, lane, expected) || Match(1, register, lane, expected);
            int[] kinds = [WorldNormal, WorldTangent, WorldBitangent, WorldCross, WorldPosition, FromCamera];
            var result = new Dictionary<(string Register, int Lane), Meaning>();
            foreach (var output in outputs.Where(row => !row.Name.StartsWith("SV_", StringComparison.Ordinal)))
            {
                string register = $"o{output.Register}";
                var mask = output.Mask.Select(channel => "xyzw".IndexOf(channel)).Where(lane => lane >= 0).ToList();
                if (mask.Contains(0) && mask.Contains(1) && mask.Contains(2))
                    foreach (int kind in kinds)
                        foreach (float sign in new[] { 1f, -1f })
                            if (!result.ContainsKey((register, 0)) && Enumerable.Range(0, 3).All(lane => Vector(register, lane, sample => sign * vectors[sample][kind][lane])))
                                for (int lane = 0; lane < 3; lane++) result[(register, lane)] = new(Kind: kind, Channel: lane, Sign: sign);
                foreach (int lane in mask.Where(lane => !result.ContainsKey((register, lane))))
                {
                    var first = Lane(0, 0, register, lane);
                    if (first.Known && float.IsFinite(first.Float) && Match(0, register, lane, _ => first.Float))
                    { result[(register, lane)] = new(Constant: first.Float); continue; }
                    int copied = streams.FindIndex(stream => Match(0, register, lane, sample => stream.Values[sample]));
                    if (copied >= 0) { result[(register, lane)] = new(streams[copied].Operation, streams[copied].Channel); continue; }
                    foreach (int kind in kinds)
                        for (int channel = 0; channel < 3 && !result.ContainsKey((register, lane)); channel++)
                            if (Vector(register, lane, sample => vectors[sample][kind][channel]))
                                result[(register, lane)] = new(Kind: kind, Channel: channel);
                }
            }
            // A shader that moves its vertices writes the moved position; Blender draws it moved, so it is still the world position.
            if (outputs.FirstOrDefault(row => row.Name == "SV_POSITION") is { } clip)
                foreach (var output in outputs.Where(row => !row.Name.StartsWith("SV_", StringComparison.Ordinal)))
                {
                    string register = $"o{output.Register}";
                    if (Enumerable.Range(0, 3).Any(lane => result.ContainsKey((register, lane)))) continue;
                    // The clip transform reads the world position, or its offset from the camera.
                    foreach (var origin in new[] { Vector3.Zero, CameraPosition })
                        if (!result.ContainsKey((register, 0)) && Enumerable.Range(0, 3).All(lane => Enumerable.Range(0, Samples).All(sample =>
                            Lane(2, sample, register, lane) is { Known: true } moved && Lane(2, sample, $"o{clip.Register}", lane) is { Known: true } at
                            && MathF.Abs(moved.Float - at.Float + ClipShift[lane] - origin[lane]) <= Tolerance * MathF.Max(1, MathF.Abs(moved.Float)))))
                            for (int lane = 0; lane < 3; lane++) result[(register, lane)] = new(Kind: WorldPosition, Channel: lane);
                }
            return result;
        }

        /** CB11 rows of the rest pose for this layout; null when it is not an entity layout. */
        private static Vector4[]? Window(string vertex)
        {
            var placement = Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Offset);
            var rows = new Vector4[WindowRows];
            rows[0] = new(placement.M11, placement.M12, placement.M13, placement.M14);
            rows[1] = new(placement.M21, placement.M22, placement.M23, placement.M24);
            rows[2] = new(placement.M31, placement.M32, placement.M33, placement.M34);
            rows[3] = new(placement.M41, placement.M42, placement.M43, placement.M44);
            // A part without a palette may scale per axis by row 4; that scale matches row 5's, and row 4 w stays unknown.
            rows[4] = new(ObjectRow.W, ObjectRow.W, ObjectRow.W, float.NaN);
            (rows[5], rows[6], rows[7]) = (ObjectRow, UvRow, Row7);
            if (Regex.IsMatch(vertex, @"dcl_constantbuffer CB11\[8\], immediateIndexed")) return rows;
            if (!Regex.IsMatch(vertex, @"dcl_constantbuffer CB11\[\d+\], dynamicIndexed")) return null;
            var offsets = Regex.Matches(vertex, @"cb11\[r\d+\.[xyzw] \+ (\d+)\]").Select(match => match.Groups[1].Value).ToArray();
            if (offsets.Length == 0 || offsets.Any(offset => offset != "8")) return null;
            switch (NativeShaderContracts.ClassifyPaletteIndex(vertex))
            {
                case NativeEntityPalette.DualQuaternion:
                    // qd = 0.5 * (t, 0) * qr, as the viewport packs it.
                    var axis = new Vector3(Rotation.X, Rotation.Y, Rotation.Z);
                    var dual = .5f * (Rotation.W * Offset + Vector3.Cross(Offset, axis));
                    for (int row = FirstPaletteRow; row + 2 <= WindowRows; row += 2)
                    {
                        rows[row] = new(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
                        rows[row + 1] = new(dual, -.5f * Vector3.Dot(Offset, axis));
                    }
                    return rows;
                case NativeEntityPalette.Matrix:
                    for (int row = FirstPaletteRow; row + 3 <= WindowRows; row += 3) Transposed(row);
                    return rows;
                // An index shifted by 2 reads four rows per entry: the transposed matrix, then a row left unknown.
                case NativeEntityPalette.None when Regex.IsMatch(vertex, @"^ishl r\d+\.[xyzw]+, [rv]\d+\.[xyzw]+, l\(2\)\r?$", RegexOptions.Multiline):
                    for (int row = FirstPaletteRow; row + 4 <= WindowRows; row += 4)
                    {
                        Transposed(row);
                        rows[row + 3] = new(float.NaN);
                    }
                    return rows;
                default: return null;
            }
            void Transposed(int row)
            {
                rows[row] = new(placement.M11, placement.M21, placement.M31, placement.M41);
                rows[row + 1] = new(placement.M12, placement.M22, placement.M32, placement.M42);
                rows[row + 2] = new(placement.M13, placement.M23, placement.M33, placement.M43);
            }
        }

        private static List<Instruction>? Parse(string vertex)
        {
            var code = new List<Instruction>();
            foreach (string raw in vertex.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("dcl_", StringComparison.Ordinal)
                    || line.StartsWith("vs_", StringComparison.Ordinal)) continue;
                int space = line.IndexOf(' ');
                string opcode = space < 0 ? line : line[..space];
                if (opcode is "loop" or "endloop" or "switch" or "call" or "callc" or "retc_nz" or "retc_z" or "breakc_nz" or "breakc_z") return null;
                bool saturate = opcode.EndsWith("_sat", StringComparison.Ordinal);
                code.Add(new(saturate ? opcode[..^4] : opcode, saturate, space < 0 ? [] : DxbcText.SplitOperands(line[(space + 1)..])));
            }
            return code;
        }

        /**
         * Runs the shader once. Both sides of a branch run; an unknown condition keeps only lanes they agree on.
         * @param zero Vertex cb0, unknown constants and texture reads give zero instead of their value.
         * @param clip View rows 0 to 3 move by ClipShift without turning, and row 13 is zero.
         * @return The output registers; null for an instruction this run cannot follow.
         */
        private static Dictionary<string, Cell[]>? Execute(List<Instruction> code, Dictionary<int, Cell[]> inputs, Vector4[] window,
            IReadOnlyList<Vector4> constants, bool zero, bool clip = false)
        {
            var registers = new Dictionary<string, Cell[]>();
            var scopes = new Stack<(Dictionary<string, Cell[]> Before, Cell Condition, Dictionary<string, Cell[]>? First)>();
            Cell Constant(int slot, int row, int component)
            {
                float value = slot == 11 && row >= 0 && row < window.Length ? window[row][component]
                    : slot == 0 && !zero && row >= 0 && row < constants.Count ? constants[row][component]
                    : slot == 12 && row == 7 && component < 3 ? CameraPosition[component]
                    : slot == 12 && clip && row < 4 ? row == 3 && component < 3 ? ClipShift[component] : row == component ? 1 : 0
                    : slot == 12 && clip && row == 13 ? 0 : float.NaN;
                return !float.IsNaN(value) ? Cell.Of(value) : zero ? Cell.Of(0) : Unknown;
            }
            Cell Read(string text, int lane, bool integer)
            {
                text = text.Trim();
                bool negate = text.StartsWith('-'); if (negate) text = text[1..];
                bool absolute = text.StartsWith('|') && text.EndsWith('|'); if (absolute) text = text[1..^1];
                Cell cell;
                if (text.StartsWith("l(", StringComparison.Ordinal))
                {
                    string[] parts = text[2..^1].Split(',');
                    string literal = parts[Math.Min(lane, parts.Length - 1)].Trim();
                    cell = Regex.IsMatch(literal, @"^-?\d+$") ? Cell.Int(int.Parse(literal, System.Globalization.CultureInfo.InvariantCulture))
                        : Cell.Of(MaterialPreviewAnalyzer.ParseLiteral(literal));
                }
                else if (OperandText.Match(text) is { Success: true } match)
                {
                    string file = match.Groups["file"].Value, swizzle = match.Groups["swizzle"].Value;
                    int number = int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    int component = swizzle.Length == 0 ? lane : "xyzw".IndexOf(swizzle[Math.Min(lane, swizzle.Length - 1)]);
                    cell = file switch
                    {
                        "r" or "o" => registers.TryGetValue(file + number, out var held) ? held[component] : Unknown,
                        "v" => inputs.TryGetValue(number, out var input) ? input[component] : Unknown,
                        "cb" when match.Groups["index"].Success && Index(match.Groups["index"].Value) is int row => Constant(number, row, component),
                        _ => Unknown
                    };
                }
                else cell = Unknown;
                if (!cell.Known || !negate && !absolute) return cell;
                if (integer) { int value = (int)cell.Bits; return Cell.Int(negate ? -(absolute ? Math.Abs(value) : value) : Math.Abs(value)); }
                float magnitude = absolute ? MathF.Abs(cell.Float) : cell.Float;
                return Cell.Of(negate ? -magnitude : magnitude);
            }
            int? Index(string text)
            {
                var match = Regex.Match(text.Trim(), @"^(?:(?<register>[rv]\d+\.[xyzw])(?:\s*\+\s*)?)?(?<offset>\d+)?$");
                if (!match.Success) return null;
                int row = match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (!match.Groups["register"].Success) return row;
                var cell = Read(match.Groups["register"].Value, 0, true);
                return cell.Known && cell.Bits < WindowRows ? row + (int)cell.Bits : null;
            }
            foreach (var instruction in code)
            {
                string opcode = instruction.Opcode;
                string[] args = instruction.Args;
                if (opcode == "ret") break;
                if (opcode is "if_nz" or "if_z")
                {
                    var condition = Read(args[0], 0, true);
                    if (condition.Known) condition = Cell.Int((condition.Bits != 0) == (opcode == "if_nz") ? 1 : 0);
                    scopes.Push((Clone(registers), condition, null));
                    continue;
                }
                if (opcode == "else" && scopes.TryPop(out var open))
                {
                    scopes.Push((open.Before, open.Condition, Clone(registers)));
                    registers = Clone(open.Before);
                    continue;
                }
                if (opcode == "endif" && scopes.TryPop(out var scope))
                {
                    var taken = scope.First ?? registers;
                    var skipped = scope.First is null ? scope.Before : registers;
                    registers = scope.Condition.Known ? scope.Condition.Bits != 0 ? taken : skipped : Merge(taken, skipped);
                    continue;
                }
                int targets = opcode is "sincos" or "imul" or "umul" or "udiv" or "swapc" ? 2 : 1;
                if (args.Length <= targets) continue;
                string[] sources = args[targets..];
                var writes = new List<(string Register, int Lane, Cell Value)>();
                for (int target = 0; target < targets; target++)
                {
                    var destination = Regex.Match(args[target], @"^(?<name>[ro]\d+)(?:\.(?<mask>[xyzw]+))?$");
                    if (!destination.Success) continue;
                    string mask = destination.Groups["mask"].Value;
                    foreach (char channel in mask.Length == 0 ? "xyzw" : mask)
                    {
                        int lane = "xyzw".IndexOf(channel);
                        var value = Evaluate(opcode, target, lane, sources, Read, zero);
                        if (instruction.Saturate && value.Known) value = Cell.Of(float.IsNaN(value.Float) ? 0 : Math.Clamp(value.Float, 0, 1));
                        writes.Add((destination.Groups["name"].Value, lane, value));
                    }
                }
                foreach (var (name, lane, value) in writes)
                {
                    if (!registers.TryGetValue(name, out var cells)) registers[name] = cells = [Unknown, Unknown, Unknown, Unknown];
                    cells[lane] = value;
                }
            }
            return registers.Where(pair => pair.Key[0] == 'o').ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        /** One destination lane; a lane with an unknown operand is unknown. */
        private static Cell Evaluate(string opcode, int target, int lane, string[] sources, Func<string, int, bool, Cell> read, bool zero)
        {
            if (opcode.StartsWith("sample", StringComparison.Ordinal) || opcode.StartsWith("ld", StringComparison.Ordinal)
                || opcode.StartsWith("gather", StringComparison.Ordinal))
                return zero ? Cell.Of(0) : Unknown;
            // With constants and textures at zero, a product with a zero factor is zero whatever the other factor holds.
            float Product(float a, float b) => zero && (a == 0 || b == 0) ? 0 : a * b;
            if (opcode is "dp2" or "dp3" or "dp4")
            {
                float sum = 0;
                for (int index = 0; index < opcode[^1] - '0'; index++)
                {
                    Cell a = read(sources[0], index, false), b = read(sources[1], index, false);
                    if (!a.Known || !b.Known) return Unknown;
                    sum += Product(a.Float, b.Float);
                }
                return Cell.Of(sum);
            }
            if (opcode == "movc")
            {
                Cell condition = read(sources[0], lane, true), first = read(sources[1], lane, false), second = read(sources[2], lane, false);
                return condition.Known ? condition.Bits != 0 ? first : second : first == second ? first : Unknown;
            }
            bool integer = opcode is "iadd" or "imul" or "imad" or "ishl" or "ishr" or "ushr" or "and" or "or" or "xor" or "not" or "ineg"
                or "ieq" or "ine" or "ilt" or "ige" or "ult" or "uge" or "umin" or "umax" or "imin" or "imax" or "bfi" or "ubfe" or "ibfe"
                or "utof" or "itof" or "udiv" or "umul";
            var cells = sources.Select(source => read(source, lane, integer)).ToArray();
            if (cells.Any(cell => !cell.Known)) return Unknown;
            float F(int index) => cells[index].Float;
            uint U(int index) => cells[index].Bits;
            int I(int index) => (int)cells[index].Bits;
            static Cell Mask(bool value) => new(value ? uint.MaxValue : 0, true);
            return opcode switch
            {
                "mov" => cells[0],
                "add" => Cell.Of(F(0) + F(1)), "mul" => Cell.Of(Product(F(0), F(1))), "mad" => Cell.Of(Product(F(0), F(1)) + F(2)),
                "div" => Cell.Of(F(0) / F(1)),
                // Direct3D min and max return the operand that is not NaN.
                "min" => Cell.Of(float.IsNaN(F(0)) ? F(1) : float.IsNaN(F(1)) ? F(0) : MathF.Min(F(0), F(1))),
                "max" => Cell.Of(float.IsNaN(F(0)) ? F(1) : float.IsNaN(F(1)) ? F(0) : MathF.Max(F(0), F(1))),
                "sqrt" => Cell.Of(MathF.Sqrt(F(0))), "rsq" => Cell.Of(1 / MathF.Sqrt(F(0))), "rcp" => Cell.Of(1 / F(0)),
                "exp" => Cell.Of(MathF.Pow(2, F(0))), "log" => Cell.Of(MathF.Log2(F(0))), "frc" => Cell.Of(F(0) - MathF.Floor(F(0))),
                "round_ne" => Cell.Of(MathF.Round(F(0), MidpointRounding.ToEven)), "round_ni" => Cell.Of(MathF.Floor(F(0))),
                "round_pi" => Cell.Of(MathF.Ceiling(F(0))), "round_z" => Cell.Of(MathF.Truncate(F(0))),
                "sincos" => Cell.Of(target == 0 ? MathF.Sin(F(0)) : MathF.Cos(F(0))),
                "lt" => Mask(F(0) < F(1)), "ge" => Mask(F(0) >= F(1)), "eq" => Mask(F(0) == F(1)), "ne" => Mask(F(0) != F(1)),
                "ftou" => new(float.IsNaN(F(0)) || F(0) <= 0 ? 0 : F(0) >= 4294967295f ? uint.MaxValue : (uint)F(0), true),
                "ftoi" => Cell.Int(float.IsNaN(F(0)) ? 0 : (int)Math.Clamp(F(0), int.MinValue, int.MaxValue)),
                "utof" => Cell.Of(U(0)), "itof" => Cell.Of(I(0)),
                "iadd" => Cell.Int(I(0) + I(1)), "imul" when target == 1 => Cell.Int(I(0) * I(1)),
                "umul" when target == 1 => new(U(0) * U(1), true), "imad" => Cell.Int(I(0) * I(1) + I(2)),
                "udiv" => U(1) == 0 ? new(uint.MaxValue, true) : new(target == 0 ? U(0) / U(1) : U(0) % U(1), true),
                "ishl" => new(U(0) << (I(1) & 31), true), "ushr" => new(U(0) >> (I(1) & 31), true), "ishr" => Cell.Int(I(0) >> (I(1) & 31)),
                "and" => new(U(0) & U(1), true), "or" => new(U(0) | U(1), true), "xor" => new(U(0) ^ U(1), true), "not" => new(~U(0), true),
                "ineg" => Cell.Int(-I(0)), "ieq" => Mask(U(0) == U(1)), "ine" => Mask(U(0) != U(1)), "ilt" => Mask(I(0) < I(1)),
                "ige" => Mask(I(0) >= I(1)), "ult" => Mask(U(0) < U(1)), "uge" => Mask(U(0) >= U(1)),
                "umin" => new(Math.Min(U(0), U(1)), true), "umax" => new(Math.Max(U(0), U(1)), true),
                "imin" => Cell.Int(Math.Min(I(0), I(1))), "imax" => Cell.Int(Math.Max(I(0), I(1))),
                "bfi" => BitInsert(I(0) & 31, I(1) & 31, U(2), U(3)),
                "ubfe" => BitExtract(I(0) & 31, I(1) & 31, U(2)),
                _ => Unknown
            };
        }

        private static Cell BitInsert(int width, int offset, uint source, uint target)
        {
            uint mask = (uint)(((1UL << width) - 1) << offset);
            return new(source << offset & mask | target & ~mask, true);
        }

        private static Cell BitExtract(int width, int offset, uint source) => new(width == 0 ? 0
            : width + offset < 32 ? source << (32 - width - offset) >> (32 - width) : source >> offset, true);

        private static Dictionary<string, Cell[]> Clone(Dictionary<string, Cell[]> registers) =>
            registers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        private static Dictionary<string, Cell[]> Merge(Dictionary<string, Cell[]> a, Dictionary<string, Cell[]> b) =>
            a.Keys.Union(b.Keys).ToDictionary(key => key, key => Enumerable.Range(0, 4).Select(lane =>
                a.TryGetValue(key, out var left) && b.TryGetValue(key, out var right) && left[lane] == right[lane] ? left[lane] : Unknown).ToArray());
    }
}
