using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Behavior graphs are parsed as data and never execute package expressions. */
public sealed class BehaviorInspector : IAssetInspector
{
    // Shared and null rows count toward the editor's graph size limit.
    private const int MaximumEntries = 8192;
    // Shared source offsets decode once within this per-inspection byte budget.
    private const int MaximumSourceBytes = 1024 * 1024;
    public string Id => "sunrise.behavior";
    public string DisplayName => "Behavior";
    /** True for the behavior tag class (0x8080941E). */
    public bool CanInspect(AssetInspectionContext context) => context.ClassHash == 0x8080941E;

    private sealed record NodeLayout(string Name, int Size, int[] Roots,
        int Source = -1, bool Link = false, bool Known = false, int Expression = -1);

    private static readonly Dictionary<uint, NodeLayout> Layouts = new()
    {
        [0x80804D5E] = new("Condition structure", 0x10, []),
        [0x80804D71] = new("Condition leaf", 8, []),
        [0x80804D72] = new("Condition leaf", 4, []),
        [0x80804D73] = new("Condition structure", 0x60, []),
        [0x80804D74] = new("Condition leaf", 0x0C, []),
        [0x80804D75] = new("Condition leaf", 4, []),
        [0x80804D76] = new("Condition leaf", 1, []),
        [0x80804D78] = new("Condition structure", 0x10, []),
        [0x80804D7A] = new("Condition leaf", 8, []),
        [0x80804D7B] = new("Condition leaf", 1, []),
        [0x80804D7C] = new("Condition leaf", 1, []),
        [0x80804D80] = new("Condition leaf", 1, []),
        [0x80804D81] = new("Condition leaf", 4, []),
        [0x80804D82] = new("Condition leaf", 0x0C, []),
        [0x80804D83] = new("Condition leaf", 4, []),
        [0x80809310] = new("Condition group", 0x18, []),
        [0x80804E0E] = new("Town presence", 1, []),
        [0x80804E12] = new("Resource behavior", 0x20, [8]),
        [0x80804E13] = new("Resource leaf", 1, []),
        [0x80804E14] = new("Resource leaf", 1, []),
        [0x80804E15] = new("Resource leaf", 1, []),
        [0x80804E1C] = new("Select", 0x60, [0x18, 0x30, 0x48]),
        [0x80802907] = new("Relative teleportation", 0x58, [], 8, Expression: 0x10),
        [0x80802DA6] = new("Channel link", 0x0C, []),
        [0x80804E0F] = new("Timer", 0x58, [], 8, Expression: 0x10),
        [0x80804E10] = new("Target", 0x10, []),
        [0x80804E17] = new("Spawn projectile", 0x20, [], 0, true),
        [0x80804E19] = new("Spawn", 0x20, [], 0, true),
        [0x80804E23] = new("Search", 0x30, [0x10]),
        [0x80804E27] = new("Resource", 0x10, []),
        [0x80804E30] = new("Remove hop on", 0x0C, []),
        [0x80804E31] = new("Redirect", 0x20, [8], Known: true),
        [0x80804E35] = new("Player interaction", 0xA8, []),
        [0x80802A66] = new("Extended player interaction", 0xB8, []),
        [0x80804E38] = new("Navpoint", 8, []),
        [0x80804E39] = new("Investment unlock flag", 0x38, [8, 0x20]),
        [0x80804E3A] = new("Kill", 1, []),
        [0x80804E3B] = new("Incident", 4, []),
        [0x80804E3C] = new("Global", 0x28, [8]),
        [0x80804E3D] = new("Filter", 0x68, [0x28, 0x48], Known: true),
        [0x80804E3E] = new("External behavior", 0x10, [], 0, true, true),
        [0x80804E3F] = new("Defer", 0x18, [0]),
        [0x80804E40] = new("Counter", 0x50, [], 8, Known: true, Expression: 0x10),
        [0x80804E41] = new("Add hop on", 0x10, [], 0, true, true),
        [0x80802A6E] = new("Exit seat", 1, []),
        [0x80802A6F] = new("Storage channel", 0x50, [], 8, Expression: 0x10),
        [0x80802DA7] = new("Spawn random", 0x38, [], 0, true),
        [0x80804D7D] = new("Channel comparison", 0x90, [], 0, Known: true)
    };

