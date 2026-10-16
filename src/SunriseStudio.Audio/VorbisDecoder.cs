namespace SunriseStudio.Audio;

/**
 * Standard Vorbis I audio decoder: setup header in, float PCM out, one packet at a time.
 * Ported from libvorbis 1.3.7 (BSD); output is within 1 LSB of it. Floor type 0 is refused.
 */
internal sealed class VorbisDecoder
{
    private readonly int channels;
    private readonly int[] blockSizes = new int[2];
    private readonly VorbisCodebook[] books;
    private readonly VorbisFloor[] floors;
    private readonly VorbisResidue[] residues;
    private readonly VorbisMapping[] mappings;
    private readonly (bool Long, int Mapping)[] modes;
    private readonly int modeBits;
    private readonly float[][] windows = new float[2][];
    private readonly VorbisMdct[] transforms = new VorbisMdct[2];
    private readonly float[][] block, previous, output;
    private readonly int[][] floorValues;
    private readonly bool[] floorUsed, nonzero, bundleNonzero;
    private readonly float[][] bundle;
    private bool started, previousLong;

    /**
     * Parses a standard setup header.
     * @param channels Channel count from the identification header.
     * @param blockShiftSmall Short block size as a power of two.
     * @param blockShiftLarge Long block size as a power of two.
     * @param setup Whole setup packet, type byte included.
     */
    public VorbisDecoder(int channels, int blockShiftSmall, int blockShiftLarge, byte[] setup)
    {
        if (channels is < 1 or > 255) throw new InvalidDataException("Vorbis channel count is invalid.");
        if (blockShiftSmall is < 6 or > 13 || blockShiftLarge is < 6 or > 13 || blockShiftSmall > blockShiftLarge)
            throw new InvalidDataException("Vorbis block sizes are invalid.");
        this.channels = channels;
        blockSizes[0] = 1 << blockShiftSmall;
        blockSizes[1] = 1 << blockShiftLarge;

        var r = new VorbisBitReader(setup, 0, setup.Length);
        if (r.Read(8) != 5) throw new InvalidDataException("Vorbis setup header type is wrong.");
        foreach (byte c in "vorbis"u8)
            if (r.Read(8) != c) throw new InvalidDataException("Vorbis setup header is missing its signature.");

        books = new VorbisCodebook[r.Read(8) + 1];
        for (int i = 0; i < books.Length; i++) books[i] = VorbisCodebook.Parse(r);
        uint times = r.Read(6) + 1;
        for (uint i = 0; i < times; i++)
            if (r.Read(16) != 0) throw new InvalidDataException("Vorbis time-domain transform is not zero.");
        floors = new VorbisFloor[r.Read(6) + 1];
        for (int i = 0; i < floors.Length; i++) floors[i] = VorbisFloor.Parse(r, books);
        residues = new VorbisResidue[r.Read(6) + 1];
        for (int i = 0; i < residues.Length; i++) residues[i] = VorbisResidue.Parse(r, books);
        mappings = new VorbisMapping[r.Read(6) + 1];
        for (int i = 0; i < mappings.Length; i++) mappings[i] = VorbisMapping.Parse(r, channels, floors.Length, residues.Length);
        modes = new (bool, int)[r.Read(6) + 1];
        for (int i = 0; i < modes.Length; i++)
        {
            bool isLong = r.Read(1) != 0;
            if (r.Read(16) != 0 || r.Read(16) != 0) throw new InvalidDataException("Vorbis mode window or transform is not zero.");
            uint mapping = r.Read(8);
            if (mapping >= mappings.Length) throw new InvalidDataException("Vorbis mode names a missing mapping.");
            modes[i] = (isLong, (int)mapping);
        }
        if (r.Read(1) != 1) throw new InvalidDataException("Vorbis setup framing bit is missing.");
        modeBits = WwiseVorbisCodebooks.ILog((uint)modes.Length - 1);

        for (int i = 0; i < 2; i++)
        {
            windows[i] = Window(blockSizes[i] / 2);
            transforms[i] = new VorbisMdct(blockSizes[i]);
        }
        int maxPosts = floors.Max(f => f.Posts);
        block = new float[channels][];
        previous = new float[channels][];
        output = new float[channels][];
        floorValues = new int[channels][];
        for (int c = 0; c < channels; c++)
        {
            block[c] = new float[blockSizes[1]];
            previous[c] = new float[blockSizes[1] / 2];
            output[c] = new float[blockSizes[1]];
            floorValues[c] = new int[maxPosts];
        }
        floorUsed = new bool[channels];
        nonzero = new bool[channels];
        bundleNonzero = new bool[channels];
        bundle = new float[channels][];
    }

    /** Samples of one channel from the last Decode call. */
    public ReadOnlySpan<float> Output(int channel, int frames) => output[channel].AsSpan(0, frames);

    /**
     * Decodes one audio packet and overlaps it with the previous one.
     * @param packet One audio packet.
     * @param wwise True for a Wwise packet, which has no type bit and no window flags.
     * @return Frames now in Output; the first packet only primes the overlap and returns 0.
     */
    public int Decode(ReadOnlySpan<byte> packet, bool wwise)
    {
        var r = new VorbisPacketReader(packet);
        if (!wwise && r.Read(1) != 0) throw new InvalidDataException("Vorbis packet is not audio.");
        int mode = r.Read(modeBits);
        if (mode < 0 || mode >= modes.Length) throw new InvalidDataException("Vorbis packet names a missing mode.");
        bool isLong = modes[mode].Long;
        // A rebuilt Wwise packet ends in 7 or 5 zero pad bits, and reads may use them.
        if (wwise) r.Pad(isLong ? 5 : 7);
        // libvorbis reads the window flags here but windows from the block sizes it has seen.
        else if (isLong && (r.Read(1) < 0 || r.Read(1) < 0)) throw new InvalidDataException("Vorbis packet is truncated.");
        int n = blockSizes[isLong ? 1 : 0];
        Inverse(ref r, mappings[modes[mode].Mapping], n, isLong ? 1 : 0);
        return Overlap(isLong);
    }

