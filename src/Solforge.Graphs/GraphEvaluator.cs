namespace Solforge.Graphs;

/**
 * Constant folding over a flattened graph: what a data pin's value is. Material and weapon graphs compile through
 * it; nodes without a constant meaning (entities, events) give an empty value of their type.
 */
public sealed class GraphEvaluator(FlatGraph graph)
{
    private readonly Dictionary<(string, string), GraphValue> memo = new();
    private readonly HashSet<(string, string)> busy = new();
    public FlatGraph Graph => graph;

    /** The value reaching an input: its link's source, else the node's typed value, else the pin default. */
    public GraphValue Input(GraphNode node, string pin)
    {
        var definition = NodeLibrary.Find(node.Type)?.Pin(pin, false);
        var type = definition?.Type ?? PinType.Any;
        foreach (var (source, sourcePin) in graph.Sources(node.Id, pin))
        {
            var value = Output(source, sourcePin);
            return type is PinType.Numeric or PinType.Any ? value : value.As(type);
        }
        string? text = node.Properties.TryGetValue(pin, out var typed) ? typed : definition?.Default;
        return GraphValue.Parse(type == PinType.Any ? PinType.Float : type, text);
    }

    public bool Linked(GraphNode node, string pin) => graph.Sources(node.Id, pin).Any();

    public GraphValue Output(GraphNode node, string pin)
    {
        var key = (node.Id, pin);
        if (memo.TryGetValue(key, out var cached)) return cached;
        if (!busy.Add(key)) return GraphValue.Float(0);
        var value = Compute(node, pin);
        busy.Remove(key);
        memo[key] = value;
        return value;
    }

