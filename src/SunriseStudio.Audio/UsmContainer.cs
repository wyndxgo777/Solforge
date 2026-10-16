/**
 * File: CRI USM movie container: lists the CRID chunks and streams and extracts a stream payload. UsmExport.cs writes
 * the streams to files; AdxDecoder.cs decodes the audio.
 */
using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** One elementary stream of a USM: "@SFV" video, "@SFA" audio, "@ALP" alpha or "@SBT" subtitles, by channel. */
public sealed record UsmStream(string Signature, int Channel)
{
    public string Kind => Signature switch { "@SFV" => "video", "@SFA" => "audio", "@ALP" => "alpha", "@SBT" => "subtitle", _ => "other" };
    /** The stream's header table (VIDEO_HDRINFO, AUDIO_HDRINFO, ...), when it has one. */
    public CriUtfTable? Header { get; set; }
    public int DataChunks { get; set; }
    public long DataBytes { get; set; }
    /** The first bytes of the first data chunk; tells the codec (MPEG start code, ADX 0x8000). */
    public byte[] FirstBytes { get; set; } = [];

    /** Video: MPEG-1/2 elementary stream (USM "mpeg_codec" 1) or H.264 (5) / VP9 (9); audio: ADX (2) or HCA (4). */
    public string Codec => Kind switch
    {
        "video" => Header?.Long("mpeg_codec", -1) switch { 1 => "MPEG", 5 => "H.264", 9 => "VP9", var c => $"codec {c}" },
        "audio" => Header?.Long("audio_codec", -1) switch { 2 => "ADX", 4 => "HCA", var c => $"codec {c}" },
        _ => Kind
    };
}

/** USM container facts: the CRID directory and every stream. */
public sealed record UsmInfo(string FileName, IReadOnlyList<UsmStream> Streams, bool LooksEncrypted)
{
    public UsmStream? Video => Streams.FirstOrDefault(stream => stream.Kind == "video");
}

/**
 * CRI Sofdec2 USM (build 86657 cutscenes: tag type 27, "CRID" magic). Written from the public description of the
 * container: chunks are {char[4] signature, u32 BE size, u8, u8 payload offset, u16 BE padding, u8 channel, u8, u8,
 * u8 kind (low 2 bits: 0 data, 1 header, 2 section end, 3 seek), u32 frame time, u32 frame rate}; the payload runs
 * from byte 8 + payload offset to 8 + size - padding. Header payloads are @UTF tables. Read-only: no re-muxing.
 * Build 86657 ships MPEG video (unencrypted elementary stream) with ADX audio (track 0 the 5.1 mix, others mono).
 */
public static class UsmContainer
{
    /** Whether the bytes start with the "CRID" signature. */
    public static bool IsUsm(ReadOnlySpan<byte> bytes) => bytes.Length >= 32 && bytes[..4].SequenceEqual("CRID"u8);

    /** Reads every chunk header and the stream header tables; no payload is copied. */
    public static UsmInfo Inspect(ReadOnlySpan<byte> usm)
    {
        if (!IsUsm(usm)) throw new InvalidDataException("Not a CRI USM (no CRID chunk).");
        var streams = new Dictionary<(string, int), UsmStream>();
        string fileName = "";
        foreach (var chunk in Chunks(usm))
        {
            var payload = usm.Slice(chunk.Payload, chunk.Length);
            if (chunk.Signature == "CRID")
            {
                if (chunk.Kind == 1 && CriUtfTable.IsTable(payload)) fileName = CriUtfTable.Parse(payload).String("filename") ?? "";
                continue;
            }
            var key = (chunk.Signature, chunk.Channel);
            if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = new UsmStream(chunk.Signature, chunk.Channel);
            if (chunk.Kind == 1 && stream.Header is null && CriUtfTable.IsTable(payload)) stream.Header = CriUtfTable.Parse(payload);
            else if (chunk.Kind == 0)
            {
                if (stream.DataChunks == 0) stream.FirstBytes = payload[..Math.Min(64, payload.Length)].ToArray();
                stream.DataChunks++;
                stream.DataBytes += chunk.Length;
            }
        }
        var ordered = streams.Values.OrderBy(stream => stream.Signature switch { "@SFV" => 0, "@SFA" => 1, "@ALP" => 2, _ => 3 })
            .ThenBy(stream => stream.Channel).ToList();
        // Encrypted USM video keeps its first 0x40 bytes and masks the rest; an MPEG or ADX start then tells nothing,
        // so encryption shows as a data stream with no recognisable start at all.
        bool encrypted = ordered.Any(stream => stream.DataChunks > 0 && stream.Kind is "video" or "audio" && !KnownStart(stream));
        return new UsmInfo(fileName, ordered, encrypted);
    }