    /** libvorbis mapping0_inverse: floors, residue, coupling, envelope, then the transform. */
    private void Inverse(ref VorbisPacketReader r, VorbisMapping map, int n, int size)
    {
        int half = n / 2;
        for (int c = 0; c < channels; c++)
        {
            nonzero[c] = floorUsed[c] = floors[map.Floor(c)].Unpack(ref r, books, floorValues[c]);
            Array.Clear(block[c], 0, half);
        }
        foreach (var (magnitude, angle) in map.Coupling)
            if (nonzero[magnitude] || nonzero[angle]) nonzero[magnitude] = nonzero[angle] = true;

        for (int s = 0; s < map.Submaps; s++)
        {
            int count = 0;
            for (int c = 0; c < channels; c++)
            {
                if (map.Mux[c] != s) continue;
                bundleNonzero[count] = nonzero[c];
                bundle[count++] = block[c];
            }
            residues[map.SubmapResidue[s]].Decode(ref r, bundle, bundleNonzero, count, n);
        }

        for (int i = map.Coupling.Length - 1; i >= 0; i--)
        {
            float[] m = block[map.Coupling[i].Magnitude], a = block[map.Coupling[i].Angle];
            for (int j = 0; j < half; j++)
            {
                float mag = m[j], ang = a[j];
                if (mag > 0)
                {
                    if (ang > 0) { m[j] = mag; a[j] = mag - ang; }
                    else { a[j] = mag; m[j] = mag + ang; }
                }
                else
                {
                    if (ang > 0) { m[j] = mag; a[j] = mag + ang; }
                    else { a[j] = mag; m[j] = mag - ang; }
                }
            }
        }

        for (int c = 0; c < channels; c++)
        {
            // Coupling can mark a channel nonzero; only its own floor decides the envelope.
            if (floorUsed[c]) floors[map.Floor(c)].Apply(floorValues[c], block[c], half);
            else Array.Clear(block[c], 0, half);
            transforms[size].Inverse(block[c]);
        }
    }

    /** libvorbis vorbis_synthesis_blockin: windowed overlap of this block's left half with the last right half. */
    private int Overlap(bool isLong)
    {
        int n0 = blockSizes[0] / 2, n1 = blockSizes[1] / 2, n = isLong ? n1 : n0;
        int frames = 0;
        for (int c = 0; c < channels; c++)
        {
            float[] pcm = previous[c], p = block[c], o = output[c];
            if (started)
            {
                if (previousLong && isLong)
                {
                    float[] w = windows[1];
                    for (int i = 0; i < n1; i++) o[i] = pcm[i] * w[n1 - i - 1] + p[i] * w[i];
                    frames = n1;
                }
                else if (previousLong)
                {
                    float[] w = windows[0];
                    int lead = n1 / 2 - n0 / 2;
                    pcm.AsSpan(0, lead).CopyTo(o);
                    for (int i = 0; i < n0; i++) o[lead + i] = pcm[lead + i] * w[n0 - i - 1] + p[i] * w[i];
                    frames = lead + n0;
                }
                else if (isLong)
                {
                    float[] w = windows[0];
                    int lead = n1 / 2 - n0 / 2;
                    int i = 0;
                    for (; i < n0; i++) o[i] = pcm[i] * w[n0 - i - 1] + p[lead + i] * w[i];
                    for (; i < n1 / 2 + n0 / 2; i++) o[i] = p[lead + i];
                    frames = n1 / 2 + n0 / 2;
                }
                else
                {
                    float[] w = windows[0];
                    for (int i = 0; i < n0; i++) o[i] = pcm[i] * w[n0 - i - 1] + p[i] * w[i];
                    frames = n0;
                }
            }
            p.AsSpan(n, n).CopyTo(pcm);
        }
        started = true;
        previousLong = isLong;
        return frames;
    }

    /** Rising half of the Vorbis power-sine window. */
    private static float[] Window(int half)
    {
        var w = new float[half];
        for (int i = 0; i < half; i++)
        {
            double s = Math.Sin((i + 0.5) / half * Math.PI / 2);
            w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }
        return w;
    }
}

/** Bit reader over one audio packet; past the end every read returns -1, as libogg's does. */
internal ref struct VorbisPacketReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    private long total = data.Length * 8L;
    private long position;

    public readonly int Remaining => position >= total ? 0 : (int)(total - position);

    /** Lets reads run the given number of zero bits past the data. */
    public void Pad(int bits) => total += bits;

    /** Reads up to 31 bits, least significant first. */
    public int Read(int bits)
    {
        if (bits == 0) return 0;
        if (position + bits > total)
        {
            position = total + 1;
            return -1;
        }
        int value = (int)Peek(bits);
        position += bits;
        return value;
    }

    /** Next bits without consuming them; bits past the end read as zero. */
    public readonly uint Peek(int bits)
    {
        if (position >= total) return 0;
        int index = (int)(position >> 3), shift = (int)(position & 7);
        ulong window = 0;
        for (int i = 0; i < 5 && index + i < data.Length; i++) window |= (ulong)data[index + i] << (8 * i);
        return (uint)((window >> shift) & ((1UL << bits) - 1));
    }

    /** Advances the read position by bits. */
    public void Skip(int bits) => position += bits;

    /** Marks the packet as ended; every later read fails. */
    public void End() => position = total + 1;
}

