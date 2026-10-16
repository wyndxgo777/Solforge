using System.Text;

namespace SunriseStudio.Packages;

/** Shared strings keep restored owner labels as compact as their original in-memory index. */
internal sealed class NameCacheWriter(Stream stream, CancellationToken token)
{
    internal readonly BinaryWriter Data = new(stream, new UTF8Encoding(false, true), true);
    private readonly Dictionary<string, int> strings = new(StringComparer.Ordinal);
    /** Writes a string once and later references to it by index. */
    internal void Text(string text)
    {
        if (strings.TryGetValue(text, out int index)) { Data.Write7BitEncodedInt(index * 2); return; }
        index = strings.Count; strings.Add(text, index);
        Data.Write7BitEncodedInt(index * 2 + 1); Data.Write(text);
    }
    /** Writes a flag and, when present, the string. */
    internal void Optional(string? text) { Data.Write(text is not null); if (text is not null) Text(text); }
    /** Writes a count, then each key with its value. */
    internal void Map<T>(IReadOnlyDictionary<uint, T> values, Action<T> write)
    {
        Data.Write(values.Count);
        int index = 0;
        foreach (var pair in values)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            Data.Write(pair.Key); write(pair.Value);
        }
    }
    /** Writes a count, then each element. */
    internal void Array<T>(IReadOnlyCollection<T> values, Action<T> write)
    {
        Data.Write(values.Count);
        int index = 0;
        foreach (var value in values)
        { if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested(); write(value); }
    }
}

/** Counts and strings are bounded before allocation; an incomplete snapshot is rejected. */
internal sealed class NameCacheReader(Stream stream, CancellationToken token)
{
    internal readonly BinaryReader Data = new(stream, new UTF8Encoding(false, true), true);
    private readonly List<string> strings = [];
    /** Reads a count; throws when it is negative or implausibly large. */
    internal int Count()
    {
        token.ThrowIfCancellationRequested();
        int count = Data.ReadInt32();
        return count is >= 0 and <= 2_000_000 ? count : throw new InvalidDataException("Cache count is invalid.");
    }
    /** Reads a new string or a back-reference to an earlier one; the low bit marks a new string. */
    internal string Text()
    {
        int code = Data.Read7BitEncodedInt(), index = code >> 1;
        if (code < 0 || index > strings.Count || strings.Count > 2_000_000)
            throw new InvalidDataException("Cache string index is invalid.");
        if ((code & 1) == 0)
            return index < strings.Count ? strings[index] : throw new InvalidDataException("Cache string is missing.");
        if (index != strings.Count) throw new InvalidDataException("Cache string index is repeated.");
        int length = Data.Read7BitEncodedInt();
        if (length is < 0 or > 1024 * 1024) throw new InvalidDataException("Cache string is too long.");
        byte[] bytes = Data.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        string text = new UTF8Encoding(false, true).GetString(bytes);
        strings.Add(text);
        return text;
    }
    /** Reads an optional string. */
    internal string? Optional() => Data.ReadBoolean() ? Text() : null;
    /** Reads a count of key and value pairs. */
    internal Dictionary<uint, T> Map<T>(Func<T> read)
    {
        int count = Count();
        var result = new Dictionary<uint, T>(count);
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            result.Add(Data.ReadUInt32(), read());
        }
        return result;
    }
    /** Reads a counted array. */
    internal T[] Array<T>(Func<T> read)
    {
        int count = Count();
        var result = new T[count];
        for (int i = 0; i < count; i++)
        { if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); result[i] = read(); }
        return result;
    }
    /** Reads counted pairs into an existing dictionary. */
    internal void Into<T>(Dictionary<uint, T> target, Func<T> read)
    {
        int count = Count(); target.EnsureCapacity(count);
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            target.Add(Data.ReadUInt32(), read());
        }
    }
}
