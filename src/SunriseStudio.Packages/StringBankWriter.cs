using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Packages;

/** A string and the hash it is stored under. */
public sealed record StringBankText(uint Hash, string Text);

/** An installed bank whose layout passed every check the writer relies on. */
public sealed class StringBankTemplate
{
    // Container: size at 0, hash array at 8, 13 language data tags from 0x18.
    internal const int HashArray = 0x08, LanguageTags = 0x18, LanguageCount = 13, ContainerFixed = 0x4C;
    // Data: size at 0, parts at 8, undecoded array at 24, wide text at 40, UTF-8 text at 56, combinations at 72.
    internal const int PartArray = 8, UndecodedArray = 24, WideArray = 40, TextArray = 56, CombinationArray = 72, DataFixed = 0x58;
    internal const int PartStride = 0x20, CombinationStride = 16;
    // Part fields the reader decodes: text pointer, variable hash, byte length, character length, shift.
    internal const int PartText = 0x08, PartVariable = 0x10, PartBytes = 0x14, PartCharacters = 0x16, PartShift = 0x18;
    // Part bytes nobody decodes; they are copied from the template's plain-text parts.
    internal const int PartUnknownEnd = 0x08, PartTailStart = 0x1A;
    // The part decoder 0x7FF742F36BB0 reads UTF-8 when this bit is set, wide text from +40 when clear.
    internal const int PartFlags = 0x1B, Utf8Flag = 0x04;
    internal const uint HashRowClass = 0x80800070, PartRowClass = 0x80809A90, CombinationRowClass = 0x80809A8E;
    internal const uint ArrayMarker = 0x80809FBD, EmptyHash = 0x811C9DC5;

    /** How a string's length is counted: bytes, code points or UTF-16 units. */
    [Flags] internal enum LengthRule { None = 0, Bytes = 1, CodePoints = 2, Utf16 = 4 }

    public uint ContainerTag { get; }
    public uint DataTag { get; }
    public int StringCount { get; private init; }
    internal byte[] ContainerPrefix { get; private init; } = [];
    internal byte[] PartPrototype { get; private init; } = [];
    internal byte[] HashRowType { get; private init; } = [];
    internal byte[] PartRowType { get; private init; } = [];
    internal byte[] TextRowType { get; private init; } = [];
    internal byte[] CombinationRowType { get; private init; } = [];
    /** Row type of the template's one-character empty wide array; empty when the template has none. */
    internal byte[] WideRowType { get; private init; } = [];
    internal bool ContainerSized { get; private init; }
    internal bool DataSized { get; private init; }
    internal bool Sorted { get; private init; }
    internal bool Terminated { get; private init; }
    internal LengthRule Characters { get; private init; }

    private StringBankTemplate(uint containerTag, uint dataTag) => (ContainerTag, DataTag) = (containerTag, dataTag);