/** One codebook: Huffman decode plus the optional VQ value table. Ported from libvorbis sharedbook.c. */
internal sealed class VorbisCodebook
{
    private const int TableBits = 10;
    private int[] table = [];
    private int tableBits;
    private int[] tree = [];
    private float[]? values;

    public int Dimensions { get; private init; }
    public int Entries { get; private init; }
    public int UsedEntries { get; private init; }
    public int LookupType { get; private init; }

    /** libvorbis vorbis_staticbook_unpack, then the decode tables. */
    public static VorbisCodebook Parse(VorbisBitReader r)
    {
        if (r.Read(24) != 0x564342) throw new InvalidDataException("Vorbis codebook sync pattern is missing.");
        int dimensions = (int)r.Read(16), entries = (int)r.Read(24);
        if (WwiseVorbisCodebooks.ILog((uint)dimensions) + WwiseVorbisCodebooks.ILog((uint)entries) > 24)
            throw new InvalidDataException("Vorbis codebook is too large.");
        var lengths = new byte[entries];
        if (r.Read(1) == 0)
        {
            bool sparse = r.Read(1) != 0;
            for (int i = 0; i < entries; i++)
                lengths[i] = !sparse || r.Read(1) != 0 ? (byte)(r.Read(5) + 1) : (byte)0;
        }
        else
        {
            int length = (int)r.Read(5) + 1;
            for (int i = 0; i < entries; length++)
            {
                int count = (int)r.Read(WwiseVorbisCodebooks.ILog((uint)(entries - i)));
                if (length > 32 || count > entries - i || count > 0 && (count - 1) >> (length - 1) > 1)
                    throw new InvalidDataException("Vorbis codebook lengths are invalid.");
                for (int j = 0; j < count; j++) lengths[i++] = (byte)length;
            }
        }

        int lookup = (int)r.Read(4);
        float[]? values = null;
        if (lookup is 1 or 2)
        {
            float min = Float32(r.Read(32)), delta = Float32(r.Read(32));
            int bits = (int)r.Read(4) + 1;
            bool sequence = r.Read(1) != 0;
            long count = lookup == 1
                ? dimensions == 0 ? 0 : WwiseVorbisCodebooks.QuantValues((uint)entries, (uint)dimensions)
                : (long)entries * dimensions;
            if (count > r.Remaining / bits + 1) throw new InvalidDataException("Vorbis codebook values are truncated.");
            var quant = new uint[count];
            for (long i = 0; i < count; i++) quant[i] = r.Read(bits);
            values = Unquantize(lengths, dimensions, lookup, quant, min, delta, sequence);
        }
        else if (lookup != 0) throw new InvalidDataException("Vorbis codebook lookup type is invalid.");

        var book = new VorbisCodebook
        {
            Dimensions = dimensions, Entries = entries, LookupType = lookup,
            UsedEntries = lengths.Count(l => l > 0)
        };
        book.values = values;
        book.BuildDecoder(lengths);
        return book;
    }

    /** Values of one entry; only for books with a lookup table. */
    public ReadOnlySpan<float> Vector(int entry) => values.AsSpan(entry * Dimensions, Dimensions);

    /** Next entry number, or -1 when the packet ends; a failed read ends the packet. */
    public int Decode(ref VorbisPacketReader r)
    {
        if (UsedEntries == 0) return -1;
        int available = r.Remaining;
        int hit = table[r.Peek(tableBits)];
        if (hit >= 0)
        {
            int length = hit & 63;
            if (length <= available)
            {
                r.Skip(length);
                return hit >> 6;
            }
            r.End();
            return -1;
        }
        for (int node = 0; ;)
        {
            int bit = r.Read(1);
            if (bit < 0) return -1;
            int next = tree[node * 2 + bit];
            if (next < 0) return -1 - next;
            if (next == 0)
            {
                r.End();
                return -1;
            }
            node = next;
        }
    }

    /** libvorbis _float32_unpack. */
    private static float Float32(uint value)
    {
        double mantissa = value & 0x1FFFFF;
        if ((value & 0x80000000) != 0) mantissa = -mantissa;
        int exponent = (int)((value & 0x7FE00000) >> 21) - 20 - 768;
        return (float)Math.ScaleB(mantissa, Math.Clamp(exponent, -63, 63));
    }

    /** libvorbis _book_unquantize, kept in entry order. */
    private static float[] Unquantize(byte[] lengths, int dimensions, int lookup, uint[] quant, float min, float delta,
        bool sequence)
    {
        int entries = lengths.Length, quantValues = quant.Length;
        var result = new float[(long)entries * dimensions];
        for (int j = 0; j < entries; j++)
        {
            if (lengths[j] == 0) continue;
            float last = 0;
            int divisor = 1;
            for (int k = 0; k < dimensions; k++)
            {
                int index = lookup == 1 ? j / divisor % quantValues : j * dimensions + k;
                float value = (float)(quant[index] * (double)delta + min + last);
                if (sequence) last = value;
                result[j * dimensions + k] = value;
                if (lookup == 1) divisor *= quantValues;
            }
        }
        return result;
    }

