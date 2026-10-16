using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Engine rules that the reference tools render differently. */
internal static class ParityChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        // A stereo ramp: source frame i holds 100 * i on both channels.
        var bytes = new byte[30 * 4];
        for (int i = 0; i < 60; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(100 * (i / 2)));
        var sources = new Dictionary<uint, PcmWave> { [1] = new(48000, 2, 16, bytes) };
        const double Sample = 1.0 / 48000;

        // 10.6 samples into the source rounds to sample 11, so no output frame is interpolated.
        var offset = new AudioTimelinePlan(0, [], [new(0, 0, 1, 0, 10.6 * Sample, 5 * Sample, 0)], 5 * Sample, []);
        var shifted = Render(offset, sources, directory, "offset");
        Check(Enumerable.Range(0, 5).All(frame => shifted[frame] == 100 * (11 + frame)), "A clip source offset was not rounded to a whole sample.");

        // A 20.6-sample loop rounds to 21 samples, so frame 21 restarts at source sample 0.
        var loop = new AudioTimelinePlan(0, [], [new(0, 0, 1, 0, 0, 25 * Sample, 0, LoopSeconds: 20.6 * Sample)], 25 * Sample, []);
        var looped = Render(loop, sources, directory, "loop");
        Check(looped[20] == 2000 && looped[21] == 0 && looped[22] == 100, "A clip loop length was not rounded to a whole sample.");

        // An endless continuous random item plays every child once per preview loop.
        var endless = Random(0);
        var pass = AudioTimeline.Build(endless, 30, loopIterations: 1);
        Check(pass.Segments.Select(value => value.Id).Order().SequenceEqual(new uint[] { 21, 22, 23 }) && pass.PlaylistPath!.Truncated,
            "A continuous random loop did not play every child once.");
        var twice = AudioTimeline.Build(endless, 30, loopIterations: 2).Segments.Select(value => value.Id).ToArray();
        Check(twice.Length == 6 && twice.GroupBy(id => id).All(group => group.Count() == 2), "Each continuous random loop was not a full pass.");

        // A finite loop count is a count of full passes, not a refusal.
        var finite = AudioTimeline.Build(Random(2), 30).Segments.Select(value => value.Id).ToArray();
        Check(finite.Length == 6 && finite.GroupBy(id => id).All(group => group.Count() == 2), "Finite continuous random loops were refused or cut short.");

        // A source past full scale keeps its value until the mix: 40000 at half gain is 20000, not 16384.
        var loud = new Dictionary<uint, AudioSamples> { [2] = new(48000, 2, Enumerable.Repeat(40000f, 20).ToArray()) };
        var half = new AudioTimelinePlan(0, [], [new(0, 0, 2, 0, 0, 10 * Sample, (float)(20 * Math.Log10(.5)))], 10 * Sample, []);
        var unclipped = Read(AudioTimeline.Render(half, loud, Path.Combine(directory, "parity-unclipped.wav")).Path);
        Check(unclipped.All(value => Math.Abs(value - 20000) <= 1), "A source was clipped before its gain.");

        // A mono source reaches both stereo channels at the constant-power centre, sqrt(1/2).
        var mono = new Dictionary<uint, AudioSamples> { [3] = new(48000, 1, Enumerable.Repeat(10000f, 10).ToArray()) };
        var centred = new AudioTimelinePlan(0, [], [new(0, 0, 3, 0, 0, 10 * Sample, 0)], 10 * Sample, []);
        var both = PcmWave.Read(AudioTimeline.Render(centred, mono, Path.Combine(directory, "parity-mono.wav")).Path);
        Check(Enumerable.Range(0, 20).All(i => BinaryPrimitives.ReadInt16LittleEndian(both.Samples.AsSpan(i * 2)) == 7071),
            "A mono source was not placed at -3 dB on each channel.");
        // A music track's pan reaches its clips: x = 0 plays the left channel only.
        var music = new TestMusic().Switch(300, 0, null, TestMusic.Rule(sync: 7))
            .Playlist(30, 300, TestMusic.Item(100, 0, 0, 1, TestMusic.Item(101, 21, -1)), TestMusic.Rule(sync: 7));
        music.Segment(21, 30, 1000, [new(0, 0), new(0, 1000)], 121).Track(121, 21, TestMusic.Clip(21, 1000));
        var analysis = music.Build(); var tracks = (Dictionary<uint, AudioTrack>)analysis.Tracks;
        tracks[121] = tracks[121] with { Pan = 0f };
        var musicPlan = AudioTimeline.Build(analysis, 30, 1);
        var stereo = new byte[48000 * 4];
        for (int i = 0; i < stereo.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(stereo.AsSpan(i), 1000);
        var pannedMusic = PcmWave.Read(AudioTimeline.Render(musicPlan, new Dictionary<uint, PcmWave> { [21] = new(48000, 2, 16, stereo) },
            Path.Combine(directory, "parity-music-pan.wav")).Path);
        Check(musicPlan.Clips.All(clip => clip.Pan == 0f) && BinaryPrimitives.ReadInt16LittleEndian(pannedMusic.Samples.AsSpan(400)) == 1000
            && BinaryPrimitives.ReadInt16LittleEndian(pannedMusic.Samples.AsSpan(402)) == 0, "A music track's pan did not reach its clips.");
        Console.WriteLine("audio_parity synthetic=pass source_offset=whole_sample loop_length=whole_sample random_continuous=full_pass unclipped_gain=checked mono_centre=-3dB music_pan=checked");
    }

    /** Event 81001BCF plays a Vorbis source past full scale at -18.5 dB; 16-bit clipping first would cap it at 3895. */
    public static async Task Owned(PackageCatalog catalog, string directory)
    {
        using var session = new AudioSession(catalog, 0x81001BCF, cacheDirectory: Path.Combine(directory, "parity-cache"));
        await session.GenerateCompositions();
        var mix = PcmWave.Read(await session.DecodeComposition(0));
        int peak = Enumerable.Range(0, mix.Samples.Length / 2).Max(i => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(mix.Samples.AsSpan(i * 2))));
        Check(peak > 4000, $"A source past full scale was clipped before its gain: peak {peak}.");
        Console.WriteLine($"audio_parity owned=81001BCF unclipped_peak={peak}");
    }

    /** A continuous random root over segments 21 to 23 of one second each. */
    private static AudioAnalysis Random(int loops)
    {
        var root = TestMusic.Item(100, 0, 2, loops, TestMusic.Item(101, 21, -1), TestMusic.Item(102, 22, -1), TestMusic.Item(103, 23, -1));
        var music = new TestMusic().Switch(300, 0, null, TestMusic.Rule(sync: 7)).Playlist(30, 300, root, TestMusic.Rule(sync: 7));
        for (uint id = 21; id <= 23; id++)
            music.Segment(id, 30, 1000, [new(0, 0), new(0, 1000)], id + 100).Track(id + 100, id, TestMusic.Clip(id, 1000));
        return music.Build();
    }

    private static short[] Render(AudioTimelinePlan plan, IReadOnlyDictionary<uint, PcmWave> sources, string directory, string name) =>
        Read(AudioTimeline.Render(plan, sources, Path.Combine(directory, "parity-" + name + ".wav")).Path);

    /** The first channel of each frame. */
    private static short[] Read(string path)
    {
        var wave = PcmWave.Read(path);
        return Enumerable.Range(0, wave.Samples.Length / wave.BlockAlign)
            .Select(frame => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(frame * wave.BlockAlign))).ToArray();
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
