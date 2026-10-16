using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** Least-significant-bit-first reader, as Vorbis packs its fields. */
internal sealed class VorbisBitReader(byte[] data, int start, int length)
{
    private long bit;
    public long BitPosition => bit;
    public long Remaining => length * 8L - bit;

    /** Reads count bits (LSB first). */
    public uint Read(int count)
    {
        if (count == 0) return 0;
        if (bit + count > (long)length * 8) throw new InvalidDataException("Vorbis data is truncated.");
        uint value = 0;
        for (int i = 0; i < count; i++, bit++)
            value |= (uint)(data[start + (int)(bit >> 3)] >> (int)(bit & 7) & 1) << i;
        return value;
    }
}

/** Least-significant-bit-first writer that grows as needed. */
internal sealed class VorbisBitWriter(int capacity = 256)
{
    private byte[] buffer = new byte[Math.Max(capacity, 16)];
    private long bit;

    /** Appends count bits of value. */
    public void Write(uint value, int count)
    {
        for (int i = 0; i < count;)
        {
            int index = (int)(bit >> 3), shift = (int)(bit & 7), take = Math.Min(8 - shift, count - i);
            if (index >= buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            buffer[index] |= (byte)((value >> i & (1u << take) - 1) << shift);
            bit += take;
            i += take;
        }
    }

    /** Pads the stream to a byte boundary. */
    public void PadToByte() => bit = (bit + 7) & ~7L;

    /** The bytes written so far. */
    public byte[] ToArray() => buffer.AsSpan(0, (int)((bit + 7) >> 3)).ToArray();
}

/**
 * Wwise Vorbis as Wwise 2012 and later write it: setup inside fmt, 2-byte packet sizes,
 * trimmed packet headers and external codebooks. The setup is rebuilt; packets decode as stored.
 */
internal sealed class WwiseVorbisCodec : IWemCodec
{
    private readonly byte[] wem;
    private readonly byte[] setup;
    private readonly int channels, sampleRate, blockSmall, blockLarge, audioStart, streamEnd;
    private readonly uint layout;
    private readonly bool modifiedPackets;

    public AudioStreamInfo Info { get; }

    /** Reads the fmt extension and rebuilds the setup; nothing is decoded yet. */
    public WwiseVorbisCodec(byte[] wem, WemHeader header)
    {
        this.wem = wem;
        channels = header.Channels;
        sampleRate = header.SampleRate;
        layout = header.ChannelLayout;
        if (header.BlockAlign != 0 || header.BitsPerSample != 0 || header.HasVorbChunk || header.ExtraSize != 0x30
            || header.FmtSize < 0x42)
            throw new AudioUnsupportedException(AudioUnsupportedReason.Layout,
                "This Wwise Vorbis layout is not supported (older than Wwise 2012)");
        var extra = wem.AsSpan(header.FmtOffset + 0x18, 0x2A);
        int samples = BinaryPrimitives.ReadInt32LittleEndian(extra);
        uint seekSize = BinaryPrimitives.ReadUInt32LittleEndian(extra[0x10..]);
        uint audioOffset = BinaryPrimitives.ReadUInt32LittleEndian(extra[0x14..]);
        blockSmall = extra[0x28];
        blockLarge = extra[0x29];
        // Equal block sizes leave no mode flags to strip, so those packets are stored whole.
        modifiedPackets = blockSmall != blockLarge;
        streamEnd = header.DataOffset + header.DataSize;
        long setupOffset = header.DataOffset + (long)seekSize;
        if (header.MetaOffset >= 0) setupOffset += BinaryPrimitives.ReadUInt32LittleEndian(wem.AsSpan(header.MetaOffset + 4));
        if (audioOffset > header.DataSize || setupOffset > streamEnd - 2)
            throw new InvalidDataException("Vorbis setup or audio offset leaves the data chunk.");
        audioStart = header.DataOffset + (int)audioOffset;
        int setupSize = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan((int)setupOffset));
        if (setupSize == 0 || setupOffset + 2 + setupSize > wem.Length) throw new InvalidDataException("Vorbis setup is truncated.");
        setup = WwiseVorbisSetup.Rebuild(wem.AsSpan((int)setupOffset + 2, setupSize).ToArray(), channels);
        if (samples < 0) throw new InvalidDataException("Vorbis sample count is negative.");
        Info = new(sampleRate, channels, samples, "Wwise Vorbis");
    }

    /** Checks that the rebuilt setup parses, without decoding audio. */
    public void Validate() => _ = new VorbisDecoder(channels, blockSmall, blockLarge, setup);

    /** Decodes to 16-bit PCM, mixing down to maxChannels. */
    public PcmWave Decode(int maxChannels, CancellationToken token)
    {
        int outChannels = AudioDownmix.Create(channels, layout, maxChannels)?.OutputChannels ?? channels;
        byte[] output = WemDecoder.AllocateOutput(Info.Samples, outChannels);
        Run(maxChannels, token, (samples, written) =>
            WemDecoder.WritePcm16(samples, output.AsSpan((int)(written * outChannels * 2)), 32767f));
        // Samples the stream did not deliver stay silent.
        return new(sampleRate, (ushort)outChannels, 16, output);
    }

    /** Decodes to float samples, mixing down to maxChannels. */
    public AudioSamples DecodeSamples(int maxChannels, CancellationToken token)
    {
        int outChannels = AudioDownmix.Create(channels, layout, maxChannels)?.OutputChannels ?? channels;
        float[] output = WemDecoder.AllocateSamples(Info.Samples, outChannels);
        Run(maxChannels, token, (samples, written) =>
            WemDecoder.WriteSamples(samples, output.AsSpan((int)(written * outChannels)), 32767f));
        return new(sampleRate, outChannels, output);
    }

    private delegate void Sink(ReadOnlySpan<float> samples, long written);

    /** Decodes every packet and hands each block of interleaved, mixed-down frames to the sink. */
    private void Run(int maxChannels, CancellationToken token, Sink sink)
    {
        var downmix = AudioDownmix.Create(channels, layout, maxChannels);
        int outChannels = downmix?.OutputChannels ?? channels;
        long frames = Info.Samples;
        var decoder = new VorbisDecoder(channels, blockSmall, blockLarge, setup);
        var interleaved = new float[(1 << blockLarge) * channels];
        var mixed = new float[(1 << blockLarge) * outChannels];
        long written = 0;
        for (int offset = audioStart; offset < streamEnd && written < frames;)
        {
            token.ThrowIfCancellationRequested();
            if (offset > wem.Length - 2) throw new InvalidDataException("Vorbis packet header is truncated.");
            int size = BinaryPrimitives.ReadUInt16LittleEndian(wem.AsSpan(offset));
            int body = offset + 2;
            if (size == 0 || size > wem.Length - body) throw new InvalidDataException("Vorbis packet is truncated.");
            offset = body + size;
            int got = (int)Math.Min(decoder.Decode(wem.AsSpan(body, size), modifiedPackets), frames - written);
            if (got == 0) continue;
            for (int c = 0; c < channels; c++)
            {
                var source = decoder.Output(c, got);
                for (int i = 0; i < got; i++) interleaved[i * channels + c] = source[i];
            }
            Span<float> samples = interleaved.AsSpan(0, got * channels);
            if (downmix is not null) samples = downmix.Mix(samples, got, mixed);
            sink(samples, written);
            written += got;
        }
    }
}
