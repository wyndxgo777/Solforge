using System.Buffers.Binary;
using System.Text;
using SunriseStudio.Audio;

/**
 * CRI USM cutscenes and ADX audio (build 86657 type 27 tags) on synthetic bytes, no game install. The ADX golden hash
 * comes from ffmpeg's adpcm_adx decoder run on the same generated stream; on installed cutscenes Solforge's
 * decoder matched ffmpeg sample for sample (mono and stereo tracks).
 */
internal static class UsmChecks
{
    // FNV-1a of ffmpeg's s16le output for Synthetic(): 96 stereo frames, clamped both ways, stopped by the 0x8001 end frame.
    private const uint GoldenHash = 0xB103A5B0;

    /** Runs the checks that use generated fixtures instead of installed game content. */
    internal static void Synthetic(string temporary)
    {
        byte[] adx = Adx();
        var info = AdxDecoder.Inspect(adx);
        Check(info.SampleRate == 48000 && info.Channels == 2, "ADX header facts changed.");
        byte[] pcm = AdxDecoder.Decode(adx);
        Check(pcm.Length == 96 * 2 * 2, $"ADX end frame was not honoured ({pcm.Length} bytes).");
        Check(Fnv(pcm) == GoldenHash, $"ADX decode differs from ffmpeg (hash 0x{Fnv(pcm):X8}).");
        Check(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(8)) == short.MaxValue, "ADX samples were not clamped.");
        var (c1, c2) = AdxDecoder.Coefficients(500, 48000);
        Check(c1 > 7000 && c1 < 8192 && c2 < 0 && c2 > -4096, "ADX predictor coefficients are out of range.");
        var encrypted = adx.ToArray(); encrypted[0x13] = 9;
        Reject(() => AdxDecoder.Decode(encrypted));
        var ahx = adx.ToArray(); ahx[4] = 0x10;
        Reject(() => AdxDecoder.Decode(ahx));

        byte[] video = [0, 0, 1, 0xB3, 0x78, 0x04, 0x38, 0x15, .. Enumerable.Range(0, 200).Select(i => (byte)i), 0, 0, 1, 0xB7];
        byte[] subtitle = Encoding.ASCII.GetBytes("line one");
        var usm = new List<byte>();
        usm.AddRange(Chunk("CRID", 1, 0, Table("CRIUSF_DIR_STREAM", [("filename", "cutscene_1080p.usm"), ("filesize", 0u)])));
        usm.AddRange(Chunk("@SFV", 1, 0, Table("VIDEO_HDRINFO", [("width", 1920u), ("height", 800u), ("total_frames", 2u),
            ("framerate_n", 30000u), ("framerate_d", 1000u), ("mpeg_codec", 1u)])));
        usm.AddRange(Chunk("@SFA", 1, 1, Table("AUDIO_HDRINFO", [("audio_codec", 2u), ("sampling_rate", 48000u), ("num_channels", 2u), ("total_samples", 96u)])));
        usm.AddRange(Chunk("@SFV", 2, 0, Encoding.ASCII.GetBytes("#HEADER END     ===============\0")));
        usm.AddRange(Chunk("@SFV", 0, 0, video[..100]));
        usm.AddRange(Chunk("@SFA", 0, 1, adx[..0x120], padding: 7));
        usm.AddRange(Chunk("@SFV", 0, 0, video[100..]));
        usm.AddRange(Chunk("@SFA", 0, 1, adx[0x120..0x150]));
        usm.AddRange(Chunk("@SBT", 0, 0, subtitle));
        usm.AddRange(Chunk("@SFA", 0, 1, adx[0x150..]));
        usm.AddRange(Chunk("@SFV", 2, 0, Encoding.ASCII.GetBytes("#CONTENTS END   ===============\0")));
        byte[] file = [.. usm];

        Check(UsmContainer.IsUsm(file) && !UsmContainer.IsUsm(adx), "USM magic check failed.");
        var container = UsmContainer.Inspect(file);
        Check(container.FileName == "cutscene_1080p.usm" && !container.LooksEncrypted, "CRID table was not read.");
        Check(container.Streams.Select(s => (s.Kind, s.Channel)).SequenceEqual([("video", 0), ("audio", 1), ("subtitle", 0)]), "USM streams are wrong.");
        var videoStream = container.Video!;
        Check(videoStream.Codec == "MPEG" && videoStream.DataChunks == 2 && videoStream.DataBytes == video.Length
            && videoStream.Header!.Long("width") == 1920 && videoStream.Header.Long("framerate_n") == 30000, "Video header table was not read.");
        Check(container.Streams[1].Codec == "ADX" && container.Streams[1].Header!.Long("num_channels") == 2, "Audio header table was not read.");
        Check(UsmContainer.Extract(file, "@SFV", 0).SequenceEqual(video), "Video payloads were not joined in order.");
        Check(UsmContainer.Extract(file, "@SFA", 1).SequenceEqual(adx), "Chunk padding leaked into the ADX stream.");
        Reject(() => UsmContainer.Extract(file, "@SFA", 5));
        var truncated = file[..^5];
        Reject(() => UsmContainer.Inspect(truncated));

