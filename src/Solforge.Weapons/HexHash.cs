using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solforge.Weapons;

/** A 32-bit hash written as "0x" and eight upper-case hex digits, the only spelling a recipe accepts.
 *  The text is kept as written so a recipe round-trips byte for byte and a bad hash is reported by
 *  validation with its field name instead of failing the whole parse. */
[JsonConverter(typeof(HexHashConverter))]
public readonly record struct HexHash(string Text)
{
    /** Formats a number as the canonical 0xXXXXXXXX string. */
    public static HexHash From(uint value) => new($"0x{value:X8}");
    /** Non-throwing parse; false when the text is not canonical. */
    public bool TryParse(out uint value) => TryParseCanonical(Text, out value);
    /** Parses the hash or throws naming the recipe field, mirroring Parhelion's parse_recipe_hash. */
    public uint Parse(string field) => TryParse(out uint value) ? value
        : throw new WeaponRecipeException($"{field} must be a canonical hash of the form 0xXXXXXXXX, not {Text ?? "null"}");
    public override string ToString() => Text ?? "";

    /** Accepts only 0x followed by exactly eight upper-case hex digits. */
    public static bool TryParseCanonical(string? text, out uint value)
    {
        value = 0;
        if (text is null || text.Length != 10 || !text.StartsWith("0x", StringComparison.Ordinal)) return false;
        foreach (char digit in text.AsSpan(2))
        {
            if (!(char.IsAsciiDigit(digit) || digit is >= 'A' and <= 'F')) return false;
        }
        return uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}

/** JSON converter that reads and writes a HexHash as a plain string, rejecting non-strings. */
public sealed class HexHashConverter : JsonConverter<HexHash>
{
    public override HexHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? new(reader.GetString()!)
            : throw new JsonException("A recipe hash must be a string of the form 0xXXXXXXXX.");
    public override void Write(Utf8JsonWriter writer, HexHash value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text ?? "");
}

/** A recipe that cannot be read or does not validate; the message names the field. */
public sealed class WeaponRecipeException(string message, Exception? inner = null) : Exception(message, inner);
