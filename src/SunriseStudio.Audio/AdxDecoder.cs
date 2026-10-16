/**
 * File: CRI ADX audio: header parsing and decoding to PCM/WAV. Used by UsmContainer/UsmExport for movie audio and by
 * the audio tools in Solforge.Mcp (SolforgeTools.Media.cs).
 */
using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** The fixed header of a CRI ADX stream (big-endian). */
public sealed record AdxHeader(int DataOffset, int Encoding, int BlockSize, int BitDepth, int Channels, int SampleRate,
    long TotalSamples, int HighpassFrequency, int Version, int Flags)
{
    /** Whether the bytes start with the ADX signature (0x80 0x00). */
    public static bool IsAdx(ReadOnlySpan<byte> bytes) => bytes.Length >= 0x14 && bytes[0] == 0x80 && bytes[1] == 0x00;

    /** Reads the big-endian ADX header; throws InvalidDataException when the data is not ADX. */
    public static AdxHeader Parse(ReadOnlySpan<byte> adx)
    {
        if (!IsAdx(adx)) throw new InvalidDataException("Not a CRI ADX stream.");
        int copyright = BinaryPrimitives.ReadUInt16BigEndian(adx[2..]);
        var header = new AdxHeader(copyright + 4, adx[4], adx[5], adx[6], adx[7], checked((int)BinaryPrimitives.ReadUInt32BigEndian(adx[8..])),
            BinaryPrimitives.ReadUInt32BigEndian(adx[12..]), BinaryPrimitives.ReadUInt16BigEndian(adx[16..]), adx[18], adx[19]);
        if (header.DataOffset > adx.Length || header.Channels is < 1 or > 16 || header.SampleRate is < 1000 or > 192000)
            throw new InvalidDataException("ADX header is invalid.");
        return header;
    }
}

/**
 * Decodes standard CRI ADX (encoding 3: 4-bit ADPCM, fixed 2-tap predictor from the high-pass frequency) to
 * interleaved PCM16. Written from the public format description (Wikipedia "ADX (file format)"): each channel's
 * frame is a u16 BE scale and (block - 2) bytes of signed nibbles, high nibble first; frames of all channels
 * interleave; sample = nibble * scale + (c1 * h1 + c2 * h2) >> 12. A frame whose scale has bit 15 set ends the
 * stream. Encrypted (flags 8/9), exponential-scale (4) and AHX (0x10/0x11) streams are refused.
 */
public static class AdxDecoder
{
    /** Header facts (rate, channels, length) without decoding any samples. */
    public static AudioStreamInfo Inspect(ReadOnlySpan<byte> adx)
    {
        var header = AdxHeader.Parse(adx);
        return new AudioStreamInfo(header.SampleRate, header.Channels, header.TotalSamples, "CRI ADX");
    }

    /** Fixed-point (x4096) predictor coefficients for the high-pass frequency. */
    public static (int C1, int C2) Coefficients(int highpass, int sampleRate)
    {
        double z = Math.Cos(2 * Math.PI * highpass / sampleRate);
        double a = Math.Sqrt(2) - z, b = Math.Sqrt(2) - 1;
        double c = (a - Math.Sqrt((a + b) * (a - b))) / b;
        return ((int)Math.Floor(c * 2 * 4096), (int)Math.Floor(-(c * c) * 4096));
    }

    /** Interleaved little-endian PCM16 for every channel. */
    public static byte[] Decode(ReadOnlySpan<byte> adx, CancellationToken token = default)
    {
        var header = AdxHeader.Parse(adx);
        if (header.Flags is 8 or 9) throw new AudioUnsupportedException(AudioUnsupportedReason.Codec, "Encrypted ADX is not supported.");
        if (header.Encoding != 3 || header.BitDepth != 4 || header.BlockSize < 3)
            throw new AudioUnsupportedException(AudioUnsupportedReason.Codec,
                $"ADX encoding {header.Encoding} ({header.BitDepth}-bit, block {header.BlockSize}) is not supported; only standard ADX (3).");
        int channels = header.Channels, block = header.BlockSize, perFrame = (block - 2) * 2;
        long frameSet = (long)block * channels;
        long available = (adx.Length - header.DataOffset) / frameSet * perFrame;
        long total = Math.Min(header.TotalSamples > 0 ? header.TotalSamples : available, available);
        if (total * channels * 2 > 1L << 30) throw new InvalidDataException("ADX stream is too long to decode at once.");
        var (c1, c2) = Coefficients(header.HighpassFrequency, header.SampleRate);
        var output = new byte[total * channels * 2];
        var h1 = new int[channels]; var h2 = new int[channels];
        long written = 0;
        for (int frame = 0; written < total; frame++)
        {
            if ((frame & 1023) == 0) token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(perFrame, total - written);
            for (int ch = 0; ch < channels; ch++)
            {
                int at = checked(header.DataOffset + (int)(frame * frameSet) + ch * block);
                int scale = BinaryPrimitives.ReadUInt16BigEndian(adx[at..]);
                if ((scale & 0x8000) != 0) { total = written; break; }
                for (int s = 0; s < count; s++)
                {
                    int value = adx[at + 2 + s / 2];
                    int nibble = (s & 1) == 0 ? value >> 4 : value & 0xF;
                    if (nibble >= 8) nibble -= 16;
                    int sample = Math.Clamp(nibble * scale + ((c1 * h1[ch] + c2 * h2[ch]) >> 12), short.MinValue, short.MaxValue);
                    h2[ch] = h1[ch]; h1[ch] = sample;
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((int)(((written + s) * channels + ch) * 2)), (short)sample);
                }
            }
            written = Math.Min(total, written + count);
        }
        return written * channels * 2 == output.Length ? output : output[..(int)(written * channels * 2)];
    }
}