    /** Concatenated data payloads of one stream: the elementary stream (MPEG video, an ADX file, subtitle records). */
    public static void Extract(ReadOnlySpan<byte> usm, string signature, int channel, Stream output, CancellationToken token = default)
    {
        int written = 0;
        foreach (var chunk in Chunks(usm))
        {
            if (chunk.Kind != 0 || chunk.Signature != signature || chunk.Channel != channel) continue;
            if ((++written & 255) == 0) token.ThrowIfCancellationRequested();
            output.Write(usm.Slice(chunk.Payload, chunk.Length));
        }
        if (written == 0) throw new InvalidDataException($"USM has no {signature} channel {channel} data.");
    }

    /** Returns one stream payload (by signature and channel) as a byte array. */
    public static byte[] Extract(ReadOnlySpan<byte> usm, string signature, int channel)
    {
        using var memory = new MemoryStream();
        Extract(usm, signature, channel, memory);
        return memory.ToArray();
    }

    /** File extension for a stream's extracted bytes. */
    public static string Extension(UsmStream stream) => stream.Kind switch
    {
        "video" when stream.Codec == "H.264" => ".h264",
        "video" when stream.Codec == "VP9" => ".ivf",
        "video" => ".m2v",
        "audio" when stream.Codec == "HCA" => ".hca",
        "audio" => ".adx",
        "subtitle" => ".sbt",
        _ => ".bin"
    };

    /** Whether the first bytes of a stream look like valid data for its codec (false suggests an encrypted USM). */
    private static bool KnownStart(UsmStream stream)
    {
        var b = stream.FirstBytes;
        if (b.Length < 4) return true;
        return stream.Kind == "video"
            ? b[0] == 0 && b[1] == 0 && (b[2] == 1 || b[2] == 0 && b[3] == 1) || b.AsSpan().StartsWith("DKIF"u8)
            : b[0] == 0x80 && b[1] == 0 || b.AsSpan().StartsWith("HCA\0"u8) || (b[0] & 0x7F) == 'H' && (b[1] & 0x7F) == 'C';
    }

    /** One chunk located in the file: signature, kind, channel, payload offset and length. */
    private readonly record struct Chunk(string Signature, int Kind, int Channel, int Payload, int Length);

    /** Walks the file and lists every chunk header without copying payloads. */
    private static List<Chunk> Chunks(ReadOnlySpan<byte> usm)
    {
        var list = new List<Chunk>();
        long position = 0;
        while (position + 0x20 <= usm.Length)
        {
            int p = (int)position;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(usm[(p + 4)..]);
            if (size < 0x18 || position + 8 + size > usm.Length) throw new InvalidDataException($"USM chunk at 0x{p:X} leaves the file.");
            int payloadOffset = usm[p + 9], padding = BinaryPrimitives.ReadUInt16BigEndian(usm[(p + 10)..]);
            int start = p + 8 + payloadOffset, end = checked(p + 8 + (int)size - padding);
            if (payloadOffset < 0x18 || end < start) throw new InvalidDataException($"USM chunk at 0x{p:X} has a bad payload.");
            string signature = System.Text.Encoding.ASCII.GetString(usm.Slice(p, 4));
            list.Add(new Chunk(signature, usm[p + 15] & 3, usm[p + 12], start, end - start));
            position += 8 + size;
        }
        return list;
    }
}