    /**
     * Checks one installed bank against every layout rule the writer depends on.
     * @param isLanguageData Tells whether a language slot names a string data tag; null skips that check.
     * @return The template; InvalidDataException names the first failed check.
     */
    public static StringBankTemplate Read(uint containerTag, byte[] container, uint dataTag, byte[] data,
        Func<uint, bool>? isLanguageData = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(data);
        if (container.Length < ContainerFixed + 4) throw Fail("the container is shorter than its language table");
        var hashes = StringBankBytes.Array(container, HashArray, 4);
        if (hashes.Count == 0 || StringBankBytes.U32(container, hashes.Header + 8) != HashRowClass)
            throw Fail("the container has no typed hash array");
        if (hashes.Header - 4 < ContainerFixed) throw Fail("the hash array overlaps the language table");
        int hashEnd = hashes.Data + hashes.Count * 4;
        if (container.Length - hashEnd >= 16 || container.AsSpan(hashEnd).ContainsAnyExcept((byte)0))
            throw Fail("the container holds bytes after its hash array");
        if (StringBankBytes.U32(container, LanguageTags) != dataTag) throw Fail("the English slot names a different data tag");
        for (int slot = 1; slot < LanguageCount; slot++)
        {
            uint tag = StringBankBytes.U32(container, LanguageTags + slot * 4);
            if (tag is not (0 or uint.MaxValue) && isLanguageData?.Invoke(tag) == false)
                throw Fail($"language slot {slot} does not name a string data tag");
        }
        if (data.Length < DataFixed) throw Fail("the data tag is shorter than its array table");
        var parts = StringBankBytes.Array(data, PartArray, PartStride);
        if (parts.Count == 0 || StringBankBytes.U32(data, parts.Header + 8) != PartRowClass) throw Fail("the data has no typed part array");
        if (StringBankBytes.I64(data, UndecodedArray) != 0) throw Fail($"the data uses the undecoded array at +{UndecodedArray}");
        // Plain installed banks hold one zero wide character that no UTF-8 part points at.
        var wide = StringBankBytes.Array(data, WideArray, 2);
        if (wide.Count > 1 || wide.Count == 1 && StringBankBytes.U16(data, wide.Data) != 0)
            throw Fail("the data holds wide text");
        var text = StringBankBytes.Array(data, TextArray, 1);
        if (text.Count == 0) throw Fail("the data has no text array");
        var combinations = StringBankBytes.Array(data, CombinationArray, CombinationStride);
        if (combinations.Count != hashes.Count || StringBankBytes.U32(data, combinations.Header + 8) != CombinationRowClass)
            throw Fail("the combination array does not match the hash array");
        int arrays = Math.Min(Math.Min(parts.Header, wide.Count == 0 ? int.MaxValue : wide.Header),
            Math.Min(text.Header, combinations.Header)) - 4;
        if (arrays < DataFixed || data.AsSpan(DataFixed, arrays - DataFixed).ContainsAnyExcept((byte)0))
            throw Fail("the data holds undecoded bytes before its arrays");
        int partsEnd = parts.Data + parts.Count * PartStride, textEnd = text.Data + text.Count;
        for (int i = 0; i < combinations.Count; i++)
        {
            int row = combinations.Data + i * CombinationStride;
            long first = row + StringBankBytes.I64(data, row), count = StringBankBytes.I64(data, row + 8);
            if (count < 0 || first < parts.Data || (first - parts.Data) % PartStride != 0 || first + count * PartStride > partsEnd)
                throw Fail($"combination {i} points outside the part array");
        }
        byte[]? prototype = null;
        var rule = LengthRule.Bytes | LengthRule.CodePoints | LengthRule.Utf16;
        bool terminated = true;
        for (int i = 0; i < parts.Count; i++)
        {
            int part = parts.Data + i * PartStride;
            if (StringBankBytes.U32(data, part + PartVariable) != EmptyHash) continue;
            long start = part + PartText + StringBankBytes.I64(data, part + PartText);
            int bytes = StringBankBytes.U16(data, part + PartBytes), characters = StringBankBytes.U16(data, part + PartCharacters);
            if (start < text.Data || start + bytes > textEnd) throw Fail($"part {i} text lies outside the text array");
            var span = data.AsSpan(part, PartStride);
            if (prototype is null) prototype = span.ToArray();
            else if (!span[..PartUnknownEnd].SequenceEqual(prototype.AsSpan(0, PartUnknownEnd))
                || !span[PartTailStart..].SequenceEqual(prototype.AsSpan(PartTailStart)))
                throw Fail("plain parts disagree in their undecoded bytes");
            var (points, units) = StringBankBytes.Count(data.AsSpan((int)start, bytes), StringBankBytes.U16(data, part + PartShift));
            if (characters != bytes) rule &= ~LengthRule.Bytes;
            if (characters != points) rule &= ~LengthRule.CodePoints;
            if (characters != units) rule &= ~LengthRule.Utf16;
            terminated &= start + bytes < textEnd && data[(int)(start + bytes)] == 0;
        }
        if (prototype is null) throw Fail("the data has no plain text part");
        // Written parts hold UTF-8 and point at nothing else, so the copied bytes must say so.
        if ((prototype[PartFlags] & Utf8Flag) == 0) throw Fail("its plain parts are not UTF-8");
        if (prototype.AsSpan(0, PartUnknownEnd).ContainsAnyExcept((byte)0)) throw Fail("its plain parts use the head field");
        if (rule == LengthRule.None) throw Fail("the character length field matches no counting rule");
        bool sorted = true;
        for (int i = 1; i < hashes.Count && sorted; i++)
            sorted = StringBankBytes.U32(container, hashes.Data + (i - 1) * 4) <= StringBankBytes.U32(container, hashes.Data + i * 4);
        return new(containerTag, dataTag)
        {
            StringCount = hashes.Count,
            ContainerPrefix = container.AsSpan(0, hashes.Header - 4).ToArray(),
            PartPrototype = prototype,
            HashRowType = container.AsSpan(hashes.Header + 8, 8).ToArray(),
            PartRowType = data.AsSpan(parts.Header + 8, 8).ToArray(),
            TextRowType = data.AsSpan(text.Header + 8, 8).ToArray(),
            CombinationRowType = data.AsSpan(combinations.Header + 8, 8).ToArray(),
            WideRowType = wide.Count == 0 ? [] : data.AsSpan(wide.Header + 8, 8).ToArray(),
            ContainerSized = StringBankBytes.U64(container, 0) == (ulong)container.Length,
            DataSized = StringBankBytes.U64(data, 0) == (ulong)data.Length,
            Sorted = sorted, Terminated = terminated, Characters = rule
        };
    }

