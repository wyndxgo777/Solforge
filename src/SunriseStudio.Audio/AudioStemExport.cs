using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/**
 * Exports Wwise media as standard 16-bit PCM WAV stems (Solforge Phase 17). Raw PCM WEMs and
 * compressed (Wwise Vorbis) WEMs both decode through WemDecoder with every channel kept; the full
 * mix is NAME.wav and, for multichannel media, each channel is also NAME.chN.wav (mono, 1-based).
 * PcmWave.Write only takes mono or stereo, so this writer covers any channel count.
 */
public static class AudioStemExport
{
    public const int MaximumChannels = 32;

    /** Decodes one WEM and writes its stems; returns the written paths, mix first. */
    public static IReadOnlyList<string> ExportWem(ReadOnlySpan<byte> wem, string folder, string name,
        bool splitChannels = true, CancellationToken token = default) =>
        ExportWave(WemDecoder.Decode(wem, 0, token), folder, name, splitChannels);

    /** Writes NAME.wav and, when asked and there is more than one channel, NAME.chN.wav per channel. */
    public static IReadOnlyList<string> ExportWave(PcmWave wave, string folder, string name, bool splitChannels = true)
    {
        ArgumentNullException.ThrowIfNull(wave);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Stem names cannot contain path characters.", nameof(name));
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        string mix = Path.Combine(folder, name + ".wav");
        Write(mix, wave);
        written.Add(mix);
        if (splitChannels && wave.Channels > 1)
        {
            var stems = SplitChannels(wave);
            for (int channel = 0; channel < stems.Count; channel++)
            {
                string path = Path.Combine(folder, $"{name}.ch{channel + 1}.wav");
                Write(path, stems[channel]);
                written.Add(path);
            }
        }
        return written;
    }

    /** One mono PCM16 wave per interleaved channel. */
    public static IReadOnlyList<PcmWave> SplitChannels(PcmWave wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        RequirePcm16(wave);
        int channels = wave.Channels, frames = wave.Samples.Length / (channels * 2);
        var result = new PcmWave[channels];
        for (int channel = 0; channel < channels; channel++)
        {
            byte[] mono = new byte[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                int source = (frame * channels + channel) * 2;
                mono[frame * 2] = wave.Samples[source];
                mono[frame * 2 + 1] = wave.Samples[source + 1];
            }
            result[channel] = new(wave.SampleRate, 1, 16, mono);
        }
        return result;
    }

    /** A canonical 44-byte-header PCM16 WAV of any channel count, written through a temporary file. */
    public static void Write(string path, PcmWave wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        RequirePcm16(wave);
        if (wave.SampleRate is < 1 or > 768000) throw new InvalidDataException("Sample rate is invalid.");
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), wave.Samples.Length + 36);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), wave.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), wave.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), wave.SampleRate * wave.Channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(wave.Channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), wave.Samples.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(header);
                stream.Write(wave.Samples);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequirePcm16(PcmWave wave)
    {
        if (wave.BitsPerSample != 16 || wave.Channels is < 1 or > MaximumChannels || wave.Samples.Length % (wave.Channels * 2) != 0
            || wave.Samples.LongLength > int.MaxValue - 44)
            throw new InvalidDataException("Stems are written from PCM16 audio with 1 to 32 channels.");
    }
}
