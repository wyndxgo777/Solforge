using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Bit arrays pack fields least significant bit first into little-endian 32-bit words. */
internal static class TomeBits
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /** Bit count needed to represent the value. */
    internal static int Length(uint value) => 32 - System.Numerics.BitOperations.LeadingZeroCount(value);
    /** 32-bit words needed to hold the given number of bits. */
    internal static int Words(long bits) => checked((int)((bits + 31) >> 5));

    /**
     * Reads one field of a bit array.
     * @param data Array bytes, a whole number of words.
     * @param bit First bit of the field.
     * @param width Field width, at most 32.
     * @return The field value.
     */
    internal static uint Get(ReadOnlySpan<byte> data, long bit, int width)
    {
        if (width == 0) return 0;
        int word = checked((int)(bit >> 5)), shift = (int)(bit & 31);
        ulong window = BinaryPrimitives.ReadUInt32LittleEndian(data[(word * 4)..]);
        if (shift + width > 32) window |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data[((word + 1) * 4)..]) << 32;
        return (uint)(window >> shift & ((1UL << width) - 1));
    }

    /** CRC-32C, reflected polynomial 0x82F63B78, with initial and final xor 0xFFFFFFFF. */
    internal static uint Crc32C(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data) crc = crc >> 8 ^ CrcTable[(byte)(crc ^ value)];
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? c >> 1 ^ 0x82F63B78 : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /** Node words for 2-bit codes, then the rank table the runtime uses to find children. */
    internal static uint[] KdData(IReadOnlyList<byte> codes)
    {
        int n = codes.Count, nodeWords = Words(2L * n);
        int top = n >> 16, mid = (n >> 8) - (n >> 16), bottom = (n >> 4) - (n >> 8);
        int midStart = top, bottomStart = midStart + (mid + 1 >> 1);
        var data = new uint[nodeWords + bottomStart + (bottom + 3 >> 2)];
        for (int i = 0; i < n; i++) data[i >> 4] |= (uint)codes[i] << (2 * (i & 15));
        if (n == 0) return data;
        int topIndex = 0, midIndex = 0, bottomIndex = 0, current = InnerCount(data[0]), topBase = 0, midBase = 0;
        for (int i = 1; i < nodeWords; i++)
        {
            if ((i & 0xFFF) == 0) { topBase = midBase = current; data[nodeWords + topIndex++] = (uint)current; }
            else if ((i & 0xF) == 0)
            {
                midBase = current;
                data[nodeWords + midStart + (midIndex >> 1)] |= (uint)((current - topBase) & 0xFFFF) << (midIndex & 1) * 16;
                midIndex++;
            }
            else
            {
                data[nodeWords + bottomStart + (bottomIndex >> 2)] |= (uint)((current - midBase) & 0xFF) << (bottomIndex & 3) * 8;
                bottomIndex++;
            }
            current += InnerCount(data[i]);
        }
        return data;
    }

    /** Word count of the data of a k-d structure over n entries (base bits plus its summary levels). */
    internal static int KdDataWords(int n) =>
        Words(2L * n) + (n >> 16) + ((n >> 8) - (n >> 16) + 1 >> 1) + ((n >> 4) - (n >> 8) + 3 >> 2);

    private static int InnerCount(uint word)
    {
        int leaves = 0;
        for (int k = 0; k < 16; k++) if ((word >> 2 * k & 3) == 3) leaves++;
        return 16 - leaves;
    }
}

/** Collects fields into a bit array. */
internal sealed class TomeBitWriter
{
    private readonly List<uint> words = [];
    private long position;

    internal long Position => position;

    /** Appends a field of up to 64 bits; slot paths of deep tile trees exceed 32. */
    internal void Put(ulong value, int width)
    {
        if (width == 0) return;
        if (width > 64 || width < 64 && value >> width != 0) throw new InvalidDataException($"Value {value} does not fit {width} bits.");
        for (int done = 0; done < width;)
        {
            int shift = (int)(position & 31), word = (int)(position >> 5), take = Math.Min(32 - shift, width - done);
            while (words.Count <= word) words.Add(0);
            words[word] |= (uint)((value >> done) & ((1UL << take) - 1)) << shift;
            done += take;
            position += take;
        }
    }

    /** Returns the written bits as little-endian 32-bit words, in bytes. */
    internal byte[] ToBytes()
    {
        int count = TomeBits.Words(position);
        var bytes = new byte[count * 4];
        for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
}
