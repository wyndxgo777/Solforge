using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** One WEM codec: stream facts up front, PCM16 on demand. */
internal interface IWemCodec
{
    AudioStreamInfo Info { get; }

    /**
     * Decodes the whole stream.
     * @param maxChannels 0 keeps every channel; 2 mixes down when there are more.
     * @param token Checked between packets.
     */
    PcmWave Decode(int maxChannels, CancellationToken token);

    /** Decodes the whole stream like Decode, without clamping to 16 bits. */
    AudioSamples DecodeSamples(int maxChannels, CancellationToken token);
}

/** Turns WEM bytes into PCM16 without any external program. */
public static class WemDecoder
{
    /** Decoded audio stays under the 512 MiB preview limit, WAV header included. */
    public const long OutputLimit = 512L * 1024 * 1024;

    /** Reads the stream facts; a Vorbis setup is rebuilt and checked, no audio is decoded. */
    public static AudioStreamInfo Inspect(ReadOnlySpan<byte> wem)
    {
        var codec = Open(wem);
        if (codec is WwiseVorbisCodec vorbis) Guard(vorbis.Validate);
        return codec.Info;
    }

    /**
     * Decodes a WEM to interleaved PCM16.
     * @param wem Whole WEM file.
     * @param maxChannels 0 keeps every channel; 2 mixes down when there are more.
     * @param token Checked between packets.
     * @return Exactly the stream's sample count; unsupported media throws AudioUnsupportedException.
     */
    public static PcmWave Decode(ReadOnlySpan<byte> wem, int maxChannels, CancellationToken token)
    {
        if (maxChannels is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(maxChannels), "Use 0 or 2.");
        var codec = Open(wem);
        PcmWave result = null!;
        Guard(() => result = codec.Decode(maxChannels, token));
        return result;
    }

    /**
     * Decodes a WEM for mixing: the same values as Decode, but samples past full scale are kept.
     * @param wem Whole WEM file.
     * @param maxChannels 0 keeps every channel; 2 mixes down when there are more.
     * @param token Checked between packets.
     */
    public static AudioSamples DecodeSamples(ReadOnlySpan<byte> wem, int maxChannels, CancellationToken token)
    {
        if (maxChannels is not (0 or 2)) throw new ArgumentOutOfRangeException(nameof(maxChannels), "Use 0 or 2.");
        var codec = Open(wem);
        AudioSamples result = null!;
        Guard(() => result = codec.DecodeSamples(maxChannels, token));
        return result;
    }

    /** Picks the codec for a WEM: PCM for raw data, Wwise Vorbis otherwise. */
    internal static IWemCodec Open(ReadOnlySpan<byte> wem)
    {
        var header = WemHeader.Parse(wem);
        if (header.FormatTag != WemHeader.VorbisTag)
            return new PcmWemCodec(wem.Slice(header.DataOffset, header.DataSize).ToArray(), header);
        byte[] copy = wem.ToArray();
        IWemCodec? codec = null;
        Guard(() => codec = new WwiseVorbisCodec(copy, header));
        return codec!;
    }

    /** Allocates a 16-bit output buffer, rejecting sizes above the preview limit. */
    internal static byte[] AllocateOutput(long frames, int channels)
    {
        long bytes = frames * channels * 2;
        if (bytes > OutputLimit - 44) throw new InvalidDataException("Decoded audio exceeds the 512 MiB preview limit.");
        return new byte[bytes];
    }

    /** The limit counts PCM16 bytes, so both decode paths accept the same streams. */
    internal static float[] AllocateSamples(long frames, int channels)
    {
        if (frames * channels * 2 > OutputLimit - 44) throw new InvalidDataException("Decoded audio exceeds the 512 MiB preview limit.");
        return new float[frames * channels];
    }

