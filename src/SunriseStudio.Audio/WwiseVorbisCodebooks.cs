using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/**
 * The external codebook library Wwise Vorbis setups name by id.
 * Built from the libvorbis/aoTuV 6.03 encoder books; book layout ported from ww2ogg (BSD).
 */
internal static class WwiseVorbisCodebooks
{
    private const string ResourceName = "SunriseStudio.Audio.WwiseVorbisCodebooks";
    private static readonly Lazy<(byte[] Data, int[] Offsets)> Library = new(Load);

    /** Compact bytes of one book. */
    public static ReadOnlySpan<byte> Get(int id)
    {
        var (data, offsets) = Library.Value;
        if ((uint)id >= (uint)(offsets.Length - 1))
            throw new InvalidDataException($"Vorbis setup names codebook {id}, which the library does not have.");
        return data.AsSpan(offsets[id], offsets[id + 1] - offsets[id]);
    }

    /**
     * Writes one library book as a standard Vorbis codebook.
     * @param id Library id from the setup.
     * @param output Setup packet being rebuilt.
     */
    public static void Rebuild(int id, VorbisBitWriter output)
    {
        byte[] book = Get(id).ToArray();
        var input = new VorbisBitReader(book, 0, book.Length);
        output.Write(0x564342, 24);                                      // "BCV" sync pattern
        uint dimensions = input.Read(4), entries = input.Read(14);
        output.Write(dimensions, 16);
        output.Write(entries, 24);
        uint ordered = input.Read(1);
        output.Write(ordered, 1);
        if (ordered != 0)
        {
            output.Write(input.Read(5), 5);
            for (uint current = 0; current < entries;)
            {
                int bits = ILog(entries - current);
                uint number = input.Read(bits);
                output.Write(number, bits);
                current += number;
                if (current > entries) throw new InvalidDataException("Codebook lengths run past its entries.");
            }
        }
        else
        {
            int width = (int)input.Read(3);
            uint sparse = input.Read(1);
            if (width is 0 or > 5) throw new InvalidDataException("Codebook length width is invalid.");
            output.Write(sparse, 1);
            for (uint i = 0; i < entries; i++)
            {
                bool present = true;
                if (sparse != 0)
                {
                    uint flag = input.Read(1);
                    output.Write(flag, 1);
                    present = flag != 0;
                }
                if (present) output.Write(input.Read(width), 5);
            }
        }
        uint lookup = input.Read(1);
        output.Write(lookup, 4);
        if (lookup == 1)
        {
            output.Write(input.Read(32), 32);
            output.Write(input.Read(32), 32);
            uint valueBits = input.Read(4);
            output.Write(valueBits, 4);
            output.Write(input.Read(1), 1);
            long values = QuantValues(entries, dimensions);
            for (long i = 0; i < values; i++) output.Write(input.Read((int)valueBits + 1), (int)valueBits + 1);
        }
        // A book is sized bits / 8 + 1, so a full last byte still has a zero byte after it.
        if (input.BitPosition / 8 + 1 != book.Length) throw new InvalidDataException($"Codebook {id} size is wrong.");
    }

    /** Number of bits needed to hold value (Vorbis ilog). */
    public static int ILog(uint value) => value == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(value);

    /** Largest v with v^dimensions <= entries (Tremor's _book_maptype1_quantvals). */
    public static long QuantValues(uint entries, uint dimensions)
    {
        if (dimensions == 0 || entries == 0) throw new InvalidDataException("Codebook has no dimensions or entries.");
        long values = (long)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        while (values > 0 && Power(values, dimensions) > entries) values--;
        while (Power(values + 1, dimensions) <= entries) values++;
        return values;
    }

    private static double Power(long value, uint exponent)
    {
        double result = 1;
        for (uint i = 0; i < exponent; i++) result *= value;
        return result;
    }

    /** Layout: books, then N+1 little-endian offsets; the last u32 is the table start. */
    private static (byte[] Data, int[] Offsets) Load()
    {
        using var stream = typeof(WwiseVorbisCodebooks).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Vorbis codebook library is missing from the build.");
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        int table = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
        if (table < 0 || table > data.Length - 8 || (data.Length - table) % 4 != 0)
            throw new InvalidDataException("The Vorbis codebook library table is invalid.");
        int[] offsets = new int[(data.Length - table) / 4];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(table + i * 4));
            if (offsets[i] > table || i > 0 && offsets[i] < offsets[i - 1])
                throw new InvalidDataException("The Vorbis codebook library table is invalid.");
        }
        return (data, offsets);
    }
}