    /** Assigns codewords as libvorbis _make_words does and builds a lookup table plus a tree. */
    private void BuildDecoder(byte[] lengths)
    {
        var words = MakeWords(lengths) ?? throw new InvalidDataException("Vorbis codebook lengths do not form a code.");
        int maxLength = lengths.Length == 0 ? 0 : lengths.Max();
        if (UsedEntries == 0) return;
        if (UsedEntries == 1)
        {
            // A single entry is one codeword of length 1 that matches either bit.
            int only = Array.FindIndex(lengths, l => l > 0);
            tableBits = 1;
            table = [only << 6 | 1, only << 6 | 1];
            tree = [-1 - only, -1 - only];
            return;
        }
        tableBits = Math.Min(maxLength, TableBits);
        table = new int[1 << tableBits];
        Array.Fill(table, -1);
        var nodes = new List<int> { 0, 0 };
        for (int entry = 0; entry < lengths.Length; entry++)
        {
            int length = lengths[entry];
            if (length == 0) continue;
            uint word = words[entry], reversed = 0;
            int node = 0;
            for (int k = 0; k < length; k++)
            {
                int bit = (int)(word >> (length - 1 - k) & 1);
                reversed |= (uint)bit << k;
                int slot = node * 2 + bit;
                if (k == length - 1) nodes[slot] = -1 - entry;
                else
                {
                    if (nodes[slot] < 0) throw new InvalidDataException("Vorbis codebook code is not prefix-free.");
                    if (nodes[slot] == 0)
                    {
                        nodes[slot] = nodes.Count / 2;
                        nodes.Add(0);
                        nodes.Add(0);
                    }
                    node = nodes[slot];
                }
            }
            if (length <= tableBits)
                for (int j = 0; j < 1 << (tableBits - length); j++) table[reversed | (uint)(j << length)] = entry << 6 | length;
        }
        tree = [.. nodes];
    }

    /** libvorbis _make_words: lowest codewords first; null for an over- or under-full tree. */
    private static uint[]? MakeWords(byte[] lengths)
    {
        var marker = new uint[33];
        var words = new uint[lengths.Length];
        int count = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0) continue;
            uint entry = marker[length];
            if (length < 32 && entry >> length != 0) return null;
            words[i] = entry;
            count++;
            for (int j = length; j > 0; j--)
            {
                if ((marker[j] & 1) != 0)
                {
                    if (j == 1) marker[1]++;
                    else marker[j] = marker[j - 1] << 1;
                    break;
                }
                marker[j]++;
            }
            for (int j = length + 1; j < 33; j++)
            {
                if (marker[j] >> 1 != entry) break;
                entry = marker[j];
                marker[j] = marker[j - 1] << 1;
            }
        }
        if (!(count == 1 && marker[2] == 2))
            for (int i = 1; i < 33; i++)
                if ((marker[i] & (0xFFFFFFFFu >> (32 - i))) != 0) return null;
        return words;
    }
}

/** Floor type 1. Ported from libvorbis floor1.c. */
internal sealed class VorbisFloor
{
    /** libvorbis VIF_POSIT: at most 63 posts besides the two ends. */
    private const int MaxPosts = 63;
    private static readonly int[] QuantRange = [256, 128, 86, 64];

    private int[] partitionClass = [], classDimensions = [], classSubclasses = [], classBook = [], postList = [];
    private int[][] subclassBooks = [];
    private int[] forwardIndex = [], lowNeighbour = [], highNeighbour = [];
    private int multiplier, quantQ;

    public int Posts { get; private set; }

    /** libvorbis floor1_unpack and floor1_look. */
    public static VorbisFloor Parse(VorbisBitReader r, VorbisCodebook[] books)
    {
        if (r.Read(16) != 1) throw new InvalidDataException("Vorbis floor type 0 is not supported.");
        var f = new VorbisFloor();
        int partitions = (int)r.Read(5), maxClass = -1;
        f.partitionClass = new int[partitions];
        for (int i = 0; i < partitions; i++) maxClass = Math.Max(maxClass, f.partitionClass[i] = (int)r.Read(4));
        f.classDimensions = new int[maxClass + 1];
        f.classSubclasses = new int[maxClass + 1];
        f.classBook = new int[maxClass + 1];
        f.subclassBooks = new int[maxClass + 1][];
        for (int i = 0; i <= maxClass; i++)
        {
            f.classDimensions[i] = (int)r.Read(3) + 1;
            f.classSubclasses[i] = (int)r.Read(2);
            if (f.classSubclasses[i] != 0 && (f.classBook[i] = (int)r.Read(8)) >= books.Length)
                throw new InvalidDataException("Vorbis floor names a missing class book.");
            f.subclassBooks[i] = new int[1 << f.classSubclasses[i]];
            for (int k = 0; k < f.subclassBooks[i].Length; k++)
                if ((f.subclassBooks[i][k] = (int)r.Read(8) - 1) >= books.Length)
                    throw new InvalidDataException("Vorbis floor names a missing subclass book.");
        }
        f.multiplier = (int)r.Read(2) + 1;
        f.quantQ = QuantRange[f.multiplier - 1];
        int rangeBits = (int)r.Read(4), count = 0;
        var posts = new List<int> { 0, 1 << rangeBits };
        foreach (int c in f.partitionClass)
        {
            count += f.classDimensions[c];
            if (count > MaxPosts) throw new InvalidDataException("Vorbis floor has too many posts.");
            for (int k = 0; k < f.classDimensions[c]; k++) posts.Add((int)r.Read(rangeBits));
        }
        f.postList = [.. posts];
        f.Posts = posts.Count;
        f.forwardIndex = [.. Enumerable.Range(0, f.Posts).OrderBy(i => f.postList[i])];
        for (int i = 1; i < f.Posts; i++)
            if (f.postList[f.forwardIndex[i]] == f.postList[f.forwardIndex[i - 1]])
                throw new InvalidDataException("Vorbis floor repeats a post.");
        f.lowNeighbour = new int[f.Posts];
        f.highNeighbour = new int[f.Posts];
        for (int i = 2; i < f.Posts; i++)
        {
            int low = 0, high = 1, lowX = 0, highX = f.postList[1], x = f.postList[i];
            for (int j = 0; j < i; j++)
            {
                int p = f.postList[j];
                if (p > lowX && p < x) { low = j; lowX = p; }
                if (p < highX && p > x) { high = j; highX = p; }
            }
            f.lowNeighbour[i] = low;
            f.highNeighbour[i] = high;
        }
        return f;
    }

