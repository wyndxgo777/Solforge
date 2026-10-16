using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** Interleaved decoded samples in 16-bit units, not clipped, so gain applies before the mix clips. */
public sealed record AudioSamples(int SampleRate, int Channels, float[] Samples)
{
    public long Frames => Samples.Length / Channels;
    public double DurationSeconds => (double)Frames / SampleRate;

    /** Bytes the same audio takes as PCM16, the unit of the working limits. */
    public long Pcm16Bytes => Samples.LongLength * 2;

    /** Converts 16-bit PCM to float samples. */
    public static AudioSamples FromPcm(PcmWave wave)
    {
        if (wave.BitsPerSample != 16 || wave.Channels < 1 || wave.Samples.Length % wave.BlockAlign != 0)
            throw new NotSupportedException("Timeline sources need valid mono or stereo PCM16.");
        var samples = new float[wave.Samples.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(i * 2));
        return new(wave.SampleRate, wave.Channels, samples);
    }
}