    /** The character length for text under this template's rule; null when the template cannot tell. */
    internal int? CharacterLength(string text, int bytes)
    {
        int points = 0;
        foreach (var _ in text.EnumerateRunes()) points++;
        var values = new HashSet<int>();
        if (Characters.HasFlag(LengthRule.Bytes)) values.Add(bytes);
        if (Characters.HasFlag(LengthRule.CodePoints)) values.Add(points);
        if (Characters.HasFlag(LengthRule.Utf16)) values.Add(text.Length);
        return values.Count == 1 ? values.First() : null;
    }

    private static InvalidDataException Fail(string reason) => new($"String bank template refused: {reason}.");
}

/** Writes class-0x80809A88 string banks; bytes the reader does not decode come from an installed bank. */
public static class StringBankWriter
{
    public const uint ContainerClass = 0x80809A88, DataClass = 0x80809A8A;
    /** The part byte length is a u16. */
    public const int MaximumTextBytes = ushort.MaxValue;
    /** Bounds a bank to what the editor can rewrite in one step. */
    public const int MaximumStrings = 65536;

    /** FNV-1 over UTF-8; an empty text gives the empty marker 0x811C9DC5. */
    public static uint Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    /** FNV-1 over the bytes as stored; Hash(text) is this over the text's UTF-8. */
    public static uint Hash(ReadOnlySpan<byte> bytes)
    {
        uint hash = StringBankTemplate.EmptyHash;
        foreach (byte value in bytes) hash = unchecked(hash * 0x01000193) ^ value;
        return hash;
    }