    /**
     * libvorbis floor1_inverse1: reads the posts into fit values.
     * @return False when the channel is unused or the packet ends.
     */
    public bool Unpack(ref VorbisPacketReader r, VorbisCodebook[] books, int[] fit)
    {
        if (r.Read(1) != 1) return false;
        int bits = WwiseVorbisCodebooks.ILog((uint)quantQ - 1);
        fit[0] = r.Read(bits);
        fit[1] = r.Read(bits);
        for (int i = 0, j = 2; i < partitionClass.Length; i++)
        {
            int c = partitionClass[i], dimensions = classDimensions[c], subBits = classSubclasses[c];
            int value = 0;
            if (subBits != 0 && (value = books[classBook[c]].Decode(ref r)) == -1) return false;
            for (int k = 0; k < dimensions; k++)
            {
                int book = subclassBooks[c][value & ((1 << subBits) - 1)];
                value >>= subBits;
                if (book < 0) fit[j + k] = 0;
                else if ((fit[j + k] = books[book].Decode(ref r)) == -1) return false;
            }
            j += dimensions;
        }
        for (int i = 2; i < Posts; i++)
        {
            int low = lowNeighbour[i], high = highNeighbour[i];
            int predicted = RenderPoint(postList[low], postList[high], fit[low], fit[high], postList[i]);
            int highRoom = quantQ - predicted, lowRoom = predicted;
            int room = Math.Min(highRoom, lowRoom) << 1, value = fit[i];
            if (value != 0)
            {
                if (value >= room) value = highRoom > lowRoom ? value - lowRoom : -1 - (value - highRoom);
                else value = (value & 1) != 0 ? -((value + 1) >> 1) : value >> 1;
                fit[i] = (value + predicted) & 0x7FFF;
                fit[low] &= 0x7FFF;
                fit[high] &= 0x7FFF;
            }
            else fit[i] = predicted | 0x8000;
        }
        return true;
    }

    /** libvorbis floor1_inverse2: multiplies the residue by the rendered envelope. */
    public void Apply(int[] fit, float[] output, int n)
    {
        int highX = 0, lowX = 0, lowY = Math.Clamp(fit[0] * multiplier, 0, 255);
        for (int j = 1; j < Posts; j++)
        {
            int current = forwardIndex[j];
            int highY = fit[current] & 0x7FFF;
            if (highY != fit[current]) continue;
            highX = postList[current];
            highY = Math.Clamp(highY * multiplier, 0, 255);
            RenderLine(n, lowX, highX, lowY, highY, output);
            lowX = highX;
            lowY = highY;
        }
        for (int j = highX; j < n; j++) output[j] *= InverseDb[lowY];
    }

    private static int RenderPoint(int x0, int x1, int y0, int y1, int x)
    {
        y0 &= 0x7FFF;
        y1 &= 0x7FFF;
        int dy = y1 - y0, adx = x1 - x0, err = Math.Abs(dy) * (x - x0), off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    /** libvorbis render_line: scales the residue along one floor segment. */
    private static void RenderLine(int n, int x0, int x1, int y0, int y1, float[] d)
    {
        int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy), b = dy / adx, sy = dy < 0 ? b - 1 : b + 1;
        int x = x0, y = y0, err = 0;
        ady -= Math.Abs(b * adx);
        if (n > x1) n = x1;
        if (x < n) d[x] *= InverseDb[y];
        while (++x < n)
        {
            err += ady;
            if (err >= adx)
            {
                err -= adx;
                y += sy;
            }
            else y += b;
            d[x] *= InverseDb[y];
        }
    }