    private GraphValue Compute(GraphNode node, string pin)
    {
        GraphValue In(string id) => Input(node, id);
        string Prop(string id) => node.Get(id, NodeLibrary.Find(node.Type)?.Property(id)?.Default ?? "");
        switch (node.Type)
        {
            case NodeLibrary.Reroute: return In("in");
            case "const.float": case "mat.scalar_param": return GraphValue.Parse(PinType.Float, Prop("value"));
            case "const.int": return GraphValue.Parse(PinType.Int, Prop("value"));
            case "const.bool": return GraphValue.Parse(PinType.Bool, Prop("value"));
            case "const.string": return GraphValue.Str(Prop("value"));
            case "const.vector3": return GraphValue.Parse(PinType.Vector3, Prop("value"));
            case "const.color": case "mat.vector_param": return GraphValue.Parse(PinType.Color, Prop("value"));
            case "mat.texture": return GraphValue.Ref(PinType.Texture, Prop("tag").Trim());
            case "math.add": return Binary(In("a"), In("b"), (a, b) => a + b);
            case "math.subtract": return Binary(In("a"), In("b"), (a, b) => a - b);
            case "math.multiply": return Binary(In("a"), In("b"), (a, b) => a * b);
            case "math.divide": return Binary(In("a"), In("b"), (a, b) => b == 0 ? 0 : a / b);
            case "math.min": return Binary(In("a"), In("b"), Math.Min);
            case "math.max": return Binary(In("a"), In("b"), Math.Max);
            case "math.power": return Binary(In("a"), In("b"), Math.Pow);
            case "math.one_minus": return Unary(In("a"), a => 1 - a);
            case "math.abs": return Unary(In("a"), Math.Abs);
            case "math.negate": return Unary(In("a"), a => -a);
            case "math.saturate": return Unary(In("a"), a => Math.Clamp(a, 0, 1));
            case "math.floor": return Unary(In("a"), Math.Floor);
            case "math.sqrt": return Unary(In("a"), a => a <= 0 ? 0 : Math.Sqrt(a));
            case "math.lerp":
            {
                double alpha = In("alpha").X;
                return Binary(In("a"), In("b"), (a, b) => a + (b - a) * alpha);
            }
            case "math.clamp":
            {
                var low = In("min"); var high = In("max");
                var clamped = Binary(In("value"), low, Math.Max);
                return Binary(clamped, high, Math.Min);
            }
            case "logic.compare":
            {
                double a = In("a").X, b = In("b").X;
                return GraphValue.Bool(Prop("op") switch
                {
                    "<=" => a <= b, "==" => Math.Abs(a - b) < 1e-9, "!=" => Math.Abs(a - b) >= 1e-9, ">=" => a >= b, ">" => a > b, _ => a < b
                });
            }
            case "logic.and": return GraphValue.Bool(In("a").X != 0 && In("b").X != 0);
            case "logic.or": return GraphValue.Bool(In("a").X != 0 || In("b").X != 0);
            case "logic.not": return GraphValue.Bool(In("a").X == 0);
            case "logic.select": return In("condition").X != 0 ? In("true") : In("false");
            case "vec.make3": return GraphValue.Vec3(In("x").X, In("y").X, In("z").X);
            case "vec.make_color": return GraphValue.Rgba(In("r").X, In("g").X, In("b").X, In("a").X);
            case "vec.break":
            {
                var value = In("value");
                return GraphValue.Float(pin switch { "x" => value.X, "y" => value.Y, "z" => value.Z, _ => value.W });
            }
            case "vec.dot":
            {
                var a = In("a"); var b = In("b");
                int width = Math.Max(a.Width, b.Width);
                double sum = 0;
                for (int i = 0; i < width; i++) sum += (a.Width == 1 ? a.X : a[i]) * (b.Width == 1 ? b.X : b[i]);
                return GraphValue.Float(sum);
            }
            case "vec.normalize":
            {
                var a = In("a");
                double length = Math.Sqrt(Enumerable.Range(0, a.Width).Sum(i => a[i] * a[i]));
                return length == 0 ? a : Unary(a, x => x / length);
            }
            case "mat.srgb_to_linear":
            {
                var color = In("color").As(PinType.Color);
                static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                return GraphValue.Rgba(Linear(color.X), Linear(color.Y), Linear(color.Z), color.W);
            }
            case "mat.hsv":
            {
                double h = In("h").X % 1, s = In("s").X, v = In("v").X;
                if (h < 0) h += 1;
                double f = h * 6, c = v * s, x = c * (1 - Math.Abs(f % 2 - 1)), m = v - c;
                var (r, g, b) = (int)f switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
                return GraphValue.Rgba(r + m, g + m, b + m);
            }
        }
        if (node.Type.StartsWith("conv.", StringComparison.Ordinal))
        {
            var output = NodeLibrary.Find(node.Type)?.Pin("out", true);
            return In("in").As(output?.Type ?? PinType.Any);
        }
        var type = NodeLibrary.Find(node.Type)?.Pin(pin, true)?.Type ?? PinType.Any;
        return GraphValue.Ref(type, "");
    }

    private static PinType Widest(GraphValue a, GraphValue b)
    {
        if (a.Width == 1 && b.Width == 1) return a.Type == PinType.Int && b.Type == PinType.Int ? PinType.Int : PinType.Float;
        return a.Width >= b.Width ? a.Type : b.Type;
    }

    public static GraphValue Binary(GraphValue a, GraphValue b, Func<double, double, double> op)
    {
        var type = Widest(a, b);
        int width = Math.Max(a.Width, b.Width);
        double Pick(GraphValue value, int i) => value.Width == 1 ? value.X : i < value.Width ? value[i] : 0;
        double[] parts = new double[4];
        for (int i = 0; i < width; i++) parts[i] = op(Pick(a, i), Pick(b, i));
        if (type == PinType.Int) parts[0] = Math.Truncate(parts[0]);
        return new(type, parts[0], parts[1], parts[2], parts[3]);
    }

    public static GraphValue Unary(GraphValue a, Func<double, double> op)
    {
        var type = a.Width == 1 ? (a.Type == PinType.Int ? PinType.Int : PinType.Float) : a.Type;
        double[] parts = new double[4];
        for (int i = 0; i < a.Width; i++) parts[i] = op(a[i]);
        return new(type, parts[0], parts[1], parts[2], parts[3]);
    }
}
