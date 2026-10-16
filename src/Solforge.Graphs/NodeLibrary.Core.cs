namespace Solforge.Graphs;

/** Node types every graph kind shares: reroutes, functions, constants, math, logic, vectors and conversions. */
public static partial class NodeLibrary
{
    public const string Reroute = "core.reroute";
    public const string FunctionCall = "core.function_call";
    public const string FunctionInput = "core.function_input";
    public const string FunctionOutput = "core.function_output";

    /** Math nodes fold to constants: the result takes the widest input's type. */
    public static readonly string[] MathTypes =
        ["math.add", "math.subtract", "math.multiply", "math.divide", "math.min", "math.max", "math.power"];

    private static IEnumerable<NodeType> CoreTypes()
    {
        const GraphKind all = GraphKind.All;
        yield return Node(Reroute, "Reroute", "Utility", all, "Bends a wire; passes its input through unchanged.",
            [In("in", "", PinType.Any), Out("out", "", PinType.Any)], keywords: "knot wire");
        yield return Node(FunctionCall, "Call Function", "Functions", all, "Runs a collapsed subgraph (function).",
            [], [Prop("function", "Function", PropertyKind.Text)], hidden: true);
        yield return Node(FunctionInput, "Function Inputs", "Functions", all, "The values a function receives.", [], hidden: true);
        yield return Node(FunctionOutput, "Function Outputs", "Functions", all, "The values a function returns.", [], hidden: true);

        // Constants.
        yield return Node("const.float", "Float", "Constants", all, "A number.", [Out("value", "", PinType.Float)],
            [Prop("value", "Value", PropertyKind.Number, "0")], "scalar number constant");
        yield return Node("const.int", "Integer", "Constants", all, "A whole number.", [Out("value", "", PinType.Int)],
            [Prop("value", "Value", PropertyKind.Integer, "0")], "int number constant");
        yield return Node("const.bool", "Boolean", "Constants", all, "True or false.", [Out("value", "", PinType.Bool)],
            [Prop("value", "Value", PropertyKind.Bool, "false")], "bool flag constant");
        yield return Node("const.string", "Text", "Constants", all, "A string.", [Out("value", "", PinType.String)],
            [Prop("value", "Value", PropertyKind.Text)], "string constant");
        yield return Node("const.vector3", "Vector 3", "Constants", all, "Three numbers.", [Out("value", "", PinType.Vector3)],
            [Prop("value", "Value", PropertyKind.Text, "0, 0, 0")], "vec3 constant");
        yield return Node("const.color", "Color", "Constants", all, "An RGBA colour.", [Out("value", "", PinType.Color)],
            [Prop("value", "Value", PropertyKind.Color, "#FFFFFFFF")], "rgba colour constant vec4");

        // Math (component-wise on any number or vector).
        foreach (var (id, title, keywords) in new[]
        {
            ("math.add", "Add", "+ plus sum"), ("math.subtract", "Subtract", "- minus"), ("math.multiply", "Multiply", "* times scale"),
            ("math.divide", "Divide", "/"), ("math.min", "Min", "minimum smaller"), ("math.max", "Max", "maximum larger"),
            ("math.power", "Power", "pow exponent ^")
        })
            yield return Node(id, title, "Math", all, $"{title} A and B, per component.",
                [In("a", "A", PinType.Numeric, "0"), In("b", "B", PinType.Numeric, id is "math.multiply" or "math.divide" or "math.power" ? "1" : "0"),
                 Out("result", "", PinType.Numeric)], keywords: keywords);
        foreach (var (id, title, keywords) in new[]
        {
            ("math.one_minus", "One Minus", "1-x invert"), ("math.abs", "Abs", "absolute"), ("math.negate", "Negate", "-x"),
            ("math.saturate", "Saturate", "clamp 0 1"), ("math.floor", "Floor", "round down"), ("math.sqrt", "Square Root", "sqrt")
        })
            yield return Node(id, title, "Math", all, $"{title} of the input, per component.",
                [In("a", "", PinType.Numeric, "0"), Out("result", "", PinType.Numeric)], keywords: keywords);
        yield return Node("math.lerp", "Lerp", "Math", all, "Blends A to B by Alpha (0 gives A, 1 gives B).",
            [In("a", "A", PinType.Numeric, "0"), In("b", "B", PinType.Numeric, "1"), In("alpha", "Alpha", PinType.Float, "0.5"),
             Out("result", "", PinType.Numeric)], keywords: "linear interpolate mix blend");
        yield return Node("math.clamp", "Clamp", "Math", all, "Keeps the value between Min and Max.",
            [In("value", "Value", PinType.Numeric, "0"), In("min", "Min", PinType.Numeric, "0"), In("max", "Max", PinType.Numeric, "1"),
             Out("result", "", PinType.Numeric)], keywords: "limit range");

        // Logic.
        yield return Node("logic.compare", "Compare", "Logic", all, "Compares two numbers.",
            [In("a", "A", PinType.Float, "0"), In("b", "B", PinType.Float, "0"), Out("result", "", PinType.Bool)],
            [Prop("op", "Operator", PropertyKind.Choice, "<", "", "<", "<=", "==", "!=", ">=", ">")], "less greater equal");
        yield return Node("logic.and", "And", "Logic", all, "True when both are true.",
            [In("a", "A", PinType.Bool), In("b", "B", PinType.Bool), Out("result", "", PinType.Bool)], keywords: "&& both");
        yield return Node("logic.or", "Or", "Logic", all, "True when either is true.",
            [In("a", "A", PinType.Bool), In("b", "B", PinType.Bool), Out("result", "", PinType.Bool)], keywords: "|| either");
        yield return Node("logic.not", "Not", "Logic", all, "Inverts a boolean.",
            [In("a", "", PinType.Bool), Out("result", "", PinType.Bool)], keywords: "! invert");
        yield return Node("logic.select", "Select", "Logic", all, "Picks True or False by Condition.",
            [In("condition", "Condition", PinType.Bool), In("true", "True", PinType.Numeric, "1"), In("false", "False", PinType.Numeric, "0"),
             Out("result", "", PinType.Numeric)], keywords: "if switch ternary");

        // Vectors.
        yield return Node("vec.make3", "Make Vector 3", "Vector", all, "Builds a Vector 3 from X, Y and Z.",
            [In("x", "X", PinType.Float, "0"), In("y", "Y", PinType.Float, "0"), In("z", "Z", PinType.Float, "0"), Out("value", "", PinType.Vector3)],
            keywords: "append combine vec3");
        yield return Node("vec.make_color", "Make Color", "Vector", all, "Builds a colour from R, G, B and A.",
            [In("r", "R", PinType.Float, "1"), In("g", "G", PinType.Float, "1"), In("b", "B", PinType.Float, "1"), In("a", "A", PinType.Float, "1"),
             Out("value", "", PinType.Color)], keywords: "append combine rgba vec4");
        yield return Node("vec.break", "Break Vector", "Vector", all, "Splits a vector or colour into components.",
            [In("value", "", PinType.Numeric), Out("x", "X / R", PinType.Float), Out("y", "Y / G", PinType.Float),
             Out("z", "Z / B", PinType.Float), Out("w", "W / A", PinType.Float)], keywords: "split components mask swizzle");
        yield return Node("vec.dot", "Dot Product", "Vector", all, "Sum of the products of the components.",
            [In("a", "A", PinType.Numeric), In("b", "B", PinType.Numeric), Out("result", "", PinType.Float)], keywords: "dot");
        yield return Node("vec.normalize", "Normalize", "Vector", all, "Scales a vector to length 1.",
            [In("a", "", PinType.Numeric), Out("result", "", PinType.Numeric)], keywords: "unit length");

        // Implicit conversions; the editor inserts these when linking mismatched pins.
        foreach (var (from, to, id) in PinTypes.Conversions)
            yield return Node(id, $"To {PinTypes.Label(to)}", "Conversion", all, $"Converts {PinTypes.Label(from)} to {PinTypes.Label(to)}.",
                [In("in", "", from), Out("out", "", to)], keywords: $"convert cast {from} {to}".ToLowerInvariant());
    }
}