    /**
     * Builds a complete bank from a template and the full string list.
     * @param dataTag Tag the new English data entry gets; every language slot names it.
     * @param strings Every string of the bank, including ones it already held.
     * @return Container and data payloads; unknown bytes are the template's.
     */
    public static (byte[] Container, byte[] Data) Write(StringBankTemplate template, uint dataTag,
        IReadOnlyList<StringBankText> strings)
    {
        ArgumentNullException.ThrowIfNull(template);
        var rows = Prepare(template, strings);
        int count = rows.Count;
        int containerHeader = Align(template.ContainerPrefix.Length + 4);
        byte[] container = new byte[checked(containerHeader + 16 + count * 4)];
        template.ContainerPrefix.CopyTo(container, 0);
        Q(container, StringBankTemplate.HashArray, count);
        Q(container, StringBankTemplate.HashArray + 8, containerHeader - StringBankTemplate.HashArray - 8);
        for (int slot = 0; slot < StringBankTemplate.LanguageCount; slot++)
            U(container, StringBankTemplate.LanguageTags + slot * 4, dataTag);
        Header(container, containerHeader, count, template.HashRowType);
        for (int i = 0; i < count; i++) U(container, containerHeader + 16 + i * 4, rows[i].Hash);
        if (template.ContainerSized) Q(container, 0, container.Length);

        int terminator = template.Terminated ? 1 : 0;
        int textBytes = rows.Sum(row => row.Bytes.Length + terminator);
        int partHeader = Align(StringBankTemplate.DataFixed + 4), partStart = partHeader + 16;
        int partEnd = partStart + count * StringBankTemplate.PartStride;
        bool wide = template.WideRowType.Length != 0;
        int wideHeader = Align(partEnd + 4), textHeader = Align((wide ? wideHeader + 16 + 2 : partEnd) + 4), textStart = textHeader + 16;
        int combinationHeader = Align(textStart + textBytes + 4), combinationStart = combinationHeader + 16;
        byte[] data = new byte[checked(combinationStart + count * StringBankTemplate.CombinationStride)];
        Descriptor(data, StringBankTemplate.PartArray, count, partHeader);
        if (wide)
        {
            // One zero wide character, as the template holds; the new array is zeroed already.
            Descriptor(data, StringBankTemplate.WideArray, 1, wideHeader);
            Header(data, wideHeader, 1, template.WideRowType);
        }
        Descriptor(data, StringBankTemplate.TextArray, textBytes, textHeader);
        Descriptor(data, StringBankTemplate.CombinationArray, count, combinationHeader);
        Header(data, partHeader, count, template.PartRowType);
        Header(data, textHeader, textBytes, template.TextRowType);
        Header(data, combinationHeader, count, template.CombinationRowType);
        int cursor = textStart;
        for (int i = 0; i < count; i++)
        {
            int part = partStart + i * StringBankTemplate.PartStride;
            template.PartPrototype.CopyTo(data, part);
            Q(data, part + StringBankTemplate.PartText, cursor - part - StringBankTemplate.PartText);
            U(data, part + StringBankTemplate.PartVariable, StringBankTemplate.EmptyHash);
            S(data, part + StringBankTemplate.PartBytes, (ushort)rows[i].Bytes.Length);
            S(data, part + StringBankTemplate.PartCharacters, (ushort)rows[i].Characters);
            S(data, part + StringBankTemplate.PartShift, 0);
            rows[i].Bytes.CopyTo(data, cursor);
            cursor += rows[i].Bytes.Length + terminator;
            int combination = combinationStart + i * StringBankTemplate.CombinationStride;
            Q(data, combination, part - combination);
            Q(data, combination + 8, 1);
        }
        if (template.DataSized) Q(data, 0, data.Length);
        return (container, data);
    }

    /** Checks that the template can encode these strings, without writing anything. */
    public static void Validate(StringBankTemplate template, IReadOnlyList<StringBankText> strings) =>
        Prepare(template, strings);

