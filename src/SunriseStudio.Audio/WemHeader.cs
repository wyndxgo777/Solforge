/**
 * File: Parses the RIFF/WEM header of Wwise media and defines the exception for media Studio cannot decode.
 */
using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** Why a media file cannot be decoded. */
public enum AudioUnsupportedReason { EmptyPlaceholder, NotPlayableMedia, BigEndian, Codec, Layout }

/** Media Studio cannot decode; Message is the text the UI shows. InvalidDataException is sealed. */
public sealed class AudioUnsupportedException(AudioUnsupportedReason reason, string message)
    : NotSupportedException(message)
{
    public AudioUnsupportedReason Reason => reason;
}

/** One RIFF WEM: the fmt fields and the data chunk. */
public sealed class WemHeader
{
    public const ushort PcmTag = 0x0001, PcmExtensibleTag = 0xFFFE, VorbisTag = 0xFFFF;

    public required ushort FormatTag { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required int BlockAlign { get; init; }
    public required int BitsPerSample { get; init; }
    public required int FmtOffset { get; init; }
    public required int FmtSize { get; init; }
    public required int ExtraSize { get; init; }
    /** WAVEFORMATEXTENSIBLE speaker mask; 0 when the file has none. */
    public required uint ChannelLayout { get; init; }
    public required int DataOffset { get; init; }
    public required int DataSize { get; init; }
    /** Offset of the "meta" chunk body, or -1. */
    public required int MetaOffset { get; init; }
    public required bool HasVorbChunk { get; init; }

    /**
     * Reads the chunk list. Wwise chunks are not padded to even sizes.
     * @param wem Whole WEM file.
     * @return The header; unsupported codecs throw AudioUnsupportedException.
     */
    public static WemHeader Parse(ReadOnlySpan<byte> wem)
    {
        if (wem.Length <= 1)
            throw new AudioUnsupportedException(AudioUnsupportedReason.EmptyPlaceholder, "Empty placeholder stream");
        if (wem.Length >= 4 && wem[..4].SequenceEqual("RIFX"u8))
            throw new AudioUnsupportedException(AudioUnsupportedReason.BigEndian, "Big-endian (RIFX) media is not supported");
        if (wem.Length < 12 || !wem[..4].SequenceEqual("RIFF"u8) || !wem.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new AudioUnsupportedException(AudioUnsupportedReason.NotPlayableMedia, "Not playable media");

        int fmt = -1, fmtSize = 0, data = -1, dataSize = 0, meta = -1;
        bool vorb = false;
        for (int p = 12; p <= wem.Length - 8;)
        {
            uint type = BinaryPrimitives.ReadUInt32BigEndian(wem[p..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wem[(p + 4)..]);
            if (type == 0) break;
            int body = p + 8;
            if (size > (uint)(wem.Length - body)) throw new InvalidDataException("WEM chunk leaves the file.");
            switch (type)
            {
                case 0x666D7420: fmt = body; fmtSize = (int)size; break;   // "fmt "
                case 0x64617461: data = body; dataSize = (int)size; break;  // "data"
                case 0x6D657461: meta = body; break;                         // "meta"
                case 0x766F7262: vorb = true; break;                         // "vorb"
            }
            p = body + (int)size;
        }
        if (fmt < 0 || fmtSize < 16) throw new InvalidDataException("WEM has no format chunk.");
        if (data < 0) throw new InvalidDataException("WEM has no data chunk.");

        var f = wem.Slice(fmt, fmtSize);
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        int extra = fmtSize >= 18 && tag is not (0x0165 or 0x0166)
            ? BinaryPrimitives.ReadUInt16LittleEndian(f[16..]) : 0;
        uint layout = 0;
        if (extra >= 6 && fmtSize >= 24)
        {
            layout = BinaryPrimitives.ReadUInt32LittleEndian(f[20..]);
            // Newer Wwise packs channel count, config type and mask; the mask starts at bit 12.
            if ((layout & 0xFF) == channels) layout >>= 12;
        }
        var header = new WemHeader
        {
            FormatTag = tag, Channels = channels,
            SampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(f[4..]), int.MaxValue),
            BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]),
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]),
            FmtOffset = fmt, FmtSize = fmtSize, ExtraSize = extra, ChannelLayout = layout,
            DataOffset = data, DataSize = dataSize, MetaOffset = meta, HasVorbChunk = vorb
        };
        if (tag is not (PcmTag or PcmExtensibleTag or VorbisTag))
            throw new AudioUnsupportedException(AudioUnsupportedReason.Codec, CodecName(tag) is { } name
                ? name + " audio is not supported" : $"Codec 0x{tag:X4} is not supported");
        if (channels < 1 || header.SampleRate < 1) throw new InvalidDataException("WEM format has no channels or rate.");
        return header;
    }

    /** Wwise codec names by format tag, for the unsupported message. */
    private static string? CodecName(ushort tag) => tag switch
    {
        0x0002 or 0x0069 => "IMA ADPCM",
        0x0161 or 0x0162 => "xWMA",
        0x0165 or 0x0166 => "XMA2",
        0xAAC0 => "AAC",
        0xFFF0 => "DSP ADPCM",
        0xFFFB => "HEVAG",
        0xFFFC => "ATRAC9",
        0x3039 or 0x3040 or 0x3041 => "Opus",
        0x8311 => "PTADPCM",
        _ => null
    };
}