    /** Cyclic pointers and excessive nesting are reported instead of followed. */
    public AssetInspection Inspect(AssetInspectionContext context)
    {
        var r = new TagReader(context.Data);
        HashSet<int> visited = [];
        List<AssetReference> references = [];
        List<string> diagnostics = [];
        Dictionary<string, AssetGraphNode> graphNodes = [];
        List<AssetGraphEdge> graphEdges = [];
        Dictionary<uint, string?> channelNames = [];
        Dictionary<int, string> sourceTexts = [];
        int sourceBytes = 0;
        int total = 0;
        void Count(int count)
        {
            if (count > MaximumEntries - total) throw new InvalidDataException("Behavior graph exceeds the view limit.");
            total += count;
        }
        AssetNode Root(int offset, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("Behavior graph exceeds the view limit.");
            Count(1);
            var array = r.Array(offset + 8, 0x80809420, 16);
            Count(array.Count);
            string rootId = $"root:{offset}";
            graphNodes.TryAdd(rootId, new(rootId, "Graph", offset));
            List<AssetNode> children = [];
            for (int i = 0; i < array.Count; i++)
            {
                int p = array.Data + i * 16;
                int node = r.Relative(p + 8);
                if (node >= 0) graphEdges.Add(new(rootId, $"node:{node}", $"Element {i}"));
                children.Add(new($"Element {i}", [r.UInt("Name hash", p, hex: true)],
                    node < 0 ? [] : [ReadNode(node, depth + 1)]));
            }
            return new($"Graph {offset:X}", [], children);
        }
        AssetNode ReadNode(int offset, int depth)
        {
            if (!visited.Add(offset)) return new($"Shared node {offset:X}", []);
            if (depth > 64) throw new InvalidDataException("Behavior graph exceeds the view limit.");
            Count(1);
            if (!r.Has(offset - 4, 4)) throw new InvalidDataException("Truncated behavior node.");
            uint type = r.U32(offset - 4);
            string nodeId = $"node:{offset}";
            List<AssetProperty> properties = [r.UInt("Class", offset - 4, hex: true)];
            if (!Layouts.TryGetValue(type, out var layout))
            {
                diagnostics.Add($"Unknown behavior class {type:X8}.");
                graphNodes.TryAdd(nodeId, new(nodeId, $"Unknown {type:X8}", offset));
                return new($"Unknown node {offset:X}", properties);
            }
            graphNodes.TryAdd(nodeId, new(nodeId, layout.Name, offset));
            foreach (int child in layout.Roots)
                graphEdges.Add(new(nodeId, $"root:{offset + child}", "Graph"));
            if (!r.Has(offset, layout.Size)) throw new InvalidDataException("Truncated behavior record.");
            properties.Add(new("Coverage", layout.Known ? "Known structure" : "Partial"));
            if (layout.Source >= 0)
            {
                int text = r.Relative(offset + layout.Source);
                if (text >= 0)
                {
                    string source = SourceText(text);
                    properties.Add(new("Source", source, text));
                }
            }
            if (layout.Link)
            {
                uint tag = r.U32(offset + 8);
                references.Add(new(offset + 8, tag, context.ResolveName?.Invoke(tag) ?? $"{tag:X8}"));
                properties.Add(r.UInt("Target", offset + 8, true, true));
            }
            if (type is 0x80804E40 or 0x80802A6F)
                properties.Add(r.UInt("Target hash", offset, true, true));
            List<AssetNode> children = layout.Roots.Select(x => Root(offset + x, depth + 1)).ToList();
            if (type == 0x80804E3D) children.Add(Conditions(nodeId, offset + 0x18, depth + 1));
            if (type == 0x80809310) children.Add(Conditions(nodeId, offset + 8, depth + 1));
            if (type == 0x80804E10 && r.Relative(offset + 8) is int resource && resource >= 0)
            {
                graphEdges.Add(new(nodeId, $"node:{resource}", "Operation"));
                children.Add(ReadNode(resource, depth + 1));
            }
            if (layout.Expression >= 0) children.Add(Expression(nodeId, offset + layout.Expression, "Value"));
            if (type == 0x80804D7D)
            {
                properties.Add(r.Byte("Comparison mode", offset + 0x88, true));
                children.Add(Expression(nodeId, offset + 8, "Left"));
                children.Add(Expression(nodeId, offset + 0x48, "Right"));
            }
            return new($"{layout.Name} {offset:X}", properties, children);
        }

        AssetNode Conditions(string owner, int field, int depth)
        {
            var array = r.Array(field, stride: 16);
            if (array.Count != 0 && array.Class is not (0x80809311 or 0x80809316))
                throw new InvalidDataException("Invalid behavior condition array.");
            Count(array.Count);
            List<AssetNode> children = [];
            for (int i = 0; i < array.Count; i++)
            {
                int target = r.Relative(array.Data + i * 16 + 8);
                if (target < 0) continue;
                graphEdges.Add(new(owner, $"node:{target}", $"Condition {i}"));
                children.Add(ReadNode(target, depth));
            }
            return new("Conditions", [], children);
        }

        AssetNode Expression(string owner, int offset, string name)
        {
            Count(1);
            BehaviorExpression expression;
            try
            {
                expression = BehaviorExpression.Read(context.Data, offset, hash => channelNames.GetValueOrDefault(hash));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
            {
                diagnostics.Add($"Expression {offset:X}: {error.Message}");
                return new(name, [new("Status", error.Message)]);
            }
            Count(expression.Constants.Count + expression.Inputs.Count);
            string id = $"expression:{offset}";
            string label = expression.Output ?? expression.Diagnostic ?? "Empty expression";
            graphNodes.TryAdd(id, new(id, label.Length > 120 ? label[..117] + "..." : label, offset));
            graphEdges.Add(new(owner, id, name));
            List<AssetProperty> properties = [new("Value", expression.Output ?? ""),
                new("Native evaluator", expression.NativeOverride.ToString())];
            if (expression.Diagnostic is { } diagnostic)
            { diagnostics.Add($"Expression {offset:X}: {diagnostic}."); properties.Add(new("Status", diagnostic)); }
            var constants = expression.Constants.Select(value => new AssetNode($"Constant {value.Index}",
                [r.Float("X", value.Offset), r.Float("Y", value.Offset + 4),
                    r.Float("Z", value.Offset + 8), r.Float("W", value.Offset + 12)])).ToArray();
            var inputs = expression.Inputs.Select((value, index) => new AssetNode($"Input {index}",
                [new("Name", channelNames.GetValueOrDefault(value.Hash) ?? ""), r.Byte("Object selector", value.Offset),
                    r.UInt("Channel", value.Offset + 4, hex: true)])).ToArray();
            return new(name, properties, [new("Constants", [], constants), new("Inputs", [], inputs)]);
        }

        string SourceText(int offset)
        {
            if (sourceTexts.TryGetValue(offset, out string? found)) return found;
            var remaining = context.Data.Span[offset..];
            int allowance = MaximumSourceBytes - sourceBytes;
            int end = remaining[..Math.Min(remaining.Length, allowance + 1)].IndexOf((byte)0);
            if (end < 0)
                throw new InvalidDataException(remaining.Length > allowance
                    ? "Behavior source text exceeds the view limit." : "Unterminated behavior source string.");
            sourceBytes += end;
            string text = Encoding.UTF8.GetString(remaining[..end]);
            sourceTexts.Add(offset, text);
            RememberChannels(text);
            return text;
        }

        void RememberChannels(string source)
        {
            static bool Start(char c) => char.IsAsciiLetter(c) || c == '_';
            static bool Part(char c) => Start(c) || char.IsAsciiDigit(c);
            for (int at = 0; at < source.Length; at++)
            {
                if (!Start(source[at])) continue;
                int first = at++;
                while (at < source.Length && Part(source[at])) at++;
                if (at - first > 256) continue;
                string name = source[first..at];
                uint hash = StringBankWriter.Hash(name);
                if (channelNames.TryGetValue(hash, out string? prior))
                { if (prior != name) channelNames[hash] = null; }
                else channelNames.Add(hash, name);
            }
        }
        var root = Root(0, 0);
        return new("Behavior", context.Name, [root], references, diagnostics)
        { Graph = new(graphNodes.Values.ToArray(), graphEdges) };
    }
}