    /** Vorbis I floor1_inverse_dB_table, as libvorbis prints it. */
    private static readonly float[] InverseDb =
    [
        1.0649863e-07f, 1.1341951e-07f, 1.2079015e-07f, 1.2863978e-07f,
        1.3699951e-07f, 1.4590251e-07f, 1.5538408e-07f, 1.6548181e-07f,
        1.7623575e-07f, 1.8768855e-07f, 1.9988561e-07f, 2.128753e-07f,
        2.2670913e-07f, 2.4144197e-07f, 2.5713223e-07f, 2.7384213e-07f,
        2.9163793e-07f, 3.1059021e-07f, 3.3077411e-07f, 3.5226968e-07f,
        3.7516214e-07f, 3.9954229e-07f, 4.2550680e-07f, 4.5315863e-07f,
        4.8260743e-07f, 5.1396998e-07f, 5.4737065e-07f, 5.8294187e-07f,
        6.2082472e-07f, 6.6116941e-07f, 7.0413592e-07f, 7.4989464e-07f,
        7.9862701e-07f, 8.5052630e-07f, 9.0579828e-07f, 9.6466216e-07f,
        1.0273513e-06f, 1.0941144e-06f, 1.1652161e-06f, 1.2409384e-06f,
        1.3215816e-06f, 1.4074654e-06f, 1.4989305e-06f, 1.5963394e-06f,
        1.7000785e-06f, 1.8105592e-06f, 1.9282195e-06f, 2.0535261e-06f,
        2.1869758e-06f, 2.3290978e-06f, 2.4804557e-06f, 2.6416497e-06f,
        2.8133190e-06f, 2.9961443e-06f, 3.1908506e-06f, 3.3982101e-06f,
        3.6190449e-06f, 3.8542308e-06f, 4.1047004e-06f, 4.3714470e-06f,
        4.6555282e-06f, 4.9580707e-06f, 5.2802740e-06f, 5.6234160e-06f,
        5.9888572e-06f, 6.3780469e-06f, 6.7925283e-06f, 7.2339451e-06f,
        7.7040476e-06f, 8.2047000e-06f, 8.7378876e-06f, 9.3057248e-06f,
        9.9104632e-06f, 1.0554501e-05f, 1.1240392e-05f, 1.1970856e-05f,
        1.2748789e-05f, 1.3577278e-05f, 1.4459606e-05f, 1.5399272e-05f,
        1.6400004e-05f, 1.7465768e-05f, 1.8600792e-05f, 1.9809576e-05f,
        2.1096914e-05f, 2.2467911e-05f, 2.3928002e-05f, 2.5482978e-05f,
        2.7139006e-05f, 2.8902651e-05f, 3.0780908e-05f, 3.2781225e-05f,
        3.4911534e-05f, 3.7180282e-05f, 3.9596466e-05f, 4.2169667e-05f,
        4.4910090e-05f, 4.7828601e-05f, 5.0936773e-05f, 5.4246931e-05f,
        5.7772202e-05f, 6.1526565e-05f, 6.5524908e-05f, 6.9783085e-05f,
        7.4317983e-05f, 7.9147585e-05f, 8.4291040e-05f, 8.9768747e-05f,
        9.5602426e-05f, 0.00010181521f, 0.00010843174f, 0.00011547824f,
        0.00012298267f, 0.00013097477f, 0.00013948625f, 0.00014855085f,
        0.00015820453f, 0.00016848555f, 0.00017943469f, 0.00019109536f,
        0.00020351382f, 0.00021673929f, 0.00023082423f, 0.00024582449f,
        0.00026179955f, 0.00027881276f, 0.00029693158f, 0.00031622787f,
        0.00033677814f, 0.00035866388f, 0.00038197188f, 0.00040679456f,
        0.00043323036f, 0.00046138411f, 0.00049136745f, 0.00052329927f,
        0.00055730621f, 0.00059352311f, 0.00063209358f, 0.00067317058f,
        0.00071691700f, 0.00076350630f, 0.00081312324f, 0.00086596457f,
        0.00092223983f, 0.00098217216f, 0.0010459992f, 0.0011139742f,
        0.0011863665f, 0.0012634633f, 0.0013455702f, 0.0014330129f,
        0.0015261382f, 0.0016253153f, 0.0017309374f, 0.0018434235f,
        0.0019632195f, 0.0020908006f, 0.0022266726f, 0.0023713743f,
        0.0025254795f, 0.0026895994f, 0.0028643847f, 0.0030505286f,
        0.0032487691f, 0.0034598925f, 0.0036847358f, 0.0039241906f,
        0.0041792066f, 0.0044507950f, 0.0047400328f, 0.0050480668f,
        0.0053761186f, 0.0057254891f, 0.0060975636f, 0.0064938176f,
        0.0069158225f, 0.0073652516f, 0.0078438871f, 0.0083536271f,
        0.0088964928f, 0.009474637f, 0.010090352f, 0.010746080f,
        0.011444421f, 0.012188144f, 0.012980198f, 0.013823725f,
        0.014722068f, 0.015678791f, 0.016697687f, 0.017782797f,
        0.018938423f, 0.020169149f, 0.021479854f, 0.022875735f,
        0.024362330f, 0.025945531f, 0.027631618f, 0.029427276f,
        0.031339626f, 0.033376252f, 0.035545228f, 0.037855157f,
        0.040315199f, 0.042935108f, 0.045725273f, 0.048696758f,
        0.051861348f, 0.055231591f, 0.058820850f, 0.062643361f,
        0.066714279f, 0.071049749f, 0.075666962f, 0.080584227f,
        0.085821044f, 0.091398179f, 0.097337747f, 0.10366330f,
        0.11039993f, 0.11757434f, 0.12521498f, 0.13335215f,
        0.14201813f, 0.15124727f, 0.16107617f, 0.17154380f,
        0.18269168f, 0.19456402f, 0.20720788f, 0.22067342f,
        0.23501402f, 0.25028656f, 0.26655159f, 0.28387361f,
        0.30232132f, 0.32196786f, 0.34289114f, 0.36517414f,
        0.38890521f, 0.41417847f, 0.44109412f, 0.46975890f,
        0.50028648f, 0.53279791f, 0.56742212f, 0.60429640f,
        0.64356699f, 0.68538959f, 0.72993007f, 0.77736504f,
        0.82788260f, 0.88168307f, 0.9389798f, 1f,
    ];
}

/** Residue types 0, 1 and 2. Ported from libvorbis res0.c. */
internal sealed class VorbisResidue
{
    private int type, begin, end, grouping, partitions, stages, partValues;
    private VorbisCodebook classBook = null!;
    private int[] secondStages = [];
    private VorbisCodebook?[][] stageBooks = [];
    private int[][] decodeMap = [];