    /**
     * Scales and truncates toward zero, as WritePcm16 does, but does not clamp.
     * @param scale 32767 for normalised float, 1 for float that already holds PCM16 values.
     */
    internal static void WriteSamples(ReadOnlySpan<float> samples, Span<float> output, float scale)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float value = samples[i] * scale;
            output[i] = float.IsNaN(value) ? 0 : MathF.Truncate(value);
        }
    }

    /**
     * Scales, truncates toward zero and clamps, as vgmstream converts float to PCM16.
     * @param scale 32767 for normalised float, 1 for float that already holds PCM16 values.
     */
    internal static void WritePcm16(ReadOnlySpan<float> samples, Span<byte> output, float scale)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float value = samples[i] * scale;
            int sample = value >= 32767f ? 32767 : value <= -32768f ? -32768 : float.IsNaN(value) ? 0 : (int)value;
            BinaryPrimitives.WriteInt16LittleEndian(output[(i * 2)..], (short)sample);
        }
    }

    /** Decoder faults on bad input surface as InvalidDataException; cancellation passes through. */
    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is not (InvalidDataException or AudioUnsupportedException
            or OperationCanceledException or OutOfMemoryException))
        {
            throw new InvalidDataException("Audio stream is damaged: " + error.Message, error);
        }
    }
}

/** 16-bit little-endian PCM, as Studio writes for weapon sounds. */
internal sealed class PcmWemCodec : IWemCodec
{
    private readonly byte[] data;
    private readonly int channels, sampleRate;
    private readonly uint layout;

    public AudioStreamInfo Info { get; }

    public PcmWemCodec(byte[] data, WemHeader header)
    {
        if (header.BitsPerSample != 16)
            throw new AudioUnsupportedException(AudioUnsupportedReason.Layout,
                $"{header.BitsPerSample}-bit PCM is not supported");
        if (header.FmtSize is not (0x10 or 0x12 or 0x18 or 0x28))
            throw new AudioUnsupportedException(AudioUnsupportedReason.Layout, "This PCM layout is not supported");
        this.data = data;
        channels = header.Channels;
        sampleRate = header.SampleRate;
        layout = header.ChannelLayout;
        Info = new(sampleRate, channels, data.Length / (channels * 2), "PCM 16-bit");
    }

    /** Decodes to 16-bit PCM, mixing down to maxChannels. */
    public PcmWave Decode(int maxChannels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long frames = Info.Samples;
        var downmix = AudioDownmix.Create(channels, layout, maxChannels);
        if (downmix is null)
        {
            byte[] copy = WemDecoder.AllocateOutput(frames, channels);
            data.AsSpan(0, copy.Length).CopyTo(copy);
            return new(sampleRate, (ushort)channels, 16, copy);
        }
        // vgmstream mixes PCM16 as float holding the integer values, then truncates.
        byte[] output = WemDecoder.AllocateOutput(frames, downmix.OutputChannels);
        const int chunk = 4096;
        float[] input = new float[chunk * channels], mixed = new float[chunk * downmix.OutputChannels];
        for (long start = 0; start < frames; start += chunk)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(chunk, frames - start);
            var source = data.AsSpan((int)(start * channels * 2), count * channels * 2);
            for (int i = 0; i < count * channels; i++) input[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]);
            var result = downmix.Mix(input.AsSpan(0, count * channels), count, mixed);
            WemDecoder.WritePcm16(result, output.AsSpan((int)(start * downmix.OutputChannels * 2)), 1f);
        }
        return new(sampleRate, (ushort)downmix.OutputChannels, 16, output);
    }

    /** Decodes to float samples, mixing down to maxChannels. */
    public AudioSamples DecodeSamples(int maxChannels, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long frames = Info.Samples;
        var downmix = AudioDownmix.Create(channels, layout, maxChannels);
        int outChannels = downmix?.OutputChannels ?? channels;
        float[] output = WemDecoder.AllocateSamples(frames, outChannels);
        const int chunk = 4096;
        float[] input = new float[chunk * channels], mixed = new float[chunk * outChannels];
        for (long start = 0; start < frames; start += chunk)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(chunk, frames - start);
            var source = data.AsSpan((int)(start * channels * 2), count * channels * 2);
            for (int i = 0; i < count * channels; i++) input[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]);
            Span<float> result = input.AsSpan(0, count * channels);
            if (downmix is not null) result = downmix.Mix(result, count, mixed);
            WemDecoder.WriteSamples(result, output.AsSpan((int)(start * outChannels)), 1f);
        }
        return new(sampleRate, outChannels, output);
    }
}
