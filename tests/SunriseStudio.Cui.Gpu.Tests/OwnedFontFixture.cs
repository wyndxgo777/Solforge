using System.Buffers.Binary;
using System.Text;

/** An original triangular A glyph exercises added fonts without copying a package font. */
internal static class OwnedFontFixture
{
    /** Builds a minimal TrueType font with one triangular glyph, optionally with a kerning pair. */
    public static byte[] Create(bool kerning = false)
    {
        Dictionary<string, byte[]> tables = [];
        var head = new byte[54]; U(head, 0, 0x10000); U(head, 4, 0x10000); U(head, 12, 0x5F0F3CF5);
        S(head, 16, 3); S(head, 18, 1000); S(head, 40, 600); S(head, 42, 700); S(head, 46, 8); S(head, 48, 2); tables.Add("head", head);
        var hhea = new byte[36]; U(hhea, 0, 0x10000); S(hhea, 4, 800); S(hhea, 6, -200); S(hhea, 10, 600);
        S(hhea, 16, 600); S(hhea, 18, 1); S(hhea, 34, 2); tables.Add("hhea", hhea);
        var maximum = new byte[32]; U(maximum, 0, 0x10000); S(maximum, 4, 2); S(maximum, 6, 3); S(maximum, 8, 1); S(maximum, 14, 2); tables.Add("maxp", maximum);
        var metrics = new byte[8]; S(metrics, 0, 600); S(metrics, 4, 600); tables.Add("hmtx", metrics);
        var loca = new byte[6]; S(loca, 4, 12); tables.Add("loca", loca);
        var glyph = new byte[24]; S(glyph, 0, 1); S(glyph, 6, 600); S(glyph, 8, 700); S(glyph, 10, 2);
        glyph[14] = 0x31; glyph[15] = 0x21; glyph[16] = 1; S(glyph, 17, 600); S(glyph, 19, -300); S(glyph, 21, 700); tables.Add("glyf", glyph);
        var cmap = new byte[44]; S(cmap, 2, 1); S(cmap, 4, 3); S(cmap, 6, 1); U(cmap, 8, 12);
        S(cmap, 12, 4); S(cmap, 14, 32); S(cmap, 18, 4); S(cmap, 20, 4); S(cmap, 22, 1);
        S(cmap, 26, 'A'); S(cmap, 28, 0xFFFF); S(cmap, 32, 'A'); S(cmap, 34, 0xFFFF); S(cmap, 36, 1 - 'A'); S(cmap, 38, 1); tables.Add("cmap", cmap);
        var os = new byte[78]; S(os, 2, 600); S(os, 4, 400); S(os, 6, 5); U(os, 42, 1);
        Encoding.ASCII.GetBytes("SUNR").CopyTo(os, 58); S(os, 62, 0x40); S(os, 64, 'A'); S(os, 66, 'A');
        S(os, 68, 800); S(os, 70, -200); S(os, 74, 800); S(os, 76, 200); tables.Add("OS/2", os);
        var post = new byte[32]; U(post, 0, 0x30000); S(post, 8, -100); S(post, 10, 50); tables.Add("post", post);
        var labels = new[] { (1, "Sunrise Fixture"), (2, "Regular"), (4, "Sunrise Fixture Regular"), (6, "SunriseFixture-Regular") };
        byte[][] strings = labels.Select(label => Encoding.BigEndianUnicode.GetBytes(label.Item2)).ToArray();
        int stringStart = 6 + labels.Length * 12;
        var name = new byte[stringStart + strings.Sum(bytes => bytes.Length)]; S(name, 2, labels.Length); S(name, 4, stringStart);
        int cursor = stringStart;
        for (int i = 0; i < labels.Length; i++)
        {
            int row = 6 + i * 12; S(name, row, 3); S(name, row + 2, 1); S(name, row + 4, 0x409);
            S(name, row + 6, labels[i].Item1); S(name, row + 8, strings[i].Length); S(name, row + 10, cursor - stringStart);
            strings[i].CopyTo(name, cursor); cursor += strings[i].Length;
        }
        tables.Add("name", name);
        if (kerning)
        {
            var kern = new byte[24]; S(kern, 2, 1); S(kern, 6, 20); S(kern, 8, 1);
            S(kern, 10, 1); S(kern, 12, 6); S(kern, 18, 1); S(kern, 20, 1); S(kern, 22, -100);
            tables.Add("kern", kern);
        }
        byte[] output = new byte[2048]; U(output, 0, 0x10000); S(output, 4, tables.Count);
        int power = 1, selector = 0; while (power * 2 <= tables.Count) { power *= 2; selector++; }
        S(output, 6, power * 16); S(output, 8, selector); S(output, 10, tables.Count * 16 - power * 16);
        int position = 12 + tables.Count * 16, directory = 12, headAt = 0;
        foreach (var (tag, data) in tables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            position = (position + 3) & ~3; Encoding.ASCII.GetBytes(tag).CopyTo(output, directory);
            U(output, directory + 4, Checksum(data)); U(output, directory + 8, (uint)position); U(output, directory + 12, (uint)data.Length);
            data.CopyTo(output, position); if (tag == "head") headAt = position;
            position += data.Length; directory += 16;
        }
        U(output, headAt + 8, unchecked(0xB1B0AFBA - Checksum(output)));
        return output;
    }

    /** Returns the offset of the named table in the font. */
    public static int Table(byte[] font, string tag)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int row = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, row, 4) == tag) return (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(row + 8));
        }
        throw new Exception("Fixture font table is missing.");
    }
    /** Recomputes table and head checksums after the font was edited. */
    public static void RepairChecksums(byte[] font)
    {
        int head = Table(font, "head"); U(font, head + 8, 0);
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int row = 12 + i * 16;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(row + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(row + 12));
            U(font, row + 4, Checksum(font.AsSpan(offset, length).ToArray()));
        }
        U(font, head + 8, unchecked(0xB1B0AFBA - Checksum(font)));
    }
    private static uint Checksum(byte[] bytes)
    {
        uint sum = 0;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            uint word = 0;
            for (int j = 0; j < 4; j++) word = (word << 8) | (i + j < bytes.Length ? bytes[i + j] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }
    private static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    private static void S(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), unchecked((ushort)value));
}
