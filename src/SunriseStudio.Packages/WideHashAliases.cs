using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Packages;

/** Alias snapshots own sorted values and compare by contents rather than array identity. */
[JsonConverter(typeof(WideHashAliasesConverter))]
public sealed class WideHashAliases : IReadOnlyList<ulong>, IEquatable<WideHashAliases>
{
    // Catalog readers bound one native metadata array to this many rows.
    public const int MaximumCount = 1_000_000;
    public static WideHashAliases Empty { get; } = new(Array.Empty<ulong>());
    private readonly ulong[] values;
    public int Count => values.Length;
    public ulong this[int index] => values[index];

    public WideHashAliases(IEnumerable<ulong> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        values = source.Take(MaximumCount + 1).ToArray();
        if (values.Length > MaximumCount) throw new InvalidDataException("Too many wide-hash aliases.");
        Array.Sort(values);
        for (int i = 1; i < values.Length; i++)
            if (values[i] == values[i - 1])
                throw new InvalidDataException($"Wide hash {values[i]:X16} is duplicated.");
    }

    /** True when the alias is in the set. */
    public bool Contains(ulong value) => Array.BinarySearch(values, value) >= 0;
    /** Equal when both hold the same values. */
    public bool Equals(WideHashAliases? other) => other is not null && values.AsSpan().SequenceEqual(other.values);
    public override bool Equals(object? obj) => obj is WideHashAliases other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (ulong value in values) hash.Add(value);
        return hash.ToHashCode();
    }
    /** Enumerates the sorted values. */
    public IEnumerator<ulong> GetEnumerator() => ((IEnumerable<ulong>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/** Sixteen hex digits preserve every key bit in hand-edited and JSON-based workflows. */
public sealed class WideHashAliasesConverter : JsonConverter<WideHashAliases>
{
    /** Reads an array of 16-digit hex strings. */
    public override WideHashAliases Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Wide hashes need an array of 16-digit hex strings.");
        var values = new List<ulong>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { Length: 16 } text
                || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
                throw new JsonException("Each wide hash must contain exactly 16 hex digits.");
            values.Add(value);
            if (values.Count > WideHashAliases.MaximumCount) throw new JsonException("Too many wide hashes.");
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Wide-hash array is incomplete.");
        try { return new(values); }
        catch (InvalidDataException error) { throw new JsonException(error.Message, error); }
    }

    public override void Write(Utf8JsonWriter writer, WideHashAliases value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (ulong hash in value) writer.WriteStringValue(hash.ToString("X16", CultureInfo.InvariantCulture));
        writer.WriteEndArray();
    }
}
