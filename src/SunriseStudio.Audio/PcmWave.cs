/** File: In-memory PCM WAV with reading and writing. */
using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** Interleaved PCM samples with their format. */
public sealed record PcmWave(int SampleRate, ushort Channels, ushort BitsPerSample, byte[] Samples)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
    public double DurationSeconds => (double)Samples.Length / (SampleRate * BlockAlign);

    /** Writes a 16-bit WAV after checking rate, channel count and size limits. */
    public static void Write(string path, int rate, ushort channels, byte[] samples)
    {
        if (rate is < 8000 or > 192000 || channels is < 1 or > 2 || samples.Length % (channels * 2) != 0
            || samples.Length > 512 * 1024 * 1024 - 44) throw new InvalidDataException("PCM output format is invalid.");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(samples.Length + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write(channels); writer.Write(rate);
        writer.Write(rate * channels * 2); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(samples.Length); writer.Write(samples);
    }

    /** Reads a WAV file. */
    public static PcmWave Read(string path)
    {
        using var stream = File.OpenRead(path);
        var format = ReadFormat(stream);
        byte[] samples = new byte[format.Length]; stream.Position = format.Offset; stream.ReadExactly(samples);
        return new(format.Rate, format.Channels, 16, samples);
    }

    /** Reads only the stream facts from a WAV file. */
    public static AudioStreamInfo ReadInfo(string path)
    {
        using var stream = File.OpenRead(path); var format = ReadFormat(stream);
        return new(format.Rate, format.Channels, format.Length / (format.Channels * 2), "PCM16");
    }

    /** Playback accepts bounded PCM16 chunks; header inspection does not allocate the sample payload. */
    private static (int Rate, ushort Channels, long Offset, int Length) ReadFormat(FileStream stream)
    {
        if (stream.Length is < 12 or > 512L * 1024 * 1024) throw new InvalidDataException("WAV length is invalid.");
        Span<byte> header = stackalloc byte[16]; stream.ReadExactly(header[..12]);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Decoder output is not a RIFF WAVE.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != stream.Length - 8)
            throw new InvalidDataException("WAV size differs from its RIFF header.");
        ushort format = 0, channels = 0, bits = 0, align = 0; int sampleRate = 0; uint byteRate = 0;
        long dataOffset = -1; int dataLength = 0; bool formatSeen = false;
        for (long p = 12; p < stream.Length;)
        {
            if (p > stream.Length - 8) throw new InvalidDataException("WAV chunk header is truncated.");
            stream.Position = p; stream.ReadExactly(header[..8]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]); long start = p + 8;
            if (size > stream.Length - start) throw new InvalidDataException("WAV chunk leaves the file.");
            if (header[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || formatSeen) throw new InvalidDataException("WAV format is truncated or duplicated.");
                formatSeen = true; stream.ReadExactly(header);
                format = BinaryPrimitives.ReadUInt16LittleEndian(header);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                if (dataOffset >= 0) throw new InvalidDataException("WAV data is duplicated.");
                dataOffset = start; dataLength = checked((int)size);
            }
            p = checked(start + size + (size & 1));
            if (p > stream.Length) throw new InvalidDataException("WAV chunk padding is truncated.");
        }
        if (format != 1 || channels is < 1 or > 2 || bits != 16 || sampleRate is < 8000 or > 192000
            || align != channels * 2 || byteRate != sampleRate * align
            || dataOffset < 0 || dataLength % (channels * 2) != 0)
            throw new InvalidDataException("Playback needs mono or stereo PCM16 audio.");
        return (sampleRate, channels, dataOffset, dataLength);
    }
}