    /** libvorbis res0_unpack and res0_look. */
    public static VorbisResidue Parse(VorbisBitReader r, VorbisCodebook[] books)
    {
        var s = new VorbisResidue { type = (int)r.Read(16) };
        if (s.type > 2) throw new InvalidDataException("Vorbis residue type is invalid.");
        s.begin = (int)r.Read(24);
        s.end = (int)r.Read(24);
        s.grouping = (int)r.Read(24) + 1;
        s.partitions = (int)r.Read(6) + 1;
        int group = (int)r.Read(8);
        if (group >= books.Length) throw new InvalidDataException("Vorbis residue names a missing class book.");
        s.classBook = books[group];
        s.secondStages = new int[s.partitions];
        for (int j = 0; j < s.partitions; j++)
        {
            int cascade = (int)r.Read(3);
            if (r.Read(1) != 0) cascade |= (int)r.Read(5) << 3;
            s.secondStages[j] = cascade;
        }
        s.stageBooks = new VorbisCodebook?[s.partitions][];
        for (int j = 0; j < s.partitions; j++)
        {
            int count = WwiseVorbisCodebooks.ILog((uint)s.secondStages[j]);
            s.stages = Math.Max(s.stages, count);
            s.stageBooks[j] = new VorbisCodebook?[count];
            for (int k = 0; k < count; k++)
            {
                if ((s.secondStages[j] & 1 << k) == 0) continue;
                int book = (int)r.Read(8);
                if (book >= books.Length || books[book].LookupType == 0)
                    throw new InvalidDataException("Vorbis residue names a missing or valueless book.");
                s.stageBooks[j][k] = books[book];
            }
        }
        int dimensions = s.classBook.Dimensions;
        if (dimensions < 1) throw new InvalidDataException("Vorbis residue class book has no dimensions.");
        s.partValues = 1;
        for (int d = 0; d < dimensions; d++)
            if ((s.partValues *= s.partitions) > s.classBook.Entries)
                throw new InvalidDataException("Vorbis residue class book is too small.");
        s.decodeMap = new int[s.partValues][];
        for (int j = 0; j < s.partValues; j++)
        {
            int value = j, divisor = s.partValues / s.partitions;
            s.decodeMap[j] = new int[dimensions];
            for (int k = 0; k < dimensions; k++)
            {
                int digit = value / divisor;
                value -= digit * divisor;
                divisor /= s.partitions;
                s.decodeMap[j][k] = digit;
            }
        }
        return s;
    }

    /**
     * Adds this packet's residue into the channel vectors.
     * @param vectors Channels of this submap; the first n/2 values are filled.
     * @param nonzero Which of them carry a floor.
     */
    public void Decode(ref VorbisPacketReader r, float[][] vectors, bool[] nonzero, int count, int n)
    {
        if (type == 2)
        {
            bool any = false;
            for (int i = 0; i < count; i++) any |= nonzero[i];
            if (any) Inverse(ref r, vectors, count, n * count / 2, interleaved: true);
            return;
        }
        // Types 0 and 1 skip channels with no floor; their bits are not in the packet.
        var used = new float[count][];
        int active = 0;
        for (int i = 0; i < count; i++)
            if (nonzero[i]) used[active++] = vectors[i];
        if (active > 0) Inverse(ref r, used, active, n / 2, interleaved: false);
    }

    /** libvorbis _01inverse and res2_inverse; a packet that ends early keeps what was read. */
    private void Inverse(ref VorbisPacketReader r, float[][] vectors, int count, int max, bool interleaved)
    {
        int stop = Math.Min(end, max), size = stop - begin;
        if (size <= 0) return;
        int partitionsTotal = size / grouping, perWord = classBook.Dimensions;
        int words = (partitionsTotal + perWord - 1) / perWord, wordChannels = interleaved ? 1 : count;
        var partWord = new int[wordChannels][][];
        for (int c = 0; c < wordChannels; c++) partWord[c] = new int[words][];
        for (int stage = 0; stage < stages; stage++)
        {
            for (int i = 0, l = 0; i < partitionsTotal; l++)
            {
                if (stage == 0)
                    for (int c = 0; c < wordChannels; c++)
                    {
                        int value = classBook.Decode(ref r);
                        if (value == -1 || value >= partValues) return;
                        partWord[c][l] = decodeMap[value];
                    }
                for (int k = 0; k < perWord && i < partitionsTotal; k++, i++)
                    for (int c = 0; c < wordChannels; c++)
                    {
                        int partition = partWord[c][l][k];
                        if ((secondStages[partition] & 1 << stage) == 0) continue;
                        var book = stageBooks[partition][stage];
                        if (book is null) continue;
                        int offset = begin + i * grouping;
                        bool ok = interleaved ? AddInterleaved(book, ref r, vectors, count, offset)
                            : type == 0 ? AddStepped(book, ref r, vectors[c], offset) : AddRun(book, ref r, vectors[c], offset);
                        if (!ok) return;
                    }
            }
        }
    }

    /** libvorbis vorbis_book_decodevs_add (residue 0). */
    private bool AddStepped(VorbisCodebook book, ref VorbisPacketReader r, float[] a, int offset)
    {
        if (book.UsedEntries == 0) return true;
        int dimensions = book.Dimensions, step = grouping / dimensions;
        Span<int> entries = step <= 256 ? stackalloc int[step] : new int[step];
        for (int i = 0; i < step; i++)
            if ((entries[i] = book.Decode(ref r)) == -1) return false;
        for (int i = 0, o = 0; i < dimensions; i++, o += step)
            for (int j = 0; o + j < grouping && j < step; j++) a[offset + o + j] += book.Vector(entries[j])[i];
        return true;
    }

    /** libvorbis vorbis_book_decodev_add (residue 1). */
    private bool AddRun(VorbisCodebook book, ref VorbisPacketReader r, float[] a, int offset)
    {
        if (book.UsedEntries == 0) return true;
        for (int i = 0; i < grouping;)
        {
            int entry = book.Decode(ref r);
            if (entry == -1) return false;
            var t = book.Vector(entry);
            for (int j = 0; i < grouping && j < t.Length;) a[offset + i++] += t[j++];
        }
        return true;
    }

    /** libvorbis vorbis_book_decodevv_add (residue 2): values alternate between channels. */
    private bool AddInterleaved(VorbisCodebook book, ref VorbisPacketReader r, float[][] a, int count, int offset)
    {
        if (book.UsedEntries == 0) return true;
        int channel = 0, m = (offset + grouping) / count;
        for (int i = offset / count; i < m;)
        {
            int entry = book.Decode(ref r);
            if (entry == -1) return false;
            var t = book.Vector(entry);
            for (int j = 0; i < m && j < t.Length; j++)
            {
                a[channel++][i] += t[j];
                if (channel == count)
                {
                    channel = 0;
                    i++;
                }
            }
        }
        return true;
    }
}