        string folder = Path.Combine(temporary, "usm");
        var written = UsmExport.Export(file, folder, "cut");
        Check(written.Select(Path.GetFileName).SequenceEqual(["cut.video0.m2v", "cut.audio1.adx", "cut.audio1.wav", "cut.subtitle0.sbt"]),
            "USM export wrote " + string.Join(", ", written.Select(Path.GetFileName)));
        var wave = PcmWave.Read(Path.Combine(folder, "cut.audio1.wav"));
        Check(wave.Channels == 2 && wave.SampleRate == 48000 && wave.Samples.SequenceEqual(pcm), "Exported ADX WAV differs from the decoder.");
        var audioOnly = UsmExport.Export(file, Path.Combine(temporary, "usm-audio"), "cut", channel: 1, includeVideo: false);
        Check(audioOnly.Select(Path.GetFileName).SequenceEqual(["cut.audio1.adx", "cut.audio1.wav"]), "Channel filter exported other streams.");
        Console.WriteLine($"usm streams=3 video=m2v adx=ffmpeg_exact({GoldenHash:X8}) padding=stripped encrypted_adx=rejected");
    }

    /** Stereo ADX (encoding 3, 18-byte blocks, 500 Hz high-pass): three frame sets from an LCG, one big scale, then the end frame. */
    private static byte[] Adx()
    {
        var bytes = new List<byte>(new byte[0x120]);
        var header = new byte[0x14];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0x8000); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x11C);
        header[4] = 3; header[5] = 18; header[6] = 4; header[7] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 48000); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 0x200);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), 500); header[18] = 4;
        for (int i = 0; i < header.Length; i++) bytes[i] = header[i];
        for (int i = 0; i < 6; i++) bytes[0x11A + i] = "(c)CRI"u8[i];
        uint x = 12345;
        uint Next() => x = (x * 1103515245u + 12345u) & 0x7FFFFFFF;
        for (int frame = 0; frame < 3; frame++)
            for (int channel = 0; channel < 2; channel++)
            {
                uint random = Next(), scale = frame == 1 && channel == 0 ? 0x4000u : 1 + (random >> 8) % 3000;
                bytes.Add((byte)(scale >> 8)); bytes.Add((byte)scale);
                for (int i = 0; i < 16; i++) bytes.Add((byte)(Next() >> 16));
            }
        for (int channel = 0; channel < 2; channel++) { bytes.Add(0x80); bytes.Add(0x01); bytes.AddRange(new byte[16]); }
        return [.. bytes];
    }

    /** A minimal @UTF table: one row, every column stored per row, u32 or string values. */
    private static byte[] Table(string name, (string Column, object Value)[] columns)
    {
        var strings = new List<byte>();
        int Text(string text) { int at = strings.Count; strings.AddRange(Encoding.ASCII.GetBytes(text)); strings.Add(0); return at; }
        int tableName = Text(name);
        var columnBytes = new List<byte>();
        var row = new List<byte>();
        foreach (var (column, value) in columns)
        {
            columnBytes.Add((byte)(0x50 | (value is string ? 0xA : 4)));
            columnBytes.AddRange(BigEndian((uint)Text(column)));
            row.AddRange(BigEndian(value is string text ? (uint)Text(text) : (uint)value));
        }
        int rowsOffset = 24 + columnBytes.Count, stringsOffset = rowsOffset + row.Count, size = stringsOffset + strings.Count;
        var table = new List<byte>();
        table.AddRange([0, 0, (byte)(rowsOffset >> 8), (byte)rowsOffset]);
        table.AddRange(BigEndian((uint)stringsOffset)); table.AddRange(BigEndian((uint)size)); table.AddRange(BigEndian((uint)tableName));
        table.AddRange([(byte)(columns.Length >> 8), (byte)columns.Length, (byte)(row.Count >> 8), (byte)row.Count]);
        table.AddRange(BigEndian(1));
        table.AddRange(columnBytes); table.AddRange(row); table.AddRange(strings);
        return [.. "@UTF"u8, .. BigEndian((uint)table.Count), .. table];
    }

    private static byte[] Chunk(string signature, int kind, int channel, byte[] payload, int padding = 0)
    {
        var chunk = new byte[0x20 + payload.Length + padding];
        Encoding.ASCII.GetBytes(signature).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), (uint)(chunk.Length - 8));
        chunk[9] = 0x18;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(10), (ushort)padding);
        chunk[12] = (byte)channel; chunk[15] = (byte)kind;
        payload.CopyTo(chunk, 0x20);
        for (int i = 0; i < padding; i++) chunk[0x20 + payload.Length + i] = 0xEE;
        return chunk;
    }

    private static byte[] BigEndian(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }

    private static uint Fnv(byte[] bytes)
    {
        uint hash = 0x811C9DC5;
        foreach (byte b in bytes) hash = (hash ^ b) * 0x01000193;
        return hash;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or AudioUnsupportedException) { return; }
        throw new InvalidOperationException("Invalid media was accepted.");
    }
}