    /**
     * Reads back a bank of plain text, such as one this writer produced.
     * @return Strings in bank order; NotSupportedException when a part names a runtime variable.
     */
    public static IReadOnlyList<StringBankText> Read(byte[] container, byte[] data)
    {
        var hashes = StringBankBytes.Array(container, StringBankTemplate.HashArray, 4);
        var combinations = StringBankBytes.Array(data, StringBankTemplate.CombinationArray, StringBankTemplate.CombinationStride);
        if (combinations.Count != hashes.Count) throw new InvalidDataException("String combination count differs from the hash count.");
        var result = new List<StringBankText>(hashes.Count);
        var text = new StringBuilder();
        for (int i = 0; i < hashes.Count; i++)
        {
            int row = combinations.Data + i * StringBankTemplate.CombinationStride;
            long first = row + StringBankBytes.I64(data, row), parts = StringBankBytes.I64(data, row + 8);
            if (parts < 0 || first < 0 || first + parts * StringBankTemplate.PartStride > data.Length)
                throw new InvalidDataException("String combination points outside its data tag.");
            text.Clear();
            for (long p = 0; p < parts; p++)
            {
                int part = checked((int)(first + p * StringBankTemplate.PartStride));
                if (StringBankBytes.U32(data, part + StringBankTemplate.PartVariable) != StringBankTemplate.EmptyHash)
                    throw new NotSupportedException("The bank holds a runtime variable; Studio cannot rewrite it.");
                long start = part + StringBankTemplate.PartText + StringBankBytes.I64(data, part + StringBankTemplate.PartText);
                int bytes = StringBankBytes.U16(data, part + StringBankTemplate.PartBytes);
                if (start < 0 || start + bytes > data.Length) throw new InvalidDataException("String part points outside its data tag.");
                text.Append(StringBankBytes.Decode(data.AsSpan((int)start, bytes), StringBankBytes.U16(data, part + StringBankTemplate.PartShift)));
            }
            result.Add(new(StringBankBytes.U32(container, hashes.Data + i * 4), text.ToString()));
        }
        return result;
    }

    /**
     * Returns the first installed bank that passes the template checks for these strings.
     * @param containerTags Candidate container tags, most preferred first.
     * @param limit Candidates to try before giving up.
     */
    public static StringBankTemplate FindTemplate(PackageCatalog catalog, IEnumerable<uint> containerTags,
        IReadOnlyList<StringBankText> strings, int limit = 256)
    {
        var failures = new List<string>();
        int tried = 0;
        foreach (uint tag in containerTags.Distinct())
        {
            if (tried >= limit) break;
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ContainerClass
                || !catalog.ContainsInstalledPackage(entry.PackageId)) continue;
            tried++;
            try
            {
                byte[] container = catalog.ReadOriginal(tag);
                if (container.Length < StringBankTemplate.LanguageTags + 4) continue;
                uint dataTag = StringBankBytes.U32(container, StringBankTemplate.LanguageTags);
                if (!catalog.TryGetEntry(dataTag, out var dataEntry) || dataEntry.Reference != DataClass
                    || !catalog.ContainsInstalledPackage(dataEntry.PackageId)) continue;
                var template = StringBankTemplate.Read(tag, container, dataTag, catalog.ReadOriginal(dataTag), IsData);
                Validate(template, strings);
                return template;
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException
                                          or ArgumentException or OverflowException or IndexOutOfRangeException)
            {
                if (failures.Count < 3) failures.Add($"0x{tag:X8}: {error.Message}");
            }
        }
        throw new InvalidDataException("No installed string bank passed the writer's layout checks. "
            + string.Join(" ", failures));

        bool IsData(uint value) => catalog.TryGetEntry(value, out var data) && data.Reference == DataClass;
    }

    private sealed record Row(uint Hash, byte[] Bytes, int Characters);

    /** Rejects what the layout cannot hold and orders rows the way the template orders them. */
    private static List<Row> Prepare(StringBankTemplate template, IReadOnlyList<StringBankText> strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (strings.Count is 0 or > MaximumStrings) throw new InvalidDataException($"A bank holds 1 to {MaximumStrings} strings.");
        var rows = new List<Row>(strings.Count);
        var seen = new HashSet<uint>();
        foreach (var value in strings)
        {
            if (value is null || string.IsNullOrEmpty(value.Text)) throw new InvalidDataException("A bank string cannot be empty.");
            if (value.Hash == StringBankTemplate.EmptyHash) throw new InvalidDataException("0x811C9DC5 marks an empty string and cannot name text.");
            if (!seen.Add(value.Hash)) throw new InvalidDataException($"String hash 0x{value.Hash:X8} appears twice.");
            byte[] bytes = Encoding.UTF8.GetBytes(value.Text);
            if (bytes.Length > MaximumTextBytes) throw new InvalidDataException("A bank string exceeds 65535 UTF-8 bytes.");
            int characters = template.CharacterLength(value.Text, bytes.Length)
                ?? throw new InvalidDataException("The template bank does not show whether its length field counts bytes or characters. Use ASCII text.");
            if (characters > ushort.MaxValue) throw new InvalidDataException("A bank string is too long.");
            rows.Add(new(value.Hash, bytes, characters));
        }
        if (template.Sorted) rows.Sort((left, right) => left.Hash.CompareTo(right.Hash));
        return rows;
    }

    private static void Descriptor(byte[] bytes, int field, int count, int header)
    {
        Q(bytes, field, count);
        Q(bytes, field + 8, header - field - 8);
    }

    private static void Header(byte[] bytes, int header, int count, byte[] rowType)
    {
        U(bytes, header - 4, StringBankTemplate.ArrayMarker);
        Q(bytes, header, count);
        rowType.CopyTo(bytes, header + 8);
    }

    /** Array headers start on a 16-byte boundary with room for the marker before them. */
    private static int Align(int value) => checked((value + 15) & ~15);
    private static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
}

