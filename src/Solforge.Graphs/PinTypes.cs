using System.Globalization;
using System.Numerics;

namespace Solforge.Graphs;

/**
 * What a pin carries. Exec pins order actions; the rest carry data. Numeric is the math wildcard (any number or vector;
 * the widest input wins). Any is the reroute wildcard.
 */
public enum PinType { Exec, Bool, Int, Float, Vector2, Vector3, Color, Texture, String, Entity, Stat, Perk, Field, Numeric, Any }

/** Pin colours, connection rules and the automatic conversions inserted between mismatched data pins. */
public static class PinTypes
{
    /** Blueprint-style colours (RGBA 0..1). */
    public static Vector4 ColorOf(PinType type) => type switch
    {
        PinType.Exec => new(1, 1, 1, 1),
        PinType.Bool => new(.62f, .08f, .08f, 1),
        PinType.Int => new(.12f, .88f, .66f, 1),
        PinType.Float => new(.62f, .95f, .30f, 1),
        PinType.Vector2 => new(.95f, .85f, .35f, 1),
        PinType.Vector3 => new(1f, .78f, .05f, 1),
        PinType.Color => new(1f, .45f, .20f, 1),
        PinType.Texture => new(.30f, .45f, 1f, 1),
        PinType.String => new(.98f, .36f, .85f, 1),
        PinType.Entity => new(.25f, .70f, 1f, 1),
        PinType.Stat => new(1f, .62f, .25f, 1),
        PinType.Perk => new(.75f, .45f, 1f, 1),
        PinType.Field => new(.70f, .70f, .74f, 1),
        PinType.Numeric => new(.55f, .85f, .55f, 1),
        _ => new(.82f, .82f, .82f, 1)
    };

    /** Pairs that convert implicitly, with the conversion node type the editor inserts. */
    private static readonly Dictionary<(PinType From, PinType To), string> conversions = new()
    {
        [(PinType.Int, PinType.Float)] = "conv.int_float",
        [(PinType.Float, PinType.Int)] = "conv.float_int",
        [(PinType.Bool, PinType.Float)] = "conv.bool_float",
        [(PinType.Bool, PinType.Int)] = "conv.bool_int",
        [(PinType.Float, PinType.Vector2)] = "conv.float_vector2",
        [(PinType.Float, PinType.Vector3)] = "conv.float_vector3",
        [(PinType.Float, PinType.Color)] = "conv.float_color",
        [(PinType.Vector2, PinType.Vector3)] = "conv.vector2_vector3",
        [(PinType.Vector3, PinType.Color)] = "conv.vector3_color",
        [(PinType.Color, PinType.Vector3)] = "conv.color_vector3",
        [(PinType.Int, PinType.String)] = "conv.int_string",
        [(PinType.Float, PinType.String)] = "conv.float_string",
        [(PinType.Bool, PinType.String)] = "conv.bool_string",
        [(PinType.Entity, PinType.Bool)] = "conv.entity_bool",
    };

    public static IEnumerable<(PinType From, PinType To, string NodeType)> Conversions =>
        conversions.Select(pair => (pair.Key.From, pair.Key.To, pair.Value));

    /** The conversion node type for from -> to, or null when none exists (or none is needed). */
    public static string? Conversion(PinType from, PinType to) => conversions.GetValueOrDefault((from, to));

    /** Whether an output of type from may feed an input of type to directly (no conversion node). */
    public static bool Compatible(PinType from, PinType to) =>
        from == to || from == PinType.Any || to == PinType.Any
        || to == PinType.Numeric && IsNumeric(from) || from == PinType.Numeric && IsNumeric(to);

    public static bool IsNumeric(PinType type) =>
        type is PinType.Bool or PinType.Int or PinType.Float or PinType.Vector2 or PinType.Vector3 or PinType.Color or PinType.Numeric;

    /** Whether a link is allowed at all: directly or through a conversion. */
    public static bool Connectable(PinType from, PinType to) => Compatible(from, to) || Conversion(from, to) is not null;

    public static string Label(PinType type) => type switch
    {
        PinType.Vector2 => "Vector 2", PinType.Vector3 => "Vector 3", _ => type.ToString()
    };
}

