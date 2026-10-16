using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Creation.Tests;

/** Byte helpers and a bank shaped like the installed ones, with every undecoded byte set to a marker. */
internal static class Fixture
{
    public const uint ArrayMarker = 0x80809FBD, EmptyHash = 0x811C9DC5;
    public const uint ContainerMarker = 0xA5A5A5A5;
    // Installed plain parts have a zero head; tail byte 0x1B carries the UTF-8 flag 0x04.
    public const byte PartHead = 0x00, PartTail = 0x26;

    /**
     * Builds a bank; the defaults give the installed plain shape.
     * @param wide Wide characters at +40; installed plain banks hold one zero.
     * @param head Part head byte; installed plain parts hold zero.
     */
    public static (byte[] Container, byte[] Data) Bank(uint dataTag, IReadOnlyList<(uint Hash, string Text)> strings,
        bool lengthIsBytes = false, bool terminate = true, long undecoded = 0, bool splitPrototype = false,
        ushort[]? wide = null, byte head = PartHead, byte tail = PartTail)
    {
        wide ??= [0];
        int count = strings.Count;
        const int hashHeader = 0x60;
        byte[] container = new byte[hashHeader + 16 + 4 * count];
        Q(container, 0, container.Length);
        Array(container, 8, hashHeader, count, 0x80800070);
        for (int slot = 0; slot < 13; slot++) U(container, 0x18 + slot * 4, dataTag);
        U(container, 0x4C, ContainerMarker);
        for (int i = 0; i < count; i++) U(container, hashHeader + 16 + i * 4, strings[i].Hash);

        byte[][] texts = strings.Select(row => Encoding.UTF8.GetBytes(row.Text)).ToArray();
        int terminator = terminate ? 1 : 0, textBytes = texts.Sum(text => text.Length + terminator);
        const int partHeader = 0x60;
        int partStart = partHeader + 16;
        int partEnd = partStart + 32 * count, wideHeader = Align(partEnd + 4);
        int textHeader = Align((wide.Length == 0 ? partEnd : wideHeader + 16 + 2 * wide.Length) + 4), textStart = textHeader + 16;
        int combinationHeader = Align(textStart + textBytes + 4), combinationStart = combinationHeader + 16;
        byte[] data = new byte[combinationStart + 16 * count];
        Q(data, 0, data.Length);
        Array(data, 8, partHeader, count, 0x80809A90);
        if (undecoded != 0) Q(data, 24, undecoded);
        if (wide.Length != 0)
        {
            Array(data, 40, wideHeader, wide.Length, 0x80800006);
            for (int i = 0; i < wide.Length; i++) S(data, wideHeader + 16 + 2 * i, wide[i]);
        }
        Array(data, 56, textHeader, textBytes, 0x80800005);
        Array(data, 72, combinationHeader, count, 0x80809A8E);
        int cursor = textStart;
        for (int i = 0; i < count; i++)
        {
            int part = partStart + 32 * i;
            data.AsSpan(part, 8).Fill(head);
            data.AsSpan(part + 0x1A, 6).Fill(splitPrototype && i == 1 ? (byte)0x33 : tail);
            Q(data, part + 8, cursor - part - 8);
            U(data, part + 0x10, EmptyHash);
            S(data, part + 0x14, (ushort)texts[i].Length);
            S(data, part + 0x16, (ushort)(lengthIsBytes ? texts[i].Length : strings[i].Text.EnumerateRunes().Count()));
            texts[i].CopyTo(data, cursor);
            cursor += texts[i].Length + terminator;
            int combination = combinationStart + 16 * i;
            Q(data, combination, part - combination);
            Q(data, combination + 8, 1);
        }
        return (container, data);
    }

    /** Writes an array header for a field, with the marker and type. */
    public static void Array(byte[] bytes, int field, int header, long count, uint type)
    {
        Q(bytes, field, count); Q(bytes, field + 8, header - field - 8);
        U(bytes, header - 4, ArrayMarker); Q(bytes, header, count); U(bytes, header + 8, type);
    }

    /** Rounds up to a multiple of 16. */
    public static int Align(int value) => (value + 15) & ~15;
    /** Writes a little-endian 32-bit value. */
    public static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    /** Writes a little-endian 16-bit value. */
    public static void S(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    /** Writes a little-endian 64-bit value. */
    public static void Q(byte[] bytes, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), value);
    /** Reads a little-endian 32-bit value. */
    public static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    /** Reads a little-endian 16-bit value. */
    public static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    /** Reads a little-endian signed 64-bit value. */
    public static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));

    /** Throws when the condition is false. */
    public static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }

    /** Requires the action to throw the given exception type. */
    public static void Reject<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidDataException(message);
    }

    /** True when the two arrays differ only inside the given ranges. */
    public static bool OnlyChanged(byte[] before, byte[] after, params (int Offset, int Length)[] ranges)
    {
        if (before.Length != after.Length) return false;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i] && !ranges.Any(range => i >= range.Offset && i < range.Offset + range.Length)) return false;
        return true;
    }
}