/** Shared bank reads; arrays follow the reader's typed-array rules. */
internal static class StringBankBytes
{
    /** Invalid sequences decode to U+FFFD, as the bank reader decodes them. */
    private const int ReplacementCharacter = 0xFFFD;

    /** Count, header offset and data offset of a typed array. */
    internal readonly record struct TypedArray(int Count, int Header, int Data);

    /** Reads a typed array header, validating bounds. */
    internal static TypedArray Array(byte[] bytes, int field, int stride)
    {
        long count = I64(bytes, field), relative = I64(bytes, field + 8);
        if (count == 0) return new(0, 0, 0);
        long header = field + 8 + relative;
        if (count < 0 || count > 1_000_000 || header < 4 || header + 16 > bytes.Length
            || U32(bytes, (int)header - 4) != StringBankTemplate.ArrayMarker || I64(bytes, (int)header) != count
            || header + 16 + count * stride > bytes.Length)
            throw new InvalidDataException($"Array 0x{field:X} is not a valid typed array.");
        return new((int)count, (int)header, (int)header + 16);
    }

    /** The shift is added to each decoded code point, as the bank reader does. */
    internal static string Decode(ReadOnlySpan<byte> bytes, int shift)
    {
        var text = new StringBuilder();
        Walk(bytes, shift, point => text.Append(char.ConvertFromUtf32(point is >= 0 and <= 0x10FFFF and (< 0xD800 or >= 0xE000)
            ? point : ReplacementCharacter)));
        return text.ToString();
    }

    /** Code points and UTF-16 units of one part, decoded as the reader decodes it. */
    internal static (int Points, int Units) Count(ReadOnlySpan<byte> bytes, int shift)
    {
        int points = 0, units = 0;
        Walk(bytes, shift, point => { points++; units += point > 0xFFFF && point <= 0x10FFFF ? 2 : 1; });
        return (points, units);
    }

    /** Decodes UTF-8 code points and adds the shift; an invalid sequence visits -1. */
    private static void Walk(ReadOnlySpan<byte> bytes, int shift, Action<int> visit)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            int lead = bytes[offset];
            int size = lead < 0x80 ? 1 : lead is >= 0xC0 and < 0xE0 ? 2 : lead is >= 0xE0 and < 0xF0 ? 3
                : lead is >= 0xF0 and < 0xF8 ? 4 : 0;
            bool valid = size != 0 && offset + size <= bytes.Length;
            int point = size == 1 ? lead : lead & (0x7F >> size);
            for (int k = 1; valid && k < size; k++)
            {
                valid = (bytes[offset + k] & 0xC0) == 0x80;
                point = point << 6 | bytes[offset + k] & 0x3F;
            }
            visit(valid ? point + shift : -1);
            offset += Math.Max(size, 1);
        }
    }

    /** Reads a little-endian uint. */
    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    /** Reads a little-endian ushort. */
    internal static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    /** Reads a little-endian long. */
    internal static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    /** Reads a little-endian ulong. */
    internal static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
}