/** A data value as compilers and constant folding see it: up to four numbers, or text. */
public readonly record struct GraphValue(PinType Type, double X, double Y = 0, double Z = 0, double W = 0, string? Text = null)
{
    public static GraphValue Float(double value) => new(PinType.Float, value);
    public static GraphValue Int(long value) => new(PinType.Int, value);
    public static GraphValue Bool(bool value) => new(PinType.Bool, value ? 1 : 0);
    public static GraphValue Str(string text) => new(PinType.String, 0, Text: text);
    public static GraphValue Vec3(double x, double y, double z) => new(PinType.Vector3, x, y, z);
    public static GraphValue Rgba(double r, double g, double b, double a = 1) => new(PinType.Color, r, g, b, a);
    public static GraphValue Ref(PinType type, string text) => new(type, 0, Text: text);

    public double this[int index] => index switch { 0 => X, 1 => Y, 2 => Z, _ => W };
    public int Width => Type switch { PinType.Vector2 => 2, PinType.Vector3 => 3, PinType.Color => 4, _ => 1 };
    public bool IsNumeric => Type is PinType.Bool or PinType.Int or PinType.Float or PinType.Vector2 or PinType.Vector3 or PinType.Color;

    /** The value converted to another pin type, following the implicit conversions. */
    public GraphValue As(PinType target)
    {
        if (target == Type || target is PinType.Any or PinType.Numeric) return this;
        return target switch
        {
            PinType.Float => new(PinType.Float, X),
            PinType.Int => new(PinType.Int, Math.Truncate(X)),
            PinType.Bool => new(PinType.Bool, Type == PinType.Entity ? (string.IsNullOrEmpty(Text) ? 0 : 1) : X != 0 ? 1 : 0),
            PinType.Vector2 => Width == 1 ? new(PinType.Vector2, X, X) : new(PinType.Vector2, X, Y),
            PinType.Vector3 => Width == 1 ? new(PinType.Vector3, X, X, X) : new(PinType.Vector3, X, Y, Width >= 3 ? Z : 0),
            PinType.Color => Width == 1 ? new(PinType.Color, X, X, X, 1) : new(PinType.Color, X, Y, Width >= 3 ? Z : 0, Width >= 4 ? W : 1),
            PinType.String => Str(ToString()),
            _ => this with { Type = target }
        };
    }

    public override string ToString()
    {
        static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        return Type switch
        {
            PinType.Bool => X != 0 ? "true" : "false",
            PinType.Int => ((long)X).ToString(CultureInfo.InvariantCulture),
            PinType.Float => F(X),
            PinType.Vector2 => $"({F(X)}, {F(Y)})",
            PinType.Vector3 => $"({F(X)}, {F(Y)}, {F(Z)})",
            PinType.Color => $"({F(X)}, {F(Y)}, {F(Z)}, {F(W)})",
            _ => Text ?? ""
        };
    }

    /** Parses a property or default text for a pin type: numbers, "x, y, z", "#RRGGBB[AA]", true/false. */
    public static GraphValue Parse(PinType type, string? text)
    {
        text = (text ?? "").Trim();
        if (type is PinType.String or PinType.Texture or PinType.Entity or PinType.Stat or PinType.Perk or PinType.Field or PinType.Any)
            return Ref(type, text);
        if (type == PinType.Bool) return Bool(text is "1" or "true" or "True" or "yes");
        if (type == PinType.Numeric) type = PinType.Float;
        double[] parts = [0, 0, 0, type == PinType.Color ? 1 : 0];
        if (text.StartsWith('#') && text.Length is 7 or 9)
        {
            for (int i = 0; i < (text.Length - 1) / 2; i++)
                parts[i] = int.Parse(text.AsSpan(1 + 2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        }
        else
        {
            var tokens = text.Trim('(', ')').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(4, tokens.Length); i++)
                parts[i] = double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
            if (tokens.Length == 1 && type is PinType.Vector2 or PinType.Vector3 or PinType.Color)
            {
                parts[1] = parts[2] = parts[0];
                if (type == PinType.Color) parts[3] = 1;
            }
        }
        return new(type, parts[0], parts[1], parts[2], parts[3]);
    }
}