/** Mapping type 0: channel coupling and the floor and residue of each submap. */
internal sealed class VorbisMapping
{
    public int Submaps { get; private init; }
    public int[] Mux { get; private init; } = [];
    public int[] SubmapFloor { get; private init; } = [];
    public int[] SubmapResidue { get; private init; } = [];
    public (int Magnitude, int Angle)[] Coupling { get; private init; } = [];

    /** Floor index used by a channel through its submap. */
    public int Floor(int channel) => SubmapFloor[Mux[channel]];

    /** libvorbis mapping0_unpack. */
    public static VorbisMapping Parse(VorbisBitReader r, int channels, int floors, int residues)
    {
        if (r.Read(16) != 0) throw new InvalidDataException("Vorbis mapping type is invalid.");
        int submaps = r.Read(1) != 0 ? (int)r.Read(4) + 1 : 1;
        var coupling = new List<(int, int)>();
        if (r.Read(1) != 0)
        {
            int steps = (int)r.Read(8) + 1, bits = WwiseVorbisCodebooks.ILog((uint)channels - 1);
            for (int i = 0; i < steps; i++)
            {
                int magnitude = (int)r.Read(bits), angle = (int)r.Read(bits);
                if (magnitude == angle || magnitude >= channels || angle >= channels)
                    throw new InvalidDataException("Vorbis channel coupling is invalid.");
                coupling.Add((magnitude, angle));
            }
        }
        if (r.Read(2) != 0) throw new InvalidDataException("Vorbis mapping reserved field is set.");
        var mux = new int[channels];
        if (submaps > 1)
            for (int c = 0; c < channels; c++)
                if ((mux[c] = (int)r.Read(4)) >= submaps) throw new InvalidDataException("Vorbis channel names a missing submap.");
        var floor = new int[submaps];
        var residue = new int[submaps];
        for (int s = 0; s < submaps; s++)
        {
            r.Read(8);
            if ((floor[s] = (int)r.Read(8)) >= floors) throw new InvalidDataException("Vorbis submap names a missing floor.");
            if ((residue[s] = (int)r.Read(8)) >= residues) throw new InvalidDataException("Vorbis submap names a missing residue.");
        }
        return new() { Submaps = submaps, Mux = mux, SubmapFloor = floor, SubmapResidue = residue, Coupling = [.. coupling] };
    }
}

/** Inverse MDCT through a quarter-size complex FFT, in double precision. */
internal sealed class VorbisMdct
{
    private readonly int n, half, quarter;
    private readonly double[] preCos, preSin, postCos, postSin, fftCos, fftSin, re, im, dct;
    private readonly int[] reverse;

    /** Precomputes the twiddle factors and the bit-reversal order for size n. */
    public VorbisMdct(int n)
    {
        this.n = n;
        half = n / 2;
        quarter = n / 4;
        preCos = new double[quarter];
        preSin = new double[quarter];
        postCos = new double[quarter];
        postSin = new double[quarter];
        for (int k = 0; k < quarter; k++)
        {
            preCos[k] = Math.Cos(Math.PI * k / half);
            preSin[k] = Math.Sin(Math.PI * k / half);
            postCos[k] = Math.Cos(Math.PI * (k + 0.25) / half);
            postSin[k] = Math.Sin(Math.PI * (k + 0.25) / half);
        }
        fftCos = new double[Math.Max(1, quarter / 2)];
        fftSin = new double[fftCos.Length];
        for (int k = 0; k < quarter / 2; k++)
        {
            fftCos[k] = Math.Cos(2 * Math.PI * k / quarter);
            fftSin[k] = Math.Sin(2 * Math.PI * k / quarter);
        }
        int bits = WwiseVorbisCodebooks.ILog((uint)quarter) - 1;
        reverse = new int[quarter];
        for (int k = 0; k < quarter; k++)
        {
            int value = 0;
            for (int b = 0; b < bits; b++) value |= (k >> b & 1) << (bits - 1 - b);
            reverse[k] = value;
        }
        re = new double[quarter];
        im = new double[quarter];
        dct = new double[half];
    }

    /**
     * y[i] = sum X[k] cos(2 pi / n (i + 1/2 + n/4)(k + 1/2)), unscaled as libvorbis mdct_backward.
     * @param buffer First n/2 values are the coefficients; receives all n samples.
     */
    public void Inverse(float[] buffer)
    {
        for (int m = 0; m < quarter; m++)
        {
            double a = buffer[2 * m], b = buffer[half - 1 - 2 * m];
            int at = reverse[m];
            re[at] = a * preCos[m] + b * preSin[m];
            im[at] = a * preSin[m] - b * preCos[m];
        }
        for (int size = 2; size <= quarter; size <<= 1)
        {
            int span = size / 2, step = quarter / size;
            for (int start = 0; start < quarter; start += size)
                for (int k = 0; k < span; k++)
                {
                    double wr = fftCos[k * step], wi = fftSin[k * step];
                    int top = start + k, bottom = top + span;
                    double tr = re[bottom] * wr - im[bottom] * wi, ti = re[bottom] * wi + im[bottom] * wr;
                    re[bottom] = re[top] - tr;
                    im[bottom] = im[top] - ti;
                    re[top] += tr;
                    im[top] += ti;
                }
        }
        for (int j = 0; j < quarter; j++)
        {
            dct[2 * j] = re[j] * postCos[j] - im[j] * postSin[j];
            dct[half - 1 - 2 * j] = re[j] * postSin[j] + im[j] * postCos[j];
        }
        int q = half / 2;
        for (int i = 0; i < q; i++) buffer[i] = (float)dct[i + q];
        for (int i = q; i < 3 * q; i++) buffer[i] = (float)-dct[3 * q - 1 - i];
        for (int i = 3 * q; i < n; i++) buffer[i] = (float)-dct[i - 3 * q];
    }
}
