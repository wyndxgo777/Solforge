using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks segment cues, inherited meters and beat/cue synchronization, so music transitions land on the exact frame the native engine would use. */
internal static class CueChecks
{
    /** Segment 10 inherits its meter from switch 100 through switch 90; a null meter leaves it unauthored. */
    public static AudioAnalysis Fixture(AudioMeter? meter) => new TestMusic()
        .Switch(100, 0, meter, TestMusic.Rule(sync: 2))
        .Switch(90, 100)
        .Segment(10, 90, 9000, [new(11, 1000), new(42, 2250, "Bridge"), new(43, 3375), new(42, 5750), new(12, 8500)], 1)
        .Track(1, 10, TestMusic.Clip(7, 9000))
        .Build();

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        var analysis = Fixture(new(137, 7, 8, 375.125, 61.5)); var source = analysis.Segments[10];
        Check(source.Exit == 8500 && source.Cues is { Count: 5 } && source.Cues[1].Name == "Bridge", "Custom cues replaced the exit or lost their names.");
        var inherited = AudioSynchronization.Meter(analysis, 10);
        Check(inherited.Owner == 100 && inherited.Meter.Frames() == new AudioMeterFrames(10511, 73577, 18006, 2952),
            "Inherited fractional meter did not retain per-beat rounding.");
        Check(AudioSynchronization.Resolve(analysis, 10, 3, 0).FrameFromEntry == 10511
            && AudioSynchronization.Resolve(analysis, 10, 3, 10511.0 / 48000).FrameFromEntry == 21022
            && AudioSynchronization.Resolve(analysis, 10, 3, 10511.0 / 48000, strict: false).FrameFromEntry == 10511,
            "Beat synchronization did not advance exact boundaries only in strict mode.");
        Check(AudioSynchronization.Resolve(analysis, 10, 1, 0).FrameFromEntry == 2952
            && AudioSynchronization.Resolve(analysis, 10, 1, 2952.0 / 48000).FrameFromEntry == 20958,
            "Grid offset or exact-boundary behavior changed.");
        Check(AudioSynchronization.Resolve(analysis, 10, 4, 1.25, 42).FrameFromEntry == 60000
            && AudioSynchronization.Resolve(analysis, 10, 5, 1.25, 42).FrameFromEntry == 228000,
            "Next cue and next custom cue lost their different equality rules.");
        Check(AudioSynchronization.Resolve(analysis, 10, 4, 0, 999).Cue == 12
            && AudioSynchronization.Resolve(analysis, 10, 6, 0, strict: false).Cue == 11,
            "Next-cue fallback or entry-cue identity changed.");
        Reject(() => AudioSynchronization.Resolve(analysis, 10, 5, 0, 999));
        Reject(() => AudioSynchronization.Resolve(analysis, 10, 6, 0));
        Reject(() => AudioSynchronization.Resolve(analysis, 10, 3, 7.5));
        Reject(() => AudioSynchronization.Resolve(analysis, 10, 7, 7.51));
        Reject(() => new AudioMeter(0, 4, 4, 1000, 0).Frames());
        Reject(() => new AudioMeter(120, 4, 4, 1000, -1).Frames());
        Reject(() => AudioSynchronization.Resolve(Fixture(null), 10, 2, 0));
        // The bank reader skips a segment whose markers run backwards and names it.
        (uint, double)[] ordered = [(11, 1000), (42, 2250), (43, 3375), (42, 5750), (12, 8500)];
        var read = AudioAnalysis.Read([TestHirc.Bank(113, 910, [(10, 10, SegmentBytes(90, 1, 9000, ordered))])]);
        Check(read.Diagnostics.Count == 0 && read.Segments[10] is { Entry: 1000, Exit: 8500, Cues.Count: 5 }, "Ordered markers were refused.");
        var backwards = AudioAnalysis.Read([TestHirc.Bank(113, 911, [(10, 10, SegmentBytes(90, 1, 9000, [(11, 3000), .. ordered[1..]]))])]);
        Check(!backwards.Segments.ContainsKey(10) && backwards.Diagnostics.Single().StartsWith("Music object 10 skipped", StringComparison.Ordinal),
            "A segment with backwards markers was read.");
        var plan = AudioTimeline.BuildTransition(analysis, 100, 10, 0, .2);
        Check(plan.Synchronization is { FrameFromEntry: 73577, MeterOwner: 100 }
            && Math.Abs(plan.DurationSeconds - (48000.0 + 73577) / 48000) < 1e-9, "Transition did not use the inherited bar boundary.");
        byte[] bytes = new byte[48000 * 9 * 4];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), 2000);
        var rendered = AudioTimeline.Render(plan, new Dictionary<uint, PcmWave> { [7] = new(48000, 2, 16, bytes) }, Path.Combine(directory, "bar.wav"));
        var pcm = PcmWave.Read(rendered.Path);
        Check(pcm.Samples.Length == (48000 + 73577) * 4 && BinaryPrimitives.ReadInt16LittleEndian(pcm.Samples.AsSpan(pcm.Samples.Length - 2)) == 2000,
            "Bar synchronization did not end at the exact PCM frame.");
        Console.WriteLine("audio_cues synthetic=pass meter=rounded_inherited markers=ordered boundary_rules=checked pcm_frames=exact");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output)
    {
        using var session = new AudioSession(catalog, 0x80BBEC39, cacheDirectory: Path.Combine(directory, "cue-cache"));
        await session.Analyze(); var analysis = session.Analysis!;
        var segment = analysis.Segments[300340342];
        Check(segment.Cues is { Count: 34 } && segment.Exit > 125800 && segment.Entry < 1,
            "Installed custom-cue segment was truncated at its first custom cue.");
        var meter = AudioSynchronization.Meter(analysis, segment.Id);
        Check(meter.Owner == 11054086 && meter.Meter is { Tempo: 93, BeatsPerBar: 6, BeatValue: 4 }, "Installed source meter inheritance changed.");
        var bar = await session.RenderTransition(528193615, 300340342, 383151144, 2);
        var plan = session.Timeline!;
        Check(plan.Synchronization is { FrameFromEntry: 185808, Kind: 2 }, "Installed bar transition lost rounded-beat timing.");
        Check(PcmWave.Read(bar.Path).Samples.Any(value => value != 0), "Installed bar transition rendered silence.");
        if (output is not null) Export(bar, plan, output);
        var cue = await session.RenderTransition(528193615, 365188325, 318839217, 6);
        var cuePlan = session.Timeline!;
        var expected = analysis.Segments[365188325].Cues!.Skip(1).First(value => value.PositionMs >= analysis.Segments[365188325].Entry + 6000);
        uint expectedFrame = (uint)Math.Round(expected.PositionMs * 48, MidpointRounding.AwayFromZero)
            - (uint)Math.Round(analysis.Segments[365188325].Entry * 48, MidpointRounding.AwayFromZero);
        Check(cuePlan.Synchronization is { Kind: 4 } && cuePlan.Synchronization.FrameFromEntry == expectedFrame,
            "Installed next-cue transition did not choose the actual custom marker.");
        Check(PcmWave.Read(cue.Path).Samples.Any(value => value != 0), "Installed custom-cue transition rendered silence.");
        Check(cuePlan.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 365188325, 122588189, 318839217 }),
            "The custom-cue rule lost its bridge.");
        VerifySamples(session, cue, cuePlan);
        if (output is not null) Export(cue, cuePlan, Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-custom.wav"));
        Console.WriteLine($"audio_cues owned=80BBEC39 markers=34 bar_frames={plan.Synchronization!.FrameFromEntry} cue_frames={expectedFrame} duration={cuePlan.DurationSeconds:F6} pcm_samples=exact");
        void Export(AudioTimelineRender render, AudioTimelinePlan timeline, string path)
        {
            AudioSession.ExportWave(render.Path, path, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(path, ".json"), System.Text.Json.JsonSerializer.Serialize(timeline,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /** Selected samples lie after clip fade-ins and before clip fade-outs, isolating the transition. */
    private static void VerifySamples(AudioSession session, AudioTimelineRender rendered, AudioTimelinePlan plan)
    {
        var wave = PcmWave.Read(rendered.Path);
        Check(wave.Samples.Length / 4 == (long)Math.Round(plan.DurationSeconds * 48000, MidpointRounding.AwayFromZero),
            "Cue transition output gained an extra terminal frame.");
        var sources = plan.Clips.Select(value => value.Media).Distinct().ToDictionary(id => id,
            id => WemDecoder.DecodeSamples(File.ReadAllBytes(session.MediaPath(id)), 2, CancellationToken.None));
        double sync = plan.Segments[1].EntrySeconds;
        foreach (double sampleTime in new[] { 1.0, sync + .001, sync + .05, sync + .1, sync + .151, sync + .3 })
        {
            int frame = (int)Math.Round(sampleTime * 48000);
            double time = frame / 48000.0;
            for (int channel = 0; channel < 2; channel++)
            {
                float sum = 0;
                foreach (var clip in plan.Clips)
                {
                    int start = (int)Math.Round(clip.StartSeconds * 48000), count = (int)Math.Round(clip.DurationSeconds * 48000);
                    if (frame < start || frame >= start + count) continue;
                    long clock = frame - start + (long)Math.Round(clip.AutomationOffsetSeconds * 48000);
                    foreach (var lane in clip.Automation ?? [])
                        Check(new AudioAutomationCurve(lane).GainAt(clock) == 1, "Owned sample no longer isolates the transition envelope.");
                    var source = sources[clip.Media]; Check(source.Channels is 1 or 2, "Owned source channel layout changed.");
                    // The source offset is a whole output sample.
                    long step = (long)Math.Round(clip.SourceSeconds * 48000, MidpointRounding.AwayFromZero) + frame - start;
                    double position = step * (double)source.SampleRate / 48000;
                    int a = (int)Math.Floor(position); double fraction = position - a;
                    int inputChannel = Math.Min(channel, source.Channels - 1);
                    float first = source.Samples[a * source.Channels + inputChannel];
                    float next = source.Samples[(a + 1) * source.Channels + inputChannel];
                    double envelope = 1;
                    if (clip.FadeOut is { } fade)
                    {
                        Check(fade.Curve == 4, "Owned sample needs a linear transition fade.");
                        envelope = 1 - Math.Clamp((time - fade.StartSeconds) / fade.DurationSeconds, 0, 1);
                    }
                    double centre = TestMusic.ChannelGain(clip, source.Channels, channel);
                    sum += (float)((first + (next - first) * fraction) * Math.Pow(10, clip.GainDb / 20.0) * centre * envelope);
                }
                int expected = (int)Math.Clamp(Math.Round(sum), short.MinValue, short.MaxValue);
                int actual = BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan(frame * 4 + channel * 2));
                Check(Math.Abs(expected - actual) <= 1, "Owned cue-transition PCM differs from independently summed source and bridge samples.");
            }
        }
    }

    /** A version-113 segment with one child and no meter; markers keep the given order and have no names. */
    private static byte[] SegmentBytes(uint parent, uint track, double durationMs, (uint Id, double PositionMs)[] markers)
    {
        var bytes = new HircBytes().U8(0).Raw(TestHirc.Node(0, parent)).Raw(TestHirc.Ids(track))
            .F64(0).F64(0).F32(120).U8(4).U8(4).U8(0).U32(0).F64(durationMs).U32((uint)markers.Length);
        foreach (var (id, position) in markers) bytes.U32(id).F64(position).U32(0);
        return bytes.ToArray();
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidDataException(error); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return; } throw new Exception("Invalid music synchronization was accepted."); }
}
