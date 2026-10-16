using System.Buffers.Binary;
using SunriseStudio.Audio;
using SunriseStudio.Packages;

/** Checks that installed music transitions (initial fades, bridges, source tails) render with the authored fade curves and intervals. */
internal static class TransitionChecks
{
    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static async Task Owned(PackageCatalog catalog, string directory, string? output)
    {
        using var startSession = new AudioSession(catalog, 0x80B4F00D, cacheDirectory: Path.Combine(directory, "start-cache"));
        var start = await startSession.RenderTransition(53750154, 0, 93887718, 0);
        var startPlan = startSession.Timeline!;
        var startClip = startPlan.Clips.Single();
        Check(startClip.FadeIn is { StartSeconds: 2, DurationSeconds: 8, Curve: 4 } && startPlan.DurationSeconds == 46,
            "Installed initial transition did not retain its entry-relative eight-second fade.");
        VerifyDecodedEnvelope(startSession, start, [2, 4, 6, 9, 10, 20]);
        var bridge = await startSession.RenderTransition(53750154, 128209166, 93887718, 8);
        var bridgePlan = startSession.Timeline!;
        Check(bridgePlan.Segments.Select(value => value.Id).SequenceEqual(new uint[] { 128209166, 613394032, 93887718 })
            && bridgePlan.Segments[0].EndSeconds == 16 && bridgePlan.Segments[1].EntrySeconds == 8
            && bridgePlan.Segments[1].ExitSeconds == 15 && bridgePlan.Segments[2].EntrySeconds == 15,
            "Installed bridge did not preserve its seven-second interval or eight-second Exp1 source fade.");
        Check(bridgePlan.Clips.First().FadeOut is { StartSeconds: 8, DurationSeconds: 8, Curve: 6 }
            && bridgePlan.Clips.Single(value => value.Segment == 613394032).LoopSeconds == 1,
            "Installed bridge lost its fade curve or extended source.");
        var bridgePcm = PcmWave.Read(bridge.Path);
        Check(bridgePcm.Samples.Any(value => value != 0) && bridgePcm.DurationSeconds == 59,
            "Installed transition did not render the destination and source tail.");
        if (output is not null)
        {
            string bridgeOutput = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-bridge.wav");
            AudioSession.ExportWave(bridge.Path, bridgeOutput, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(bridgeOutput, ".json"), System.Text.Json.JsonSerializer.Serialize(bridgePlan,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        using var stopSession = new AudioSession(catalog, 0x80B4F065, cacheDirectory: Path.Combine(directory, "stop-cache"));
        var stop = await stopSession.RenderTransition(856418905, 539274149, 0, 8);
        var stopPlan = stopSession.Timeline!;
        Check(stopPlan.Clips.Single().FadeOut is { StartSeconds: 8, DurationSeconds: 5, Curve: 4 } && stopPlan.DurationSeconds == 13,
            "Installed immediate transition did not stop at its authored fade endpoint.");
        VerifyDecodedEnvelope(stopSession, stop, [0, 7, 8, 10.5, 12.5]);
        if (output is not null)
        {
            AudioSession.ExportWave(stop.Path, output, catalog.GameRoot);
            File.WriteAllText(Path.ChangeExtension(output, ".json"), System.Text.Json.JsonSerializer.Serialize(stopPlan,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("audio_transition owned=80B4F00D,80B4F065 entry_fade=8s immediate_stop=5s decoded_samples=exact exp1_bridge=59s");
    }

    private static void VerifyDecodedEnvelope(AudioSession session, AudioTimelineRender rendered, double[] times)
    {
        var clip = session.Timeline!.Clips.Single();
        var source = WemDecoder.DecodeSamples(File.ReadAllBytes(session.MediaPath(clip.Media)), 2, CancellationToken.None);
        var result = PcmWave.Read(rendered.Path);
        Check(source.SampleRate == 48000 && source.Channels == 2 && result.Samples.Any(value => value != 0), "Installed transition source format changed.");
        double gain = Math.Pow(10, clip.GainDb / 20.0);
        foreach (double time in times)
        {
            double factor = clip.FadeIn is { } fadeIn ? Math.Clamp((time - fadeIn.StartSeconds) / fadeIn.DurationSeconds, 0, 1)
                : 1 - Math.Clamp((time - clip.FadeOut!.StartSeconds) / clip.FadeOut.DurationSeconds, 0, 1);
            for (int channel = 0; channel < 2; channel++)
            {
                double sourceTime = clip.SourceSeconds + time - clip.StartSeconds;
                float sample = source.Samples[(int)Math.Round(sourceTime * 48000) * 2 + channel];
                double side = TestMusic.ChannelGain(clip, source.Channels, channel);
                int expected = (int)Math.Clamp(Math.Round((float)(sample * gain * side * factor)), short.MinValue, short.MaxValue);
                int actual = BinaryPrimitives.ReadInt16LittleEndian(result.Samples.AsSpan((int)Math.Round(time * 48000) * 4 + channel * 2));
                Check(Math.Abs(expected - actual) <= 1, "Installed transition differs from the independently evaluated PCM envelope.");
            }
        }
    }

    /** A bridged rule between two child switches; the source rule syncs immediately or on the exit cue. */
    private static AudioAnalysis Fixture(int sync) => new TestMusic()
        .Switch(100, 0, null, TestMusic.Rule(111, 222, sync, 0, 2000, 4, 1000, true, 1000, 4, 0, false,
            bridge: new(30, 500, 4, -500, 500, 4, 500, true, true)))
        .Switch(111, 100).Switch(222, 100)
        .Segment(10, 111, 6000, [new(0, 1000), new(0, 4000)], 1).Track(1, 10, TestMusic.Clip(11, 6000))
        .Segment(20, 222, 5000, [new(0, 1000), new(0, 4000)], 2).Track(2, 20, TestMusic.Clip(22, 5000))
        .Segment(30, 100, 3000, [new(0, 500), new(0, 2500)], 3).Track(3, 30, TestMusic.Clip(33, 1000, endTrimMs: 2000))
        .Build();

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string directory)
    {
        var analysis = Fixture(0);
        var parsed = analysis.TransitionContainers[100].Rules.Single();
        Check(parsed.Bridge is { FadeInMs: 500, FadeOutOffsetMs: 500, PreEntry: true, PostExit: true }, "Bridge fade settings were discarded.");
        var plan = AudioTimeline.BuildTransition(analysis, 100, 10, 20, 2);
        Check(plan.Segments.Select(segment => segment.Id).SequenceEqual(new uint[] { 10, 30, 20 }) && plan.DurationSeconds == 10,
            "Bridge entry/exit cues did not separate the source and destination.");
        Check(plan.Segments[0].EndSeconds == 5 && plan.Segments[1].StartSeconds == 3.5 && plan.Segments[1].EndSeconds == 6.5
            && plan.Segments[2].StartSeconds == 6, "Authored lead-in, pre-entry, post-exit or fade offsets were ignored.");
        var result = AudioTimeline.Render(plan, new Dictionary<uint, PcmWave> { [11] = Constant(6, 1000), [22] = Constant(5, 2000), [33] = Constant(1, 4000) },
            Path.Combine(directory, "transition.wav"));
        var wave = PcmWave.Read(result.Path);
        Check(Sample(wave, 2) == 1000 && Sample(wave, 3.75) == 2625 && Sample(wave, 4.5) == 4250
            && Sample(wave, 5.5) == 4000 && Sample(wave, 6.25) == 2500 && Sample(wave, 7) == 2000,
            "Authored transition envelopes did not produce the exact expected PCM samples.");
        Check(result.ClippedSamples == 0 && plan.Clips.Single(clip => clip.Media == 33).LoopSeconds == 1,
            "Bridge source extension did not retain its source loop duration.");
        Reject(() => AudioTimeline.BuildTransition(analysis, 100, 10, 20, double.NaN));
        Check(AudioCurves.Interpolate(6, .5, true) == .375 && AudioCurves.Interpolate(6, .5, false) == .375
            && AudioCurves.Interpolate(2, .5, true) == .625 && AudioCurves.Interpolate(2, .5, false) == .625,
            "Authored curve mirroring did not retain the native Exp1/Log1 midpoint.");
        var curvedClip = new AudioTimelineClip(1, 1, 1, 0, 0, 3, 0, FadeOut: new(.5, 2, 6, false));
        var curved = AudioTimeline.Render(new(1, [], [curvedClip], 3, []), new Dictionary<uint, PcmWave> { [1] = Constant(3, 16000) },
            Path.Combine(directory, "exp1.wav"));
        var curvedPcm = PcmWave.Read(curved.Path);
        Check(Sample(curvedPcm, .5) == 16000 && Sample(curvedPcm, 1) == 10500 && Sample(curvedPcm, 1.5) == 6000
            && Sample(curvedPcm, 2) == 2500 && Sample(curvedPcm, 2.5) == 0,
            "Exp1 fade did not retain the native mirrored PCM envelope.");
        Reject(() => AudioCurves.Interpolate(9, .5, true));
        Reject(() => AudioTimeline.BuildTransition(analysis, 111, 10, 20, 1));
        Reject(() => AudioTimeline.BuildTransition(analysis, 100, 10, 20, 4));
        var exitPlan = AudioTimeline.BuildTransition(Fixture(7), 100, 10, 20, 1);
        Check(exitPlan.Segments[1].EntrySeconds == 4, "Exit synchronization did not wait for the source exit cue.");
        Console.WriteLine("audio_transition synthetic=pass bridge=checked fades=sample-exact offsets=checked source_loop=checked");
    }

    private static PcmWave Constant(int seconds, short value)
    {
        var bytes = new byte[seconds * 48000 * 4];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value);
        return new(48000, 2, 16, bytes);
    }
    private static short Sample(PcmWave wave, double seconds) => BinaryPrimitives.ReadInt16LittleEndian(wave.Samples.AsSpan((int)(seconds * 48000) * 4));
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidDataException(error); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException) { return; } throw new Exception("Unsupported transition was accepted."); }
}
