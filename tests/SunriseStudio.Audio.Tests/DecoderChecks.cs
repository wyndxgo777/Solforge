using System.Buffers.Binary;
using SunriseStudio.Audio;

/** The managed WEM decoder: PCM round trips, downmix values and refusals, with no game data. */
internal static class DecoderChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        foreach (ushort channels in new ushort[] { 1, 2 })
        {
            var wave = Wave(channels, 4800);
            byte[] wem = WwiseSoundBank.PcmWem(wave);
            var info = WemDecoder.Inspect(wem);
            Check(info.SampleRate == 48000 && info.Channels == channels && info.Samples == 4800, "PCM inspect is wrong.");
            foreach (int max in new[] { 0, 2 })
            {
                var decoded = WemDecoder.Decode(wem, max, CancellationToken.None);
                Check(decoded.Channels == channels && decoded.Samples.SequenceEqual(wave.Samples), "PCM did not round-trip.");
            }
        }

        // 5.1 to stereo: centre and backs at -3 dB, LFE dropped.
        short[] frames = [1000, -2000, 3000, 500, -400, 800, -1000, 0, -1, 0, 0, 0, 30000, 0, 30000, 0, 0, 0];
        byte[] surround = Extensible(6, 0x3F, Samples(frames));
        var stereo = WemDecoder.Decode(surround, 2, CancellationToken.None);
        Check(stereo.Channels == 2 && Values(stereo).SequenceEqual(new short[] { 2838, 687, -1000, 0, 32767, 21213 }),
            "5.1 downmix values are wrong.");
        var all = WemDecoder.Decode(surround, 0, CancellationToken.None);
        Check(all.Channels == 6 && Values(all).SequenceEqual(frames), "Six channels did not round-trip.");

        // Back centre joins both sides at -3 dB, as the game's stereo table does.
        var backCentre = WemDecoder.Decode(Extensible(4, 0x107, Samples(1000, 2000, 3000, 4000)), 2, CancellationToken.None);
        Check(Values(backCentre).SequenceEqual(new short[] { 5949, 6949 }), "Back centre downmix values are wrong.");

        // No speaker mask: channel i joins output i % 2 at equal power.
        var folded = WemDecoder.Decode(Extensible(3, 0, Samples(1000, 2000, 3000)), 2, CancellationToken.None);
        Check(Values(folded).SequenceEqual(new short[] { 2828, 2000 }), "Maskless downmix values are wrong.");

        Refuse([0], AudioUnsupportedReason.EmptyPlaceholder, "Empty placeholder stream");
        Refuse([], AudioUnsupportedReason.EmptyPlaceholder, "Empty placeholder stream");
        Refuse([0, 0, 0, 0, 0, 4, 0, 0, 0x80, 0xBB], AudioUnsupportedReason.NotPlayableMedia, "Not playable media");
        Refuse(Retag(WwiseSoundBank.PcmWem(Wave(1, 10)), 0x1234), AudioUnsupportedReason.Codec, "Codec 0x1234 is not supported");
        Refuse(Retag(WwiseSoundBank.PcmWem(Wave(1, 10)), 0x3040), AudioUnsupportedReason.Codec, "Opus audio is not supported");

        // Damaged Vorbis never escapes as anything but InvalidDataException.
        var random = new Random(1234);
        byte[] noise = new byte[64];
        random.NextBytes(noise);
        Damaged(Vorbis(noise, [], 4096));
        Damaged(Vorbis(Setup, [], 4096)[..^20]);
        Check(WemDecoder.Inspect(Vorbis(Setup, [], 4096)).Samples == 4096, "The test setup was refused.");
        int reached = 0;
        for (int i = 0; i < 300; i++)
        {
            byte[] fuzz = Vorbis(Setup, Packets(random), 4096);
            if (Damaged(fuzz, allowSuccess: true)) reached++;
            Damaged(fuzz[..random.Next(12, fuzz.Length)], allowSuccess: true);
        }
        Check(reached > 0, "No random packet stream reached the decoder.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool stopped = false;
        try { WemDecoder.Decode(surround, 2, canceled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "Canceled decode ran.");
        Console.WriteLine($"audio decoder=managed pcm=bit_exact downmix=checked refusals=typed vorbis_fuzz_decoded={reached}/300");
    }

    /**
     * Valid Wwise setup for two channels: library books 9, 21, 107, 110 and 120, one floor,
     * one type-1 residue, one coupled mapping, a short and a long mode.
     */
    private static readonly byte[] Setup = Convert.FromHexString(
        "040954B0861B78000300524000400A202000030110E0400000000002000F00008300C4C8C0000101208000000004000200");

    /** One to forty random audio packets, each with its 2-byte size. */
    private static byte[] Packets(Random random)
    {
        using var stream = new MemoryStream();
        for (int count = random.Next(1, 41); count > 0; count--)
        {
            byte[] packet = new byte[random.Next(1, 200)];
            random.NextBytes(packet);
            stream.Write(BitConverter.GetBytes((ushort)packet.Length));
            stream.Write(packet);
        }
        return stream.ToArray();
    }

    private static PcmWave Wave(ushort channels, int frames)
    {
        byte[] bytes = new byte[frames * channels * 2];
        for (int i = 0; i < bytes.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(i * 37 % 65536 - 32768));
        return new(48000, channels, 16, bytes);
    }

    private static byte[] Samples(params short[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static short[] Values(PcmWave wave)
    {
        var values = new short[wave.Samples.Length / 2];
        for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(i * 2));
        return values;
    }

    /** Extensible PCM WEM with any channel count and speaker mask. */
    private static byte[] Extensible(ushort channels, uint mask, byte[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(4 + 8 + 24 + 8 + samples.Length); writer.Write("WAVEfmt "u8); writer.Write(24);
        writer.Write((ushort)0xFFFE); writer.Write(channels); writer.Write(48000); writer.Write(48000 * channels * 2);
        writer.Write((ushort)(channels * 2)); writer.Write((ushort)16); writer.Write((ushort)6); writer.Write((ushort)16);
        writer.Write(mask); writer.Write("data"u8); writer.Write(samples.Length); writer.Write(samples);
        return stream.ToArray();
    }

    private static byte[] Retag(byte[] wem, ushort tag)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(wem.AsSpan(20), tag);
        return wem;
    }

    /** Stereo Wwise Vorbis WEM with 256 and 2048 sample blocks: setup, then the packets. */
    private static byte[] Vorbis(byte[] setup, byte[] packets, int samples)
    {
        int dataSize = 2 + setup.Length + packets.Length;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(4 + 8 + 0x42 + 8 + dataSize); writer.Write("WAVEfmt "u8); writer.Write(0x42);
        writer.Write((ushort)0xFFFF); writer.Write((ushort)2); writer.Write(48000); writer.Write(0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0x30); writer.Write((ushort)0);
        writer.Write(3u); writer.Write(samples);                       // channel mask, sample count
        writer.Write(new byte[0x0C]); writer.Write(0u); writer.Write(2 + setup.Length);
        writer.Write(new byte[0x10]); writer.Write((byte)8); writer.Write((byte)11);
        writer.Write("data"u8); writer.Write(dataSize); writer.Write((ushort)setup.Length); writer.Write(setup);
        writer.Write(packets);
        return stream.ToArray();
    }

    private static void Refuse(byte[] wem, AudioUnsupportedReason reason, string message)
    {
        try { WemDecoder.Inspect(wem); }
        catch (AudioUnsupportedException error) when (error.Reason == reason && error.Message == message) { return; }
        throw new InvalidDataException($"Expected the refusal '{message}'.");
    }

    /**
     * Inspects and decodes damaged input; any exception but InvalidDataException fails the check.
     * @return True when it decoded, with exactly the stream's sample count.
     */
    private static bool Damaged(byte[] wem, bool allowSuccess = false)
    {
        AudioStreamInfo info;
        PcmWave wave;
        try
        {
            info = WemDecoder.Inspect(wem);
            wave = WemDecoder.Decode(wem, 2, CancellationToken.None);
        }
        catch (InvalidDataException) { return false; }
        if (!allowSuccess) throw new InvalidOperationException("Damaged Vorbis was accepted.");
        if (wave.Samples.Length != info.Samples * wave.Channels * 2)
            throw new InvalidOperationException("Decoded Vorbis does not have the stream's sample count.");
        return true;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
