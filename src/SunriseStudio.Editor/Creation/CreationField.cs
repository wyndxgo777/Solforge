using System.Globalization;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor.Creation;

/** How a creation form field is entered: a tag pick, a file, a number, text or a row list. */
public enum CreationFieldKind { Tag, File, Scalar, Text, Rows }

/** One selectable value of a creation field, with its label and detail text. */
public sealed record CreationChoice(uint Value, string Label, string Detail = "");

/**
 * A field part that can be made new in place of picking an existing asset.
 * @param NewBlocker Why New is not possible yet; non-empty keeps the field on Pick existing.
 * @param NewFields Drawn and checked in place of the field while New is chosen.
 */
public sealed record CreationPart(string NewLabel, string NewBlocker, IReadOnlyList<CreationField> NewFields,
    string PickLabel = "Pick existing");

/** One workflow input: what it asks for, where it writes and why that target is trusted. */
public sealed class CreationField(string label, CreationFieldKind kind, bool required, string target)
{
    public string Label { get; } = label;
    public CreationFieldKind Kind { get; } = kind;
    public bool Required { get; } = required;
    /** The entry, offset and encoding the field writes, or the importer it feeds; not shown in the UI. */
    public string Target { get; } = target;
    /** Why the target is trusted; not shown in the UI. */
    public string Evidence { get; init; } = "";
    /** Shown as the label tooltip. */
    public string Hint { get; init; } = "";
    /** Text fields with more than one line draw a multiline box. */
    public int Lines { get; init; } = 1;
    public bool Visible { get; set; } = true;

    public string Text { get; set; } = "";
    public uint? Tag { get; set; }
    public string TagLabel { get; set; } = "";

    /** Tag fields list candidates for a search text. */
    public Func<string, IEnumerable<CreationChoice>>? Search { get; init; }
    public string FileFilter { get; init; } = "All files|*.*";
    public ScalarEncoding Encoding { get; init; } = ScalarEncoding.None;
    public bool Hexadecimal { get; init; }
    /** Scalar fields with choices draw a combo instead of a text box. */
    public Func<IReadOnlyList<CreationChoice>>? Choices { get; init; }
    /** Rows fields are drawn by their workflow. */
    public Action? DrawRows { get; init; }
    /** Extra validation; returns a problem or null. */
    public Func<string?>? Check { get; init; }

    /** Tag fields with rows pick from this cached array; a call must not scan the catalog, and a new array refilters. */
    public Func<IReadOnlyList<BrowserAsset>>? Rows { get; init; }
    /** Keeps the rows that fit. Runs on a worker, so it must not read packages or mutable workflow state. */
    public Func<BrowserAsset, bool>? RowFilter { get; init; }

    public CreationPart? Part { get; init; }
    private bool makeNew;
    /** True while the part's New side is chosen; a part with a NewBlocker stays false. */
    public bool MakeNew
    {
        get => makeNew;
        set => makeNew = value && Part is { NewBlocker.Length: 0 };
    }

    public bool IsEmpty => !MakeNew && Kind switch
    {
        CreationFieldKind.Tag => Tag is null,
        CreationFieldKind.Rows => false,
        _ => string.IsNullOrWhiteSpace(Text)
    };

    /** Problems in this field alone; an empty optional field has none. While New is chosen, only the new fields count. */
    public IEnumerable<string> Problems()
    {
        if (!Visible) yield break;
        if (MakeNew)
        {
            foreach (var field in Part!.NewFields)
                foreach (string inner in field.Problems()) yield return inner;
            yield break;
        }
        if (IsEmpty)
        {
            if (Required) yield return $"{Label} is required.";
            yield break;
        }
        if (Kind == CreationFieldKind.Scalar && Encoding != ScalarEncoding.None && !TryParse(out _))
            yield return $"{Label} is not a valid {EncodingName}.";
        if (Kind == CreationFieldKind.File && !IconChecks.Exists(Text.Trim())) yield return $"{Label} does not exist.";
        if (Check?.Invoke() is { } problem) yield return problem;
    }

    /** Parses the scalar text within its encoding's range. */
    public bool TryParse(out ulong value)
    {
        value = 0;
        string text = Text.Trim();
        bool hex = Hexadecimal || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (Encoding is ScalarEncoding.Int32 or ScalarEncoding.Int16 or ScalarEncoding.Int64 && !hex)
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) return false;
            long low = Encoding switch { ScalarEncoding.Int16 => short.MinValue, ScalarEncoding.Int32 => int.MinValue, _ => long.MinValue };
            long high = Encoding switch { ScalarEncoding.Int16 => short.MaxValue, ScalarEncoding.Int32 => int.MaxValue, _ => long.MaxValue };
            value = unchecked((ulong)signed);
            return signed >= low && signed <= high;
        }
        if (!ulong.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return false;
        return Encoding switch
        {
            ScalarEncoding.UInt8 => value <= byte.MaxValue,
            ScalarEncoding.UInt16 or ScalarEncoding.Int16 => value <= ushort.MaxValue,
            ScalarEncoding.UInt32 or ScalarEncoding.Int32 or ScalarEncoding.Float32 => value <= uint.MaxValue,
            _ => true
        };
    }

    private string EncodingName => Encoding switch
    {
        ScalarEncoding.UInt8 => "byte",
        ScalarEncoding.UInt16 => Hexadecimal ? "4-digit hex value" : "16-bit value",
        ScalarEncoding.UInt32 => Hexadecimal ? "8-digit hex value" : "32-bit value",
        ScalarEncoding.Int32 => "signed 32-bit value",
        _ => "value"
    };
}
